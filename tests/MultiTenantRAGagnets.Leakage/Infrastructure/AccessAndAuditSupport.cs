using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using MultiTenantRAGagnets.Core.Audit;
using MultiTenantRAGagnets.Core.Ingestion;
using MultiTenantRAGagnets.Core.Providers;
using MultiTenantRAGagnets.Core.Retrieval;

namespace MultiTenantRAGagnets.Leakage.Infrastructure;

// Shared plumbing for the step 12 (access change) and step 13 (audit integrity) tests.
// New file: nothing existing was edited to make these tests possible.

/// <summary>A chat provider that always fails, to prove a failing LLM call still leaves an audit row.</summary>
public sealed class ThrowingChatProvider : IChatProvider
{
    public const string Name = "throwing-chat";
    public string ModelName => Name;
    public List<ChatRequest> Requests { get; } = [];

    public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        throw new InvalidOperationException("simulated LLM outage");
    }
}

/// <summary>A chat provider that cites a made-up chunk id as well as a real one, to prove invented citations are dropped.</summary>
public sealed class InventingChatProvider : IChatProvider
{
    public string ModelName => "inventing-chat";
    public List<ChatRequest> Requests { get; } = [];
    public Guid InventedId { get; } = Guid.NewGuid();

    public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        var cited = new List<string> { InventedId.ToString("D") };
        if (request.Context.Count > 0) cited.Add(request.Context[0].Id);
        return Task.FromResult(new ChatResponse("invented", cited, ModelName));
    }
}

/// <summary>
/// Wraps any <see cref="IChunkStore"/> and records what the service did to it: audit entries appended, commits,
/// sessions. Can be told to fail the audit insert. Also lets the DB-free tests watch the service logic on a laptop.
/// </summary>
public sealed class SpyStore(IChunkStore inner) : IChunkStore
{
    public List<AuditEntry> Entries { get; } = [];
    public int Commits { get; private set; }
    public int Sessions { get; private set; }
    public int Rollbacks { get; private set; }

    /// <summary>When set, AppendAuditAsync throws this instead of writing.</summary>
    public Exception? FailAppendWith { get; set; }

    public async Task<IChunkStoreSession> BeginAsync(CallerContext caller, CancellationToken cancellationToken = default)
    {
        Sessions++;
        return new SpySession(this, await inner.BeginAsync(caller, cancellationToken));
    }

    private sealed class SpySession(SpyStore owner, IChunkStoreSession session) : IChunkStoreSession
    {
        private bool _committed;

        public Task<IReadOnlyList<RetrievedChunk>> SearchAsync(float[] q, int k, RetrievalFilterMode mode, CancellationToken ct = default) =>
            session.SearchAsync(q, k, mode, ct);

        public async Task AppendAuditAsync(AuditEntry entry, CancellationToken ct = default)
        {
            if (owner.FailAppendWith is { } failure) throw failure;
            await session.AppendAuditAsync(entry, ct);
            owner.Entries.Add(entry);
        }

        public Task<IReadOnlyList<AuditRecord>> ListAuditAsync(int limit, CancellationToken ct = default) =>
            session.ListAuditAsync(limit, ct);

        public Task StoreDocumentAsync(DocumentRecord document, IReadOnlyList<ChunkRecord> chunks, CancellationToken ct = default) =>
            session.StoreDocumentAsync(document, chunks, ct);

        public async Task CommitAsync(CancellationToken ct = default)
        {
            await session.CommitAsync(ct);
            _committed = true;
            owner.Commits++;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_committed) owner.Rollbacks++;
            await session.DisposeAsync();
        }
    }
}

/// <summary>Neutral shape of one audit record, whether it came from a database row or from the entry handed to a store.</summary>
public sealed record AuditView(
    Guid TenantId, Guid UserId, string Role, string QueryHash,
    Guid[] Retrieved, Guid[] Sent, string Model, int LatencyMs)
{
    public static AuditView From(AuditEntry e) =>
        new(e.TenantId, e.UserId, e.Role, e.QueryHash, e.RetrievedChunkIds.ToArray(), e.SentChunkIds.ToArray(), e.Model, e.LatencyMs);
}

