namespace MultiTenantRAGagnets.Eval.Tests;

public class CorpusTests
{
    private static readonly SyntheticCorpus Corpus = CorpusGenerator.Generate();

    private static ManifestEntry Entry(string tenant, string role) =>
        Corpus.Manifest.Single(m => m.TenantSlug == tenant && m.Role == role);

    // ---------- determinism ----------

    [Fact]
    public void Same_seed_gives_byte_identical_json()
    {
        var a = CorpusWriter.Render(CorpusGenerator.Generate(42));
        var b = CorpusWriter.Render(CorpusGenerator.Generate(42));
        Assert.Equal(a.Keys, b.Keys);
        foreach (var name in a.Keys)
        {
            Assert.Equal(a[name], b[name]);
        }
    }

    [Fact]
    public void Written_files_are_byte_identical_across_two_runs()
    {
        var d1 = Directory.CreateTempSubdirectory("eval-a-").FullName;
        var d2 = Directory.CreateTempSubdirectory("eval-b-").FullName;
        try
        {
            CorpusWriter.WriteAll(CorpusGenerator.Generate(7), d1);
            CorpusWriter.WriteAll(CorpusGenerator.Generate(7), d2);
            var names = Directory.GetFiles(d1).Select(Path.GetFileName).OrderBy(n => n).ToList();
            Assert.Equal(5, names.Count);
            foreach (var n in names)
            {
                Assert.Equal(File.ReadAllBytes(Path.Combine(d1, n!)), File.ReadAllBytes(Path.Combine(d2, n!)));
            }
        }
        finally
        {
            Directory.Delete(d1, true);
            Directory.Delete(d2, true);
        }
    }

    [Fact]
    public void Different_seed_changes_the_output()
    {
        var a = CorpusWriter.Render(CorpusGenerator.Generate(1))["corpus.json"];
        var b = CorpusWriter.Render(CorpusGenerator.Generate(2))["corpus.json"];
        Assert.NotEqual(a, b);
    }

    // ---------- shape ----------

    [Fact]
    public void Two_tenants_three_roles_with_expected_document_levels()
    {
        Assert.Equal(2, Corpus.Tenants.Count);
        Assert.Equal(6, Corpus.Manifest.Count);
        Assert.Equal(new[] { 1, 2, 3 }, Corpus.Documents.Select(d => d.RequiredLevel).Distinct().OrderBy(x => x));
        foreach (var t in Corpus.Tenants)
        {
            var docs = Corpus.Documents.Where(d => d.TenantSlug == t.Slug).ToList();
            Assert.Equal(12, docs.Count);
            Assert.Equal(2, docs.Count(d => d.IsInjection));
            foreach (var level in new[] { 1, 2, 3 })
            {
                Assert.Contains(docs, d => d.RequiredLevel == level && !d.IsInjection);
            }
        }
    }

    [Fact]
    public void Documents_are_near_identical_across_tenants_except_name_and_secret()
    {
        foreach (var a in Corpus.Documents.Where(d => d.TenantSlug == "acme"))
        {
            var b = Corpus.Documents.Single(d => d.TenantSlug == "globex" && d.TemplateSlug == a.TemplateSlug);
            Assert.NotEqual(a.Id, b.Id);
            Assert.NotEqual(a.SecretMarker, b.SecretMarker);
            var normA = a.Text.Replace(a.SecretMarker, "<S>").Replace("Acme", "<T>");
            var normB = b.Text.Replace(b.SecretMarker, "<S>").Replace("Globex", "<T>");
            Assert.Equal(normA, normB);
        }
    }

    [Fact]
    public void Secret_markers_are_distinct_and_appear_only_in_their_own_document()
    {
        var markers = Corpus.Documents.Select(d => d.SecretMarker).ToList();
        Assert.Equal(markers.Count, markers.Distinct().Count());
        foreach (var d in Corpus.Documents)
        {
            Assert.Equal(1, Corpus.Documents.Count(o => o.Text.Contains(d.SecretMarker, StringComparison.Ordinal)));
            Assert.StartsWith($"SECRET-{d.TenantSlug.ToUpperInvariant()}-", d.SecretMarker);
        }
    }

