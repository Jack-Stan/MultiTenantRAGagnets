using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MultiTenantRAGagnets.Eval.Tests;

/// <summary>
/// Proofs for the hard profile. Wrong labels make every recall/MRR number meaningless, so these are deliberately
/// paranoid: they re-derive visibility from the document levels (not from the manifest), search the whole tenant for
/// the literal answer text, and pin the standard corpus's bytes so the additive profile cannot disturb it.
/// </summary>
public class HardCorpusTests
{
    private static readonly SyntheticCorpus Hard = CorpusGenerator.Generate(CorpusProfile.Hard);

    private static ManifestEntry Entry(string tenant, string role) =>
        Hard.Manifest.Single(m => m.TenantSlug == tenant && m.Role == role);

    private static IReadOnlyList<GoldLabel> Gold => Hard.Hard!.Gold;

    // ---------- the standard corpus is untouched ----------

    [Fact]
    public void Standard_profile_bytes_are_pinned_and_unchanged()
    {
        // SHA-256 of the five files of Generate(seed 42, 200 adversarial) captured BEFORE the hard profile existed.
        var expected = new Dictionary<string, string>
        {
            ["adversarial.json"] = "6b55f4fc02a4dc242b80c2c240ddb370c25680e9be8929695d8db97c487f481d",
            ["chunks.json"] = "e6803a172c0d100aa9b5bd184c5226ec66a3e0ef94d45393700f78800af42aa0",
            ["corpus.json"] = "0ccb1e214098fd65eaadce032ef81ae073bd8c1a724d2383755d2b58408adf20",
            ["manifest.json"] = "a90f68bed06e4ca3750d1b9051b6fc30b37aebaa17f7a3ae327c4b78d33c21b6",
            ["questions.json"] = "76414c1b3820d16d95eff12ca9bbf8034c7006791a38b8c9e0e53faa5e7a46cd",
        };

        var rendered = CorpusWriter.Render(CorpusGenerator.Generate());
        Assert.Equal(expected.Keys.OrderBy(k => k), rendered.Keys.OrderBy(k => k));
        foreach (var (name, hash) in expected)
        {
            var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rendered[name]))).ToLowerInvariant();
            Assert.Equal(hash, actual);
        }

        Assert.Equal("standard", CorpusGenerator.Generate().Profile);
        Assert.Null(CorpusGenerator.Generate().Hard);
    }

    [Fact]
    public void Standard_profile_via_the_profile_overload_is_the_same_corpus()
    {
        var a = CorpusWriter.Render(CorpusGenerator.Generate());
        var b = CorpusWriter.Render(CorpusGenerator.Generate(CorpusProfile.Standard));
        Assert.Equal(a.Keys, b.Keys);
        foreach (var k in a.Keys) Assert.Equal(a[k], b[k]);
    }

    // ---------- determinism ----------

    [Fact]
    public void Hard_same_seed_gives_byte_identical_json()
    {
        var a = CorpusWriter.Render(CorpusGenerator.Generate(CorpusProfile.Hard, 42));
        var b = CorpusWriter.Render(CorpusGenerator.Generate(CorpusProfile.Hard, 42));
        Assert.Equal(a.Keys, b.Keys);
        Assert.Contains("gold.json", a.Keys);
        foreach (var name in a.Keys) Assert.Equal(a[name], b[name]);
    }

    [Fact]
    public void Hard_written_files_are_byte_identical_across_two_runs()
    {
        var d1 = Directory.CreateTempSubdirectory("hard-a-").FullName;
        var d2 = Directory.CreateTempSubdirectory("hard-b-").FullName;
        try
        {
            CorpusWriter.WriteAll(CorpusGenerator.Generate(CorpusProfile.Hard, 7), d1);
            CorpusWriter.WriteAll(CorpusGenerator.Generate(CorpusProfile.Hard, 7), d2);
            var names = Directory.GetFiles(d1).Select(Path.GetFileName).OrderBy(n => n).ToList();
            Assert.Equal(7, names.Count);
            foreach (var n in names)
                Assert.Equal(File.ReadAllBytes(Path.Combine(d1, n!)), File.ReadAllBytes(Path.Combine(d2, n!)));
        }
        finally
        {
            Directory.Delete(d1, true);
            Directory.Delete(d2, true);
        }
    }

    [Fact]
    public void Hard_different_seed_changes_the_output_but_not_the_shape()
    {
        var a = CorpusGenerator.Generate(CorpusProfile.Hard, 1);
        var b = CorpusGenerator.Generate(CorpusProfile.Hard, 2);
        Assert.NotEqual(CorpusWriter.Render(a)["corpus.json"], CorpusWriter.Render(b)["corpus.json"]);
        Assert.Equal(a.Documents.Count, b.Documents.Count);
        Assert.Equal(a.Hard!.Gold.Count, b.Hard!.Gold.Count);
    }

    // ---------- size and shape ----------

    [Fact]
    public void Corpus_is_large_and_keeps_two_tenants_and_three_roles()
    {
        Assert.Equal(2, Hard.Tenants.Count);
        Assert.Equal(6, Hard.Manifest.Count);
        Assert.True(Hard.Documents.Count >= 300, $"documents={Hard.Documents.Count}");
        Assert.True(Hard.Chunks.Count >= 1500, $"chunks={Hard.Chunks.Count}");
        Assert.Equal(new[] { 1, 2, 3 }, Hard.Documents.Select(d => d.RequiredLevel).Distinct().OrderBy(x => x));
        Assert.Equal(Hard.Chunks.Count, Hard.Chunks.Select(c => c.Id).Distinct().Count());
        Assert.Equal(Hard.Documents.Count, Hard.Documents.Select(d => d.Id).Distinct().Count());
        Assert.All(Hard.Documents, d => Assert.NotEmpty(d.ChunkIds));
    }

    [Fact]
    public void Secret_markers_are_unique_and_appear_only_in_their_own_document()
    {
        var markers = Hard.Documents.Select(d => d.SecretMarker).ToList();
        Assert.Equal(markers.Count, markers.Distinct().Count());
        foreach (var d in Hard.Documents)
        {
            Assert.StartsWith($"SECRET-{d.TenantSlug.ToUpperInvariant()}-", d.SecretMarker);
            Assert.Equal(1, Hard.Documents.Count(o => o.Text.Contains(d.SecretMarker, StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void Distinct_question_texts_are_numerous_and_every_category_is_populated()
    {
        var texts = Gold.Select(g => g.Text).Distinct().ToList();
        Assert.True(texts.Count >= 150, $"distinct question texts = {texts.Count}");

        foreach (var cat in HardCategories.All)
            Assert.Contains(Gold, g => g.Category == cat && g.AnswerPermitted);

        // Per category, enough distinct texts to say something.
        foreach (var cat in HardCategories.All)
            Assert.True(Gold.Where(g => g.Category == cat).Select(g => g.Text).Distinct().Count() >= 12, cat);

        Assert.Equal(Gold.Count, Gold.Select(g => g.Id).Distinct().Count());
    }

    // ---------- the five hardness properties exist ----------

    [Fact]
    public void Cross_tenant_near_duplicates_have_the_same_title_and_different_facts()
    {
        var meta = Hard.Hard!.DocMeta.ToDictionary(m => m.DocId);
        var current = Hard.Documents.Where(d => meta[d.Id].Kind == HardDocKinds.Current).ToList();
        Assert.True(current.Count >= 90);

        foreach (var a in current.Where(d => d.TenantSlug == "acme"))
        {
            var b = current.Single(d => d.TenantSlug == "globex" && d.TemplateSlug == a.TemplateSlug);
            Assert.Equal(a.Title, b.Title);
            Assert.NotEqual(a.Id, b.Id);
            var factA = Gold.First(g => g.DocId == a.Id && g.Kind == "fact").AnswerNeedles[0];
            var factB = Gold.First(g => g.DocId == b.Id && g.Kind == "fact").AnswerNeedles[0];
            Assert.NotEqual(factA, factB);
        }
    }

    [Fact]
    public void Versioned_topics_hold_four_superseded_editions_with_distinct_facts_inside_the_tenant()
    {
        var meta = Hard.Hard!.DocMeta;
        var versioned = HardTopics.All.Where(t => t.Profile == TopicProfile.Versioned).ToList();
        Assert.True(versioned.Count >= 10);

        foreach (var tenant in new[] { "acme", "globex" })
        foreach (var topic in versioned)
        {
            var editions = meta.Where(m => m.TenantSlug == tenant && m.TopicSlug == topic.Slug &&
                                           m.Kind is HardDocKinds.Current or HardDocKinds.Superseded).ToList();
            Assert.Equal(5, editions.Count);
            Assert.Equal(4, editions.Count(e => e.Kind == HardDocKinds.Superseded));
            Assert.Single(editions, e => e.Kind == HardDocKinds.Current && e.Edition == 5);

            var docs = editions.Select(e => Hard.Documents.Single(d => d.Id == e.DocId)).ToList();
            Assert.Single(docs.Select(d => d.RequiredLevel).Distinct()); // same level: all equally visible
            // Fact sentence of each edition is different, so a chunk can be wrong by number alone.
            var facts = docs.Select(d => FactSentenceOf(d, topic)).ToList();
            Assert.Equal(5, facts.Distinct().Count());
            Assert.Contains(docs.Where(d => meta.Single(m => m.DocId == d.Id).Kind == HardDocKinds.Superseded),
                d => d.Text.Contains("no longer applies", StringComparison.Ordinal));
        }
    }

    private static string FactSentenceOf(CorpusDocument d, HardTopic topic)
    {
        var prefix = topic.Fact[..topic.Fact.IndexOf("{n}", StringComparison.Ordinal)];
        var s = d.Text.IndexOf(prefix, StringComparison.Ordinal);
        Assert.True(s >= 0, $"fact prefix not found in {d.Title}");
        var e = d.Text.IndexOf('.', s);
        return d.Text[s..(e + 1)];
    }

    [Fact]
    public void Distractor_documents_share_topic_vocabulary_but_never_contain_an_answer()
    {
        var meta = Hard.Hard!.DocMeta.ToDictionary(m => m.DocId);
        var distractors = Hard.Documents.Where(d => meta[d.Id].Kind == HardDocKinds.Distractor).ToList();
        Assert.True(distractors.Count >= 100);

        foreach (var d in distractors)
        {
            // No needle of any question about the same topic and tenant may occur in a distractor document.
            foreach (var g in Gold.Where(g => g.TenantSlug == d.TenantSlug && g.DocId != d.Id))
            {
                foreach (var needle in g.AnswerNeedles)
                    Assert.DoesNotContain(needle, d.Text);
            }
        }

        // The heavy ones repeat the literal question text (without an answer): strongest lexical bait.
        var heavy = HardTopics.All.Where(t => t.Profile == TopicProfile.Distractor).ToList();
        Assert.True(heavy.Count >= 10);
        foreach (var t in heavy)
        {
            var faq = Hard.Documents.Single(d => d.TenantSlug == "acme" && d.TemplateSlug == t.Slug && meta[d.Id].Edition == 2 && meta[d.Id].Kind == HardDocKinds.Distractor);
            Assert.Contains(t.Q1, faq.Text);
        }
    }

    [Fact]
    public void Paraphrased_questions_share_few_content_words_with_the_answer_sentence()
    {
        var stop = new HashSet<string>("the a an of to in is are for and or on at by what how many much do does i my me you your we our it its that this with from be must may can when who which there than then so as if up per each every any all after before".Split(' '));
        static HashSet<string> Words(string s) => Regex.Matches(s.ToLowerInvariant(), "[a-z]+").Select(m => m.Value).ToHashSet();
        double Overlap(GoldLabel g)
        {
            var q = Words(g.Text).Except(stop).ToHashSet();
            var a = Words(string.Join(' ', g.AnswerNeedles)).Except(stop).ToHashSet();
            return q.Count(a.Contains) / (double)Math.Max(1, q.Count);
        }

        var seen = new HashSet<string>();
        var para = Gold.Where(g => g.Category == HardCategories.Paraphrase && seen.Add(g.Text)).ToList();
        seen.Clear();
        var kw = Gold.Where(g => g.Category is HardCategories.Keyword or HardCategories.Distractor && seen.Add(g.Text)).ToList();
        Assert.True(para.Count >= 60 && kw.Count >= 20);

        var paraMean = para.Average(Overlap);
        var kwMean = kw.Average(Overlap);
        Assert.True(paraMean <= 0.10, $"paraphrase overlap mean {paraMean:0.00}");
        Assert.True(para.Max(Overlap) <= 0.40, $"paraphrase overlap max {para.Max(Overlap):0.00}");
        Assert.True(kwMean >= 0.60, $"keyword overlap mean {kwMean:0.00}");
    }

    [Fact]
    public void Multi_chunk_answers_need_two_chunks_and_no_single_chunk_has_both_facts()
    {
        var multi = Gold.Where(g => g.Category == HardCategories.MultiChunk).ToList();
        Assert.NotEmpty(multi);
        var chunkById = Hard.Chunks.ToDictionary(c => c.Id);

        foreach (var g in multi)
        {
            Assert.Equal(2, g.AnswerNeedles.Count);
            Assert.Equal(2, g.GoldGroups.Count);
            Assert.All(g.GoldGroups, grp => Assert.NotEmpty(grp));
            Assert.Empty(g.GoldGroups[0].Intersect(g.GoldGroups[1]));
            Assert.True(g.GoldChunkIds.Count >= 2);
            foreach (var id in g.GoldChunkIds)
            {
                var t = chunkById[id].Text;
                Assert.False(g.AnswerNeedles.All(n => t.Contains(n, StringComparison.Ordinal)),
                    $"{g.Id}: one chunk holds the whole answer, so it is not a multi-chunk answer");
            }
        }
    }

    [Fact]
    public void Injection_documents_exist_carry_a_payload_and_are_never_a_gold_target()
    {
        var meta = Hard.Hard!.DocMeta.ToDictionary(m => m.DocId);
        var inj = Hard.Documents.Where(d => d.IsInjection).ToList();
        Assert.True(inj.Count >= 6);
        Assert.All(inj, d => Assert.Equal(HardDocKinds.Injection, meta[d.Id].Kind));
        Assert.All(inj, d => Assert.Matches("(?i)ignore all previous|override|debug mode|authorise", d.Text));
        var injIds = inj.Select(d => d.Id).ToHashSet();
        Assert.DoesNotContain(Gold, g => injIds.Contains(g.DocId));
        Assert.DoesNotContain(Hard.Documents, d => !d.IsInjection && meta[d.Id].Kind == HardDocKinds.Injection);
    }

    // ---------- labels are right ----------

    [Fact]
    public void Every_gold_id_exists_belongs_to_the_answer_document_and_the_askers_tenant()
    {
        var chunkById = Hard.Chunks.ToDictionary(c => c.Id);
        var docById = Hard.Documents.ToDictionary(d => d.Id);

        foreach (var g in Gold)
        {
            Assert.NotEmpty(g.GoldChunkIds);
            Assert.NotEmpty(g.GoldGroups);
            var doc = docById[g.DocId];
            Assert.Equal(g.TenantSlug, doc.TenantSlug);
            foreach (var id in g.GoldChunkIds)
            {
                Assert.True(chunkById.ContainsKey(id), $"{g.Id}: gold chunk {id} does not exist");
                Assert.Equal(g.DocId, chunkById[id].DocId);
                Assert.Equal(g.TenantSlug, chunkById[id].TenantSlug);
            }

            // GoldChunkIds is exactly the union of the per-needle groups.
            Assert.Equal(g.GoldGroups.SelectMany(x => x).ToHashSet(), g.GoldChunkIds.ToHashSet());
            Assert.All(g.GoldGroups, grp => Assert.NotEmpty(grp));
            Assert.Equal(g.AnswerNeedles.Count, g.GoldGroups.Count);
        }
    }

    [Fact]
    public void Visibility_of_gold_matches_an_independent_level_check_for_every_caller()
    {
        var docById = Hard.Documents.ToDictionary(d => d.Id);
        foreach (var g in Gold)
        {
            var level = docById[g.DocId].RequiredLevel;
            var shouldSee = Roles.CanSee(g.Role, level); // the one hierarchy rule, not the manifest
            Assert.Equal(shouldSee, g.AnswerPermitted);

            var visible = Entry(g.TenantSlug, g.Role).VisibleChunkIds.ToHashSet();
            if (g.AnswerPermitted)
            {
                Assert.All(g.GoldChunkIds, id => Assert.Contains(id, visible));
                Assert.Equal(g.GoldChunkIds.OrderBy(x => x), g.PermittedGoldChunkIds.OrderBy(x => x));
            }
            else
            {
                Assert.Empty(g.PermittedGoldChunkIds);
                Assert.All(g.GoldChunkIds, id => Assert.DoesNotContain(id, visible));
            }
        }
    }

    [Fact]
    public void No_gold_chunk_of_a_tenant_is_visible_to_any_caller_of_another_tenant()
    {
        foreach (var g in Gold)
        {
            foreach (var other in Hard.Manifest.Where(m => m.TenantSlug != g.TenantSlug))
            {
                var visible = other.VisibleChunkIds.ToHashSet();
                Assert.All(g.GoldChunkIds, id => Assert.DoesNotContain(id, visible));
            }
        }
    }

    [Fact]
    public void The_answer_text_occurs_in_the_gold_chunks_and_in_no_other_chunk_of_any_tenant()
    {
        var chunkById = Hard.Chunks.ToDictionary(c => c.Id);
        var seen = new HashSet<(string, string)>();

        foreach (var g in Gold)
        {
            if (!seen.Add((g.TenantSlug, g.Text))) continue; // same tenant + text: same gold for every role
            var gold = g.GoldChunkIds.ToHashSet();

            // Every gold chunk carries at least one needle in full.
            foreach (var id in gold)
                Assert.Contains(g.AnswerNeedles, nd => chunkById[id].Text.Contains(nd, StringComparison.Ordinal));

            // Every needle is in some gold chunk, and in NO chunk outside the gold set (any tenant, any level).
            foreach (var needle in g.AnswerNeedles)
            {
                var holders = Hard.Chunks.Where(c => c.Text.Contains(needle, StringComparison.Ordinal)).Select(c => c.Id).ToList();
                Assert.NotEmpty(holders);
                Assert.All(holders, id => Assert.Contains(id, gold));
            }
        }
    }

    [Fact]
    public void Compatibility_questions_are_exactly_the_permitted_gold_instances()
    {
        var permitted = Gold.Where(g => g.AnswerPermitted).ToList();
        Assert.Equal(permitted.Count, Hard.Questions.Count);
        var byId = Gold.ToDictionary(g => g.Id);
        foreach (var q in Hard.Questions)
        {
            var g = byId[q.Id];
            Assert.True(g.AnswerPermitted);
            Assert.Equal(g.PermittedGoldChunkIds, q.ExpectedChunkIds);
            var visible = Entry(q.TenantSlug, q.Role).VisibleChunkIds.ToHashSet();
            Assert.All(q.ExpectedChunkIds, id => Assert.Contains(id, visible));
        }
    }

    // ---------- above-role: the oracle must say "none permitted", and leakage is impossible by construction ----------

    [Fact]
    public void Above_role_questions_exist_for_employee_and_manager_and_never_for_hr_admin()
    {
        Assert.Contains(Gold, g => !g.AnswerPermitted && g.Role == Roles.Employee);
        Assert.Contains(Gold, g => !g.AnswerPermitted && g.Role == Roles.Manager);
        Assert.DoesNotContain(Gold, g => !g.AnswerPermitted && g.Role == Roles.HrAdmin);
        // Same text, same tenant, different role: answerable for a higher role, "none permitted" for a lower one.
        var pair = Gold.Where(g => !g.AnswerPermitted).First();
        Assert.Contains(Gold, g => g.Text == pair.Text && g.TenantSlug == pair.TenantSlug && g.Role == Roles.HrAdmin && g.AnswerPermitted);
    }

    [Fact]
    public void Forbidden_golds_cannot_leak_by_construction_and_the_permitted_set_holds_no_answer_text()
    {
        var chunkById = Hard.Chunks.ToDictionary(c => c.Id);
        var checkedCallers = 0;

        foreach (var g in Gold.Where(g => !g.AnswerPermitted))
        {
            var visible = Entry(g.TenantSlug, g.Role).VisibleChunkIds;
            // 1. no forbidden gold id is in the permitted set (what retrieval is allowed to return)...
            Assert.Empty(visible.Intersect(g.GoldChunkIds));
            // 2. ...and nothing the caller may see contains the answer text at all, so even a perfect retriever
            //    over the permitted set cannot produce it: the correct outcome is "not available to this caller".
            foreach (var needle in g.AnswerNeedles)
                Assert.DoesNotContain(visible, id => chunkById[id].Text.Contains(needle, StringComparison.Ordinal));
            checkedCallers++;
        }

        Assert.True(checkedCallers >= 300, $"above-role instances = {checkedCallers}");
    }

    [Fact]
    public void Every_hr_admin_gold_is_answerable_and_the_manifest_is_the_whole_tenant()
    {
        foreach (var tenant in new[] { "acme", "globex" })
        {
            var hr = Entry(tenant, Roles.HrAdmin).VisibleChunkIds.ToHashSet();
            Assert.Equal(Hard.Chunks.Where(c => c.TenantSlug == tenant).Select(c => c.Id).ToHashSet(), hr);
            Assert.True(Entry(tenant, Roles.Employee).VisibleChunkIds.Count < Entry(tenant, Roles.Manager).VisibleChunkIds.Count);
            Assert.True(Entry(tenant, Roles.Manager).VisibleChunkIds.Count < hr.Count);
        }
    }

    [Fact]
    public void Chunk_required_level_matches_its_document_and_manifest_is_sorted()
    {
        var level = Hard.Documents.ToDictionary(d => d.Id, d => d.RequiredLevel);
        var chunkLevel = Hard.Chunks.ToDictionary(c => c.Id, c => c.RequiredLevel);
        Assert.All(Hard.Chunks, c => Assert.Equal(level[c.DocId], c.RequiredLevel));
        foreach (var e in Hard.Manifest)
        {
            var strs = e.VisibleChunkIds.Select(g => g.ToString("D")).ToList();
            Assert.Equal(strs.OrderBy(s => s, StringComparer.Ordinal), strs);
            Assert.All(e.VisibleChunkIds, id => Assert.True(Roles.CanSee(e.Role, chunkLevel[id])));
        }
    }
}
