using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Pgvector;
using MultiTenantRAGagnets.Core.Audit;
using MultiTenantRAGagnets.Core.Ingestion;

namespace MultiTenantRAGagnets.Core.Retrieval;

/// <summary>
/// UNVERIFIED against a real database (no Postgres was available when written). Compiles; the first run
/// against pgvector/pgvector:pg16 with db/migrations applied is the actual test.
///
/// Implements db/README.md "Per-request pattern": one transaction per request on an app_user connection,
/// tenant/role applied with set_config(..., true) (== SET LOCAL, bind-parameter friendly), so nothing
/// outlives COMMIT/ROLLBACK and a pooled connection cannot leak it. Npgsql additionally resets the
/// connection on return to the pool (DISCARD ALL equivalent) unless "No Reset On Close" is set, which
/// <see cref="NpgsqlDataSourceFactory"/> refuses.
/// </summary>
public sealed class NpgsqlChunkStore : IChunkStore
{
    private readonly NpgsqlDataSource _dataSource;
    private int _roleChecked; // 0 = not yet, 1 = verified
    private readonly string _iterativeScan;
    private readonly ILogger _logger;
    private long _indexFallbackCount;

    /// <summary>
    /// How many searches on THIS store instance fell back to the exact scan. Same events as the process-wide
    /// <c>retrieval.index_fallback</c> counter, but scoped to the instance so a test or a results writer can read it.
    /// </summary>
    public long IndexFallbackCount => Interlocked.Read(ref _indexFallbackCount);

    /// <param name="dataSource">Built by <see cref="NpgsqlDataSourceFactory"/> (pgvector mapping enabled).</param>
    /// <param name="logger">Optional; the fallback warning goes here. Silent (null logger) when omitted.</param>
    public NpgsqlChunkStore(NpgsqlDataSource dataSource, ILogger<NpgsqlChunkStore>? logger = null)
        : this(dataSource, "strict_order", logger)
    {
    }

    /// <summary>
    /// TEST SEAM (internal): lets the tests starve the index path deterministically (iterative scan 'off') so the
    /// exact-scan fallback in <c>Session.SearchAsync</c> can be proven. Production always uses 'strict_order'.
    /// </summary>
    internal NpgsqlChunkStore(NpgsqlDataSource dataSource, string iterativeScan, ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _iterativeScan = iterativeScan is "off" or "strict_order" or "relaxed_order"
            ? iterativeScan
            : throw new ArgumentOutOfRangeException(nameof(iterativeScan));
    }