    [Fact]
    public void Chunk_ids_are_unique_and_every_document_has_chunks()
    {
        var ids = Corpus.Chunks.Select(c => c.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(Corpus.Documents, d => Assert.NotEmpty(d.ChunkIds));
        Assert.True(Corpus.Chunks.Count > Corpus.Documents.Count);
    }

    [Fact]
    public void Injection_documents_contain_an_injection_payload()
    {
        var inj = Corpus.Documents.Where(d => d.IsInjection).ToList();
        Assert.Equal(4, inj.Count);
        Assert.All(inj, d => Assert.Matches("(?i)ignore all previous|override", d.Text));
    }

    // ---------- manifest oracle ----------

    [Fact]
    public void No_tenant_secret_is_expected_visible_to_the_other_tenant_for_any_role()
    {
        foreach (var entry in Corpus.Manifest)
        {
            var visible = entry.VisibleChunkIds.ToHashSet();
            foreach (var chunk in Corpus.Chunks)
            {
                foreach (var doc in Corpus.Documents.Where(d => d.TenantSlug != entry.TenantSlug))
                {
                    // Any chunk carrying a foreign secret marker must not be visible.
                    if (chunk.Text.Contains(doc.SecretMarker, StringComparison.Ordinal))
                    {
                        Assert.DoesNotContain(chunk.Id, visible);
                    }
                }
            }
        }
    }

    [Fact]
    public void Manifest_only_ever_contains_the_callers_own_tenant_chunks()
    {
        var tenantOfChunk = Corpus.Chunks.ToDictionary(c => c.Id, c => c.TenantSlug);
        foreach (var e in Corpus.Manifest)
        {
            Assert.All(e.VisibleChunkIds, id => Assert.Equal(e.TenantSlug, tenantOfChunk[id]));
        }
    }

    [Fact]
    public void Employee_and_manager_never_see_hr_admin_only_documents()
    {
        var hrOnly = Corpus.Documents.Where(d => d.RequiredLevel == 3).SelectMany(d => d.ChunkIds).ToHashSet();
        foreach (var tenant in new[] { "acme", "globex" })
        {
            Assert.DoesNotContain(Entry(tenant, "employee").VisibleChunkIds, hrOnly.Contains);
            Assert.DoesNotContain(Entry(tenant, "manager").VisibleChunkIds, hrOnly.Contains);
        }
    }

    [Fact]
    public void Employee_never_sees_manager_documents()
    {
        var mgr = Corpus.Documents.Where(d => d.RequiredLevel == 2).SelectMany(d => d.ChunkIds).ToHashSet();
        foreach (var tenant in new[] { "acme", "globex" })
        {
            Assert.DoesNotContain(Entry(tenant, "employee").VisibleChunkIds, mgr.Contains);
        }
    }

    [Fact]
    public void Hr_admin_inherits_manager_and_employee_and_sees_whole_tenant()
    {
        foreach (var tenant in new[] { "acme", "globex" })
        {
            var hr = Entry(tenant, "hr-admin").VisibleChunkIds.ToHashSet();
            var mgr = Entry(tenant, "manager").VisibleChunkIds.ToHashSet();
            var emp = Entry(tenant, "employee").VisibleChunkIds.ToHashSet();
            Assert.True(emp.IsSubsetOf(mgr));
            Assert.True(mgr.IsSubsetOf(hr));
            Assert.True(emp.Count < mgr.Count && mgr.Count < hr.Count);
            Assert.Equal(
                Corpus.Chunks.Where(c => c.TenantSlug == tenant).Select(c => c.Id).ToHashSet(),
                hr);
        }
    }

    [Fact]
    public void Manifest_is_sorted_so_it_is_stable()
    {
        foreach (var e in Corpus.Manifest)
        {
            var strs = e.VisibleChunkIds.Select(g => g.ToString("D")).ToList();
            Assert.Equal(strs.OrderBy(s => s, StringComparer.Ordinal), strs);
        }
    }

    // ---------- labelled questions ----------

    [Fact]
    public void Every_question_has_expected_chunks_that_the_asker_may_see_and_that_contain_the_answer()
    {
        Assert.NotEmpty(Corpus.Questions);
        var chunkById = Corpus.Chunks.ToDictionary(c => c.Id);
        foreach (var q in Corpus.Questions)
        {
            Assert.NotEmpty(q.ExpectedChunkIds);
            var visible = Entry(q.TenantSlug, q.Role).VisibleChunkIds.ToHashSet();
            Assert.All(q.ExpectedChunkIds, id =>
            {
                Assert.Contains(id, visible);
                Assert.Equal(q.DocId, chunkById[id].DocId);
            });
        }

        // Reference questions: the expected chunk text contains the (or part of the) secret of the asker's own tenant.
        foreach (var q in Corpus.Questions.Where(q => q.Kind == "reference"))
        {
            var doc = Corpus.Documents.Single(d => d.Id == q.DocId);
            Assert.Equal(q.TenantSlug, doc.TenantSlug);
            Assert.Contains(q.ExpectedChunkIds, id => chunkById[id].Text.Contains(doc.SecretMarker, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Question_counts_grow_with_role_level()
    {
        int Count(string r) => Corpus.Questions.Count(q => q.TenantSlug == "acme" && q.Role == r);
        Assert.True(Count("employee") < Count("manager") && Count("manager") < Count("hr-admin"));
    }

    // ---------- adversarial ----------

    [Fact]
    public void Adversarial_generator_produces_200_by_default_with_all_kinds()
    {
        Assert.Equal(200, Corpus.AdversarialQueries.Count);
        Assert.Equal(200, Corpus.AdversarialQueries.Select(q => q.Id).Distinct().Count());
        foreach (var kind in AdversarialKinds.All)
        {
            Assert.Equal(40, Corpus.AdversarialQueries.Count(q => q.Kind == kind));
        }
    }

    [Fact]
    public void Every_adversarial_bait_is_forbidden_for_its_caller_and_queries_are_nonempty()
    {
        foreach (var q in Corpus.AdversarialQueries)
        {
            Assert.False(string.IsNullOrWhiteSpace(q.Text));
            Assert.DoesNotContain(q.BaitChunkId, Entry(q.TenantSlug, q.Role).VisibleChunkIds);
        }
    }

    [Fact]
    public void Adversarial_set_covers_cross_tenant_and_cross_role_for_every_role()
    {
        var chunkTenant = Corpus.Chunks.ToDictionary(c => c.Id, c => c.TenantSlug);
        Assert.Contains(Corpus.AdversarialQueries, q => chunkTenant[q.BaitChunkId] != q.TenantSlug);
        Assert.Contains(Corpus.AdversarialQueries, q => chunkTenant[q.BaitChunkId] == q.TenantSlug);
        foreach (var role in Roles.All)
        {
            Assert.Contains(Corpus.AdversarialQueries, q => q.Role == role);
        }
    }

    [Fact]
    public void Adversarial_count_is_configurable_and_deterministic()
    {
        var a = CorpusGenerator.Generate(5, 37).AdversarialQueries;
        var b = CorpusGenerator.Generate(5, 37).AdversarialQueries;
        Assert.Equal(37, a.Count);
        Assert.Equal(a, b);
    }
}
