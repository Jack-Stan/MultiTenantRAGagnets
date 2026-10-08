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

    /// <param name="dataSource">Built by <see cref="NpgsqlDataSourceFactory"/> (pgvector mapping enabled).</param>
    public NpgsqlChunkStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
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
            await using (var cmd = new NpgsqlCommand("SET LOCAL hnsw.iterative_scan = 'strict_order'", connection, transaction))
            {
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return new Session(connection, transaction, caller);
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
        private bool _committed;

        public Session(NpgsqlConnection connection, NpgsqlTransaction transaction, CallerContext caller)
        {
            _connection = connection;
            _transaction = transaction;
            _caller = caller;
        }

        public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
            float[] queryEmbedding, int k, RetrievalFilterMode mode, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(queryEmbedding);
            if (k < 1) throw new ArgumentOutOfRangeException(nameof(k));

            var filtered = mode == RetrievalFilterMode.AppAndRls;
            await using var cmd = new NpgsqlCommand(filtered ? RetrievalSql.FilteredSearch : RetrievalSql.RlsOnlySearch, _connection, _transaction);
            cmd.Parameters.AddWithValue("q", new Vector(queryEmbedding));
            cmd.Parameters.AddWithValue("k", NpgsqlDbType.Integer, k);
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
                    reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3), reader.GetDouble(4)));
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
                INSERT INTO documents (id, tenant_id, title, required_level, version)
                VALUES (@id, @tenant, @title, @level, @version)
                ON CONFLICT (id) DO UPDATE
                   SET title = EXCLUDED.title,
                       required_level = EXCLUDED.required_level,
                       version = EXCLUDED.version,
                       updated_at = now()
                """, _connection, _transaction))
            {
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

    // TEST-ONLY: no WHERE, no join. RLS on chunks is the only thing standing between this and every tenant.
    public const string RlsOnlySearch = """
        SELECT id, doc_id, tenant_id, text, (embedding <=> @q)::float8 AS distance
        FROM chunks
        ORDER BY embedding <=> @q
        LIMIT @k
        """;
}