    public async Task<IChunkStoreSession> BeginAsync(CallerContext caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Validate();

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        NpgsqlTransaction? transaction = null;
        try
        {
            await EnsureLockedDownRoleAsync(connection, cancellationToken).ConfigureAwait(false);
            transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            // Bind parameters, never string-built SQL. The role is mapped to its level by the DB function,
            // so the hierarchy lives in exactly one place; an unknown role gives '' which fails closed.
            await using (var cmd = new NpgsqlCommand(
                """
                SELECT set_config('app.tenant_id',  @tenant, true),
                       set_config('app.role_level', coalesce(rag_role_level(@role)::text, ''), true)
                """, connection, transaction))
            {
                cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Text, caller.TenantId.ToString("D"));
                cmd.Parameters.AddWithValue("role", NpgsqlDbType.Text, caller.Role);
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Also the role default (002_rls.sql); belt and braces for TRD 7.3 (iterative index scan).
            await using (var cmd = new NpgsqlCommand("SELECT set_config('hnsw.iterative_scan', @mode, true)", connection, transaction))
            {
                cmd.Parameters.AddWithValue("mode", NpgsqlDbType.Text, _iterativeScan);
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return new Session(connection, transaction, caller, this);
        }
        catch
        {
            if (transaction is not null) await transaction.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>TRD 7.1: refuse to serve requests on a superuser / BYPASSRLS connection (RLS would be a no-op). Checked once.</summary>
    private async Task EnsureLockedDownRoleAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        if (Volatile.Read(ref _roleChecked) == 1) return;

        await using var cmd = new NpgsqlCommand(
            "SELECT rolsuper OR rolbypassrls FROM pg_roles WHERE rolname = current_user", connection);
        var privileged = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (privileged is not bool isPrivileged || isPrivileged)
            throw new InvalidOperationException(
                "The request-path connection must use the locked-down app_user role (NOSUPERUSER, NOBYPASSRLS). " +
                "A privileged role ignores row level security. Check ConnectionStrings:App.");
        Volatile.Write(ref _roleChecked, 1);
    }

    private sealed class Session : IChunkStoreSession
    {
        private readonly NpgsqlConnection _connection;
        private readonly NpgsqlTransaction _transaction;
        private readonly CallerContext _caller;
        private readonly NpgsqlChunkStore _store;
        private bool _committed;

        public Session(NpgsqlConnection connection, NpgsqlTransaction transaction, CallerContext caller, NpgsqlChunkStore store)
        {
            _store = store;
            _connection = connection;
            _transaction = transaction;
            _caller = caller;
        }

        public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
            float[] queryEmbedding, int k, RetrievalFilterMode mode, RankingPreference ranking, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(queryEmbedding);
            ArgumentNullException.ThrowIfNull(ranking);
            if (k < 1) throw new ArgumentOutOfRangeException(nameof(k));
            ranking.Validate();

            // With the current-edition preference the nearest candidates are over-fetched (k * multiplier) through the
            // same permitted-only predicate and index path, then re-ordered in SQL and cut back to k. The pool is the
            // caller's permitted chunks either way; the preference only changes the order inside it.
            var filtered = mode == RetrievalFilterMode.AppAndRls;
            var results = await RunSearchAsync(filtered, queryEmbedding, k, ranking, cancellationToken).ConfigureAwait(false);

            // The under-return guard is unchanged and still keyed on k, not on the pool size: the re-ranked result is
            // min(k, pool), so "fewer than k" still means "the index path under-filled" and takes the exact scan.
            if (results.Count >= k) return results;

            // Under-fill. Either the caller really can see fewer than k chunks (then the exact scan below returns the
            // same rows, at the cost of one extra query), or the approximate HNSW path missed rows it should have
            // found. The second case is real and silent: an HNSW graph can leave rows unreachable (measured in CI:
            // about 5 percent of a 2665-row table, all 36 rows of one tenant, deterministic per index build), and an
            // iterative scan only walks reachable nodes, so it returns 0 of k even though 15 chunks are visible.
            // An exact scan (index scans off, RLS and the tenant filter unchanged) cannot miss. It is only taken
            // when the approximate path under-filled, so the common case keeps the index latency.
            string previous;
            await using (var cmd = new NpgsqlCommand("SELECT current_setting('enable_indexscan')", _connection, _transaction))
                previous = (string)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            await SetIndexScanAsync("off", cancellationToken).ConfigureAwait(false);
            try
            {
                var exact = await RunSearchAsync(filtered, queryEmbedding, k, ranking, cancellationToken).ConfigureAwait(false);

                // Observable by design: a silent fallback hides a degraded index. Counts and ids only; never the
                // query text, the embedding or any chunk text. TenantId is a GUID (the same value is in audit_log).
                Interlocked.Increment(ref _store._indexFallbackCount);
                RetrievalMetrics.RecordIndexFallback();
                _store._logger.LogWarning(
                    "Retrieval index fallback: index path under-filled, exact scan used. TenantId={TenantId} Mode={Mode} RequestedK={RequestedK} IndexRows={IndexRows} ExactRows={ExactRows}",
                    _caller.TenantId, mode, k, results.Count, exact.Count);
                return exact;
            }
            finally
            {
                await SetIndexScanAsync(previous, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task SetIndexScanAsync(string value, CancellationToken cancellationToken)
        {
            await using var cmd = new NpgsqlCommand("SELECT set_config('enable_indexscan', @v, true)", _connection, _transaction);
            cmd.Parameters.AddWithValue("v", NpgsqlDbType.Text, value);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<List<RetrievedChunk>> RunSearchAsync(
            bool filtered, float[] queryEmbedding, int k, RankingPreference ranking, CancellationToken cancellationToken)
        {
            var prefer = ranking.PreferCurrentEdition;
            var sql = prefer
                ? (filtered ? RetrievalSql.FilteredSearchPreferCurrent : RetrievalSql.RlsOnlySearchPreferCurrent)
                : (filtered ? RetrievalSql.FilteredSearch : RetrievalSql.RlsOnlySearch);
            await using var cmd = new NpgsqlCommand(sql, _connection, _transaction);
            cmd.Parameters.AddWithValue("q", new Vector(queryEmbedding));
            cmd.Parameters.AddWithValue("k", NpgsqlDbType.Integer, k);
            if (prefer)
            {
                cmd.Parameters.AddWithValue("n", NpgsqlDbType.Integer, ranking.CandidateCount(k));
                cmd.Parameters.AddWithValue("penalty", NpgsqlDbType.Double, ranking.SupersededPenalty);
            }
            if (filtered)
            {
                cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, _caller.TenantId);
                cmd.Parameters.AddWithValue("role", NpgsqlDbType.Text, _caller.Role);
            }

            var results = new List<RetrievedChunk>(k);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(new RetrievedChunk(
                    reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3), reader.GetDouble(4),
                    Superseded: prefer && reader.GetBoolean(5)));
            }
            return results;
        }

        public async Task AppendAuditAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entry);
            entry.Validate();

            // query_text is intentionally not in the column list: only the keyed hash is stored.
            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO audit_log
                    (tenant_id, user_id, role, query_hash, retrieved_chunk_ids, sent_chunk_ids, model, latency_ms)
                VALUES (@tenant, @user, @role, @hash, @retrieved, @sent, @model, @latency)
                """, _connection, _transaction);
            cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, entry.TenantId);
            cmd.Parameters.AddWithValue("user", NpgsqlDbType.Uuid, entry.UserId);
            cmd.Parameters.AddWithValue("role", NpgsqlDbType.Text, entry.Role);
            cmd.Parameters.AddWithValue("hash", NpgsqlDbType.Text, entry.QueryHash);
            cmd.Parameters.AddWithValue("retrieved", NpgsqlDbType.Array | NpgsqlDbType.Uuid, entry.RetrievedChunkIds.ToArray());
            cmd.Parameters.AddWithValue("sent", NpgsqlDbType.Array | NpgsqlDbType.Uuid, entry.SentChunkIds.ToArray());
            cmd.Parameters.AddWithValue("model", NpgsqlDbType.Text, entry.Model);
            cmd.Parameters.AddWithValue("latency", NpgsqlDbType.Integer, entry.LatencyMs);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<AuditRecord>> ListAuditAsync(int limit, CancellationToken cancellationToken = default)
        {
            if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));

            // RLS already scopes audit_log to the caller's tenant; the explicit predicate keeps the plan
            // selective and makes the intent obvious. query_text is not selected.
            await using var cmd = new NpgsqlCommand(
                """
                SELECT id, occurred_at, tenant_id, user_id, role, query_hash,
                       retrieved_chunk_ids, sent_chunk_ids, model, latency_ms
                FROM audit_log
                WHERE tenant_id = @tenant
                ORDER BY occurred_at DESC, id DESC
                LIMIT @limit
                """, _connection, _transaction);
            cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, _caller.TenantId);
            cmd.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, limit);

            var rows = new List<AuditRecord>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new AuditRecord(
                    reader.GetInt64(0),
                    reader.GetFieldValue<DateTimeOffset>(1),
                    reader.GetGuid(2),
                    reader.GetGuid(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetFieldValue<Guid[]>(6),
                    reader.GetFieldValue<Guid[]>(7),
                    reader.GetString(8),
                    reader.GetInt32(9)));
            }
            return rows;
        }

        public async Task StoreDocumentAsync(DocumentRecord document, IReadOnlyList<ChunkRecord> chunks, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentNullException.ThrowIfNull(chunks);

            // Upsert the document (ACL lives here, never on chunks). RLS WITH CHECK pins tenant_id to the
            // caller's and required_level to <= the caller's level.
            await using (var cmd = new NpgsqlCommand(
                """
                INSERT INTO documents (id, tenant_id, title, required_level, version, superseded_at)
                VALUES (@id, @tenant, @title, @level, @version, CASE WHEN @superseded THEN now() END)
                ON CONFLICT (id) DO UPDATE
                   SET title = EXCLUDED.title,
                       required_level = EXCLUDED.required_level,
                       version = EXCLUDED.version,
                       -- keep the original withdrawal time while the document stays superseded; re-ingest without the
                       -- flag makes it current again (replace semantics, same as the chunks)
                       superseded_at = CASE WHEN @superseded THEN coalesce(documents.superseded_at, now()) END,
                       updated_at = now()
                """, _connection, _transaction))
            {
                cmd.Parameters.AddWithValue("superseded", NpgsqlDbType.Boolean, document.Superseded);
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, document.Id);
                cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, document.TenantId);
                cmd.Parameters.AddWithValue("title", NpgsqlDbType.Text, document.Title);
                cmd.Parameters.AddWithValue("level", NpgsqlDbType.Smallint, (short)document.RequiredLevel);
                cmd.Parameters.AddWithValue("version", NpgsqlDbType.Integer, document.Version);
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Replace semantics: the document has one current version, so its previous chunks go.
            await using (var cmd = new NpgsqlCommand("DELETE FROM chunks WHERE doc_id = @id", _connection, _transaction))
            {
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, document.Id);
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var chunk in chunks)
            {
                await using var cmd = new NpgsqlCommand(
                    """
                    INSERT INTO chunks (id, doc_id, tenant_id, chunk_index, text, embedding, version)
                    VALUES (@id, @doc, @tenant, @idx, @text, @emb, @version)
                    """, _connection, _transaction);
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, chunk.Id);
                cmd.Parameters.AddWithValue("doc", NpgsqlDbType.Uuid, chunk.DocId);
                cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, chunk.TenantId);
                cmd.Parameters.AddWithValue("idx", NpgsqlDbType.Integer, chunk.ChunkIndex);
                cmd.Parameters.AddWithValue("text", NpgsqlDbType.Text, chunk.Text);
                cmd.Parameters.AddWithValue("emb", new Vector(chunk.Embedding));
                cmd.Parameters.AddWithValue("version", NpgsqlDbType.Integer, chunk.Version);
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_committed)
            {
                try { await _transaction.RollbackAsync().ConfigureAwait(false); }
                catch { /* connection already broken; disposing it below discards it from the pool */ }
            }
            await _transaction.DisposeAsync().ConfigureAwait(false);
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>Builds the pgvector-aware data source for the request path, with the pool-reset guard.</summary>
public static class NpgsqlDataSourceFactory
{
    public static NpgsqlDataSource Create(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("ConnectionStrings:App is not configured.");

        var csb = new NpgsqlConnectionStringBuilder(connectionString);
        if (csb.NoResetOnClose)
            throw new InvalidOperationException(
                "'No Reset On Close' must not be enabled: connection reset on return to the pool is the second line of defence after SET LOCAL (TRD 7.2).");

        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseVector();
        return builder.Build();
    }
}

/// <summary>The two vector-search statements. Internal so a unit test can pin them to the shared SQL functions.</summary>
internal static class RetrievalSql
{
    // The app-layer query from db/README.md. rag_level_allows is the SAME function the RLS policy calls;
    // rag_role_level maps the JWT role to a level server-side (NULL for unknown => no rows).
    public const string FilteredSearch = """
        SELECT c.id, c.doc_id, c.tenant_id, c.text, (c.embedding <=> @q)::float8 AS distance
        FROM chunks c
        JOIN documents d ON d.id = c.doc_id AND d.tenant_id = c.tenant_id
        WHERE c.tenant_id = @tenant
          AND rag_level_allows(rag_role_level(@role), d.required_level)
        ORDER BY c.embedding <=> @q
        LIMIT @k
        """;

    // Current-edition preference (RankingPreference). The inner query is EXACTLY FilteredSearch with LIMIT @n instead of
    // LIMIT @k: same join, same tenant + role predicate, same ORDER BY on the bare distance, so the HNSW index path and
    // the iterative scan are used as before and the candidate pool is permitted-only. The outer query only orders and
    // truncates that pool; it reads no table, so it cannot add a row. Superseded gets +@penalty on its distance
    // (>= 2 is strict); ties prefer current, then the nearer, then id for a deterministic order.
    public const string FilteredSearchPreferCurrent = """
        SELECT cand.id, cand.doc_id, cand.tenant_id, cand.text, cand.distance, cand.superseded
        FROM (
            SELECT c.id, c.doc_id, c.tenant_id, c.text,
                   (c.embedding <=> @q)::float8 AS distance,
                   (d.superseded_at IS NOT NULL) AS superseded
            FROM chunks c
            JOIN documents d ON d.id = c.doc_id AND d.tenant_id = c.tenant_id
            WHERE c.tenant_id = @tenant
              AND rag_level_allows(rag_role_level(@role), d.required_level)
            ORDER BY c.embedding <=> @q
            LIMIT @n
        ) cand
        ORDER BY cand.distance + CASE WHEN cand.superseded THEN @penalty::float8 ELSE 0::float8 END,
                 cand.superseded, cand.distance, cand.id
        LIMIT @k
        """;

    // TEST-ONLY twin of RlsOnlySearch for the preference: no tenant or role predicate in the candidate query, RLS is the
    // only wall. The superseded flag is a scalar lookup that cannot filter a row (missing/hidden => false).
    public const string RlsOnlySearchPreferCurrent = """
        SELECT cand.id, cand.doc_id, cand.tenant_id, cand.text, cand.distance, cand.superseded
        FROM (
            SELECT id, doc_id, tenant_id, text,
                   (embedding <=> @q)::float8 AS distance,
                   coalesce((SELECT d.superseded_at IS NOT NULL FROM documents d WHERE d.id = chunks.doc_id), false) AS superseded
            FROM chunks
            ORDER BY embedding <=> @q
            LIMIT @n
        ) cand
        ORDER BY cand.distance + CASE WHEN cand.superseded THEN @penalty::float8 ELSE 0::float8 END,
                 cand.superseded, cand.distance, cand.id
        LIMIT @k
        """;

    // TEST-ONLY: no WHERE, no join. RLS on chunks is the only thing standing between this and every tenant.
    public const string RlsOnlySearch = """
        SELECT id, doc_id, tenant_id, text, (embedding <=> @q)::float8 AS distance
        FROM chunks
        ORDER BY embedding <=> @q
        LIMIT @k
        """;
}