public static class AuditChecks
{
    /// <summary>Test-only HMAC key, long enough for AuditOptionsValidator (32+). Not a secret.</summary>
    public static string NewKey() => "audit-test-key-" + Guid.NewGuid().ToString("N");

    /// <summary>The expected hash, computed WITHOUT the production hasher, so the test is not grading the code with itself.</summary>
    public static string ExpectedHash(string key, string question) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(question)));

    public static string PlainSha256(string question) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(question)));

    /// <summary>A real RetrievalService over the given store, with a known hash key and (optionally) tweaked options.</summary>
    public static RetrievalService BuildService(
        IChunkStore store, IChatProvider chat, string hashKey, Action<RetrievalOptions>? configure = null)
    {
        var options = new RetrievalOptions();
        configure?.Invoke(options);
        var hasher = new HmacQueryHasher(Options.Create(new AuditOptions { HashKey = hashKey }));
        return new RetrievalService(new FakeEmbeddingProvider(), chat, store, hasher, Options.Create(options));
    }

    public static string NewMarker() => "audit-probe-" + Guid.NewGuid().ToString("N");

    private static void Check(bool condition, string message) => Assert.True(condition, message);

    private static string Short(IEnumerable<Guid> ids) => "[" + string.Join(",", ids.Select(i => i.ToString("N")[..8])) + "]";

    /// <summary>
    /// The audit-integrity rules of TESTING.md section 5, against ONE query:
    /// who/tenant/role match the caller, the hash is the keyed hash, retrieved and sent are what the response and
    /// the LLM actually saw, sent is a subset (in fact the in-order prefix) of retrieved, cited is a subset of sent.
    /// </summary>
    public static void AssertConsistent(
        string label, CallerContext caller, string question, string hashKey,
        QueryResult result, AuditView audit, IReadOnlyList<ChatRequest> chatRequests, bool chatCitesEverythingItWasGiven = true)
    {
        Check(audit.TenantId == caller.TenantId, $"{label}: audit tenant {audit.TenantId} != caller tenant {caller.TenantId}");
        Check(audit.UserId == caller.UserId, $"{label}: audit user {audit.UserId} != caller user {caller.UserId}");
        Check(audit.Role == caller.Role, $"{label}: audit role '{audit.Role}' != caller role '{caller.Role}'");

        var expectedHash = ExpectedHash(hashKey, question);
        Check(audit.QueryHash == expectedHash, $"{label}: audit query_hash is not HMAC-SHA256(key, query). got {audit.QueryHash} want {expectedHash}");
        Check(audit.QueryHash.Length == 64 && audit.QueryHash.All(Uri.IsHexDigit) && audit.QueryHash == audit.QueryHash.ToLowerInvariant(),
            $"{label}: query_hash is not 64 lowercase hex characters: {audit.QueryHash}");
        Check(audit.QueryHash != PlainSha256(question), $"{label}: query_hash equals an UNKEYED SHA-256 of the query; it must be keyed");

        Check(audit.Retrieved.SequenceEqual(result.RetrievedChunkIds),
            $"{label}: audit retrieved {Short(audit.Retrieved)} != response retrieved {Short(result.RetrievedChunkIds)}");
        Check(audit.Sent.SequenceEqual(result.SentChunkIds),
            $"{label}: audit sent {Short(audit.Sent)} != response sent {Short(result.SentChunkIds)}");

        var retrieved = audit.Retrieved.ToHashSet();
        Check(audit.Sent.All(retrieved.Contains), $"{label}: a chunk was SENT to the LLM that was never RETRIEVED. sent={Short(audit.Sent)} retrieved={Short(audit.Retrieved)}");
        Check(audit.Sent.SequenceEqual(audit.Retrieved.Take(audit.Sent.Length)),
            $"{label}: sent is not the in-rank-order prefix of retrieved. sent={Short(audit.Sent)} retrieved={Short(audit.Retrieved)}");
        Check(audit.Retrieved.Distinct().Count() == audit.Retrieved.Length, $"{label}: duplicate ids in retrieved");

        var sentSet = audit.Sent.ToHashSet();
        Check(result.CitedChunkIds.All(sentSet.Contains),
            $"{label}: response cites {Short(result.CitedChunkIds.Where(c => !sentSet.Contains(c)))} which was never sent to the LLM");
        if (chatCitesEverythingItWasGiven)
        {
            Check(result.CitedChunkIds.ToHashSet().SetEquals(sentSet),
                $"{label}: the fake chat cites every chunk it receives, so cited must equal sent. cited={Short(result.CitedChunkIds)} sent={Short(audit.Sent)}");
        }

        // What the LLM really received (the recording provider), not what the service says it sent.
        if (audit.Sent.Length == 0)
        {
            Check(chatRequests.Count == 0, $"{label}: nothing was sent, yet the chat provider was called {chatRequests.Count} time(s)");
        }
        else
        {
            Check(chatRequests.Count == 1, $"{label}: expected exactly one LLM call, saw {chatRequests.Count}");
            var seen = chatRequests[0].Context.Select(c => Guid.Parse(c.Id)).ToArray();
            Check(seen.SequenceEqual(audit.Sent), $"{label}: LLM received {Short(seen)} but the audit says sent {Short(audit.Sent)}");
        }

        Check(audit.Model == result.Model, $"{label}: audit model '{audit.Model}' != response model '{result.Model}'");
        Check(audit.LatencyMs == result.LatencyMs, $"{label}: audit latency {audit.LatencyMs} != response latency {result.LatencyMs}");
        Check(audit.LatencyMs >= 0, $"{label}: negative latency");
    }
}

