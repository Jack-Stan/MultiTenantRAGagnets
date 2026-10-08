using Microsoft.Extensions.Options;
using MultiTenantRAGagnets.Core.Audit;
using MultiTenantRAGagnets.Core.Ingestion;
using MultiTenantRAGagnets.Core.Providers;
using MultiTenantRAGagnets.Core.Retrieval;
using MultiTenantRAGagnets.Core.Tests.Support;

namespace MultiTenantRAGagnets.Core.Tests;

public class IngestAndQueryWiringTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid UserA = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    private static (InMemoryChunkStore store, CallerAccessor accessor, IngestionService ingest, RetrievalService retrieve, RecordingChatProvider chat) Build()
    {
        var store = new InMemoryChunkStore();
        var accessor = new CallerAccessor();
        var emb = new FakeEmbeddingProvider();
        var chat = new RecordingChatProvider();
        var ingest = new IngestionService(emb, new StoreBackedChunkWriter(store, accessor));
        var hasher = new HmacQueryHasher(Options.Create(new AuditOptions { HashKey = "test-only-hash-key-0123456789abcdef" }));
        var retrieve = new RetrievalService(emb, chat, store, hasher, Options.Create(new RetrievalOptions()));
        return (store, accessor, ingest, retrieve, chat);
    }

    [Fact]
    public async Task Ingested_document_is_retrievable_only_at_or_above_its_level_and_only_in_its_tenant()
    {
        var (_, accessor, ingest, retrieve, chat) = Build();
        var admin = new CallerContext(TenantA, UserA, "hr-admin");
        accessor.Set(admin);

        var result = await ingest.IngestAsync(new IngestRequest(Guid.NewGuid(), TenantA, "Pay bands", RequiredLevel: 3, Version: 1, "Engineers are paid 90k."));
        Assert.True(result.ChunkCount >= 1);

        var asHr = await retrieve.QueryAsync(admin, "Engineers are paid 90k.");
        Assert.Contains(result.ChunkIds[0], asHr.SentChunkIds);

        var asEmployee = await retrieve.QueryAsync(admin with { Role = "employee" }, "Engineers are paid 90k.");
        Assert.Empty(asEmployee.SentChunkIds);

        var otherTenant = await retrieve.QueryAsync(new CallerContext(TenantB, Guid.NewGuid(), "hr-admin"), "Engineers are paid 90k.");
        Assert.Empty(otherTenant.SentChunkIds);
        Assert.Single(chat.Requests); // the LLM only ever ran for the permitted query
    }

    [Fact]
    public async Task Writer_refuses_a_document_for_a_tenant_other_than_the_callers()
    {
        var (_, accessor, ingest, _, _) = Build();
        accessor.Set(new CallerContext(TenantA, UserA, "hr-admin"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ingest.IngestAsync(new IngestRequest(Guid.NewGuid(), TenantB, "Sneaky", 1, 1, "text")));
    }

    [Fact]
    public async Task Writer_without_an_authenticated_caller_throws()
    {
        var (_, _, ingest, _, _) = Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ingest.IngestAsync(new IngestRequest(Guid.NewGuid(), TenantA, "Doc", 1, 1, "text")));
    }

    [Fact]
    public async Task Employee_cannot_ingest_a_document_above_their_level()
    {
        var (store, accessor, ingest, _, _) = Build();
        accessor.Set(new CallerContext(TenantA, UserA, "employee"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ingest.IngestAsync(new IngestRequest(Guid.NewGuid(), TenantA, "Upgrade", 3, 1, "text")));
        Assert.Empty(store.AllChunks); // rolled back, nothing stored
    }

    [Fact]
    public void Audit_entry_rejects_a_sent_chunk_that_was_never_retrieved()
    {
        var entry = new AuditEntry(TenantA, UserA, "employee", "abc", [], [Guid.NewGuid()], "m", 1);
        Assert.Throws<InvalidOperationException>(entry.Validate);
    }
}