public sealed record AuditDbRow(long Id, DateTimeOffset OccurredAt, string? QueryText, AuditView View, string Json);

/// <summary>Reads audit_log as the OWNER (superuser, no RLS), so the test sees what is really stored, not what app_user is allowed to see.</summary>
public static class AuditDb
{
    public static async Task<long> CountAsync(NpgsqlDataSource owner, Guid tenantId)
    {
        await using var c = await owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM audit_log WHERE tenant_id = @t", c);
        cmd.Parameters.AddWithValue("t", NpgsqlDbType.Uuid, tenantId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    public static async Task<List<AuditDbRow>> RowsByHashAsync(NpgsqlDataSource owner, Guid tenantId, string hash)
    {
        await using var c = await owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT id, occurred_at, query_text, tenant_id, user_id, role, query_hash,
                   retrieved_chunk_ids, sent_chunk_ids, model, latency_ms, to_jsonb(a)::text
            FROM audit_log a
            WHERE tenant_id = @t AND query_hash = @h
            ORDER BY id
            """, c);
        cmd.Parameters.AddWithValue("t", NpgsqlDbType.Uuid, tenantId);
        cmd.Parameters.AddWithValue("h", NpgsqlDbType.Text, hash);
        var rows = new List<AuditDbRow>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            rows.Add(new AuditDbRow(
                r.GetInt64(0),
                r.GetFieldValue<DateTimeOffset>(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                new AuditView(r.GetGuid(3), r.GetGuid(4), r.GetString(5), r.GetString(6),
                    r.GetFieldValue<Guid[]>(7), r.GetFieldValue<Guid[]>(8), r.GetString(9), r.GetInt32(10)),
                r.GetString(11)));
        }
        return rows;
    }

    public static async Task<string> RowJsonAsync(NpgsqlDataSource owner, long id)
    {
        await using var c = await owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT to_jsonb(a)::text FROM audit_log a WHERE id = @id", c);
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Runs one query through the service and returns the ONE new audit row it produced.
    /// Fails if the tenant's audit_log grew by anything other than exactly one row, if the row's query_text is
    /// set, or if the question (identified by its marker) appears anywhere in the stored row.
    /// </summary>
    public static async Task<(QueryResult Result, AuditDbRow Row)> QueryAndReadAuditAsync(
        NpgsqlDataSource owner, RetrievalService service, CallerContext caller,
        string question, string marker, int k, string hashKey)
    {
        var before = await CountAsync(owner, caller.TenantId);
        var result = await service.QueryAsync(caller, question, k);
        var after = await CountAsync(owner, caller.TenantId);
        Assert.True(after - before == 1, $"expected exactly 1 new audit row for one query, tenant audit_log grew by {after - before}");

        // The same question may have been asked before in a test; the collection runs one test at a time, so the
        // newest row with this hash is the one just written (the count delta above proves there is only one new one).
        var row = (await RowsByHashAsync(owner, caller.TenantId, AuditChecks.ExpectedHash(hashKey, question))).LastOrDefault();
        Assert.True(row is not null, "no audit row carries the keyed hash of the query that was just asked");
        Assert.True(row!.QueryText is null, "audit_log.query_text must never be populated by the service");
        Assert.DoesNotContain(marker, row.Json);
        Assert.True(row.OccurredAt > DateTimeOffset.UtcNow.AddMinutes(-10) && row.OccurredAt < DateTimeOffset.UtcNow.AddMinutes(10),
            $"occurred_at {row.OccurredAt:O} is not 'now'");
        return (result, row);
    }
}

/// <summary>Document ACL helpers: change a level AS app_user (the real application role), read and restore AS the owner.</summary>
public static class AclDb
{
    /// <summary>One committed UPDATE of documents.required_level run as app_user with the given tenant/role context. Returns rows affected.</summary>
    public static async Task<int> SetLevelAsAppUserAsync(NpgsqlDataSource app, Guid tenantId, string role, Guid docId, int level)
    {
        await using var raw = await RawTx.BeginAsync(app, tenantId, role);
        await using var cmd = new NpgsqlCommand("UPDATE documents SET required_level = @lvl WHERE id = @id", raw.Conn, raw.Tx);
        cmd.Parameters.AddWithValue("lvl", NpgsqlDbType.Smallint, (short)level);
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, docId);
        var n = await cmd.ExecuteNonQueryAsync();
        await raw.Tx.CommitAsync();
        return n;
    }

    public static async Task<int> OwnerLevelAsync(NpgsqlDataSource owner, Guid docId)
    {
        await using var c = await owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT required_level FROM documents WHERE id = @id", c);
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, docId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    /// <summary>Puts a level back as the owner (works whatever the policy state) and verifies it took.</summary>
    public static async Task OwnerRestoreLevelAsync(NpgsqlDataSource owner, Guid docId, int level)
    {
        await using (var c = await owner.OpenConnectionAsync())
        await using (var cmd = new NpgsqlCommand("UPDATE documents SET required_level = @lvl WHERE id = @id", c))
        {
            cmd.Parameters.AddWithValue("lvl", NpgsqlDbType.Smallint, (short)level);
            cmd.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, docId);
            await cmd.ExecuteNonQueryAsync();
        }
        var now = await OwnerLevelAsync(owner, docId);
        if (now != level)
            throw new InvalidOperationException($"Document {docId} was NOT restored to level {level} (is {now}). The seeded database is now modified.");
    }

    /// <summary>ctid + whole-row text (including the embedding vector) of every chunk of a document. Equal before and after means the rows were never rewritten.</summary>
    public static async Task<List<string>> ChunkRowsAsync(NpgsqlDataSource owner, Guid docId)
    {
        await using var c = await owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT c.ctid::text || '|' || c::text FROM chunks c WHERE c.doc_id = @d ORDER BY c.id", c);
        cmd.Parameters.AddWithValue("d", NpgsqlDbType.Uuid, docId);
        var rows = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) rows.Add(r.GetString(0));
        return rows;
    }

    /// <summary>id|tenant|level of every document in the given tenants, for "nothing changed" checks.</summary>
    public static async Task<List<string>> DocumentAclsAsync(NpgsqlDataSource owner, IEnumerable<Guid> tenantIds)
    {
        await using var c = await owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id::text || '|' || tenant_id::text || '|' || required_level::text FROM documents WHERE tenant_id = ANY(@t) ORDER BY id", c);
        cmd.Parameters.AddWithValue("t", NpgsqlDbType.Array | NpgsqlDbType.Uuid, tenantIds.ToArray());
        var rows = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) rows.Add(r.GetString(0));
        return rows;
    }
}
