using MultiTenantRAGagnets.Core.Chunking;

namespace MultiTenantRAGagnets.Eval;

/// <summary>
/// The additive HARD corpus (profile "hard"). Same 2 tenants and 3 roles as the standard corpus, same Chunker and
/// ChunkIdentity, same SplitMix64 streams keyed on the seed, so output is reproducible byte for byte.
///
/// What makes it hard (each is a deliberate, tested property, not size alone):
///  - near-duplicates across tenants: same topic and title, different facts;
///  - near-duplicates inside a tenant: "versioned" topics hold four superseded editions next to the current one;
///  - distractors: guidance/FAQ documents that share (or literally repeat) the question wording but never answer it;
///  - paraphrased questions that avoid the answer sentence's content words;
///  - multi-chunk answers: two facts more than a chunk apart, recall needs every gold chunk;
///  - questions whose answer is above the caller's role: gold is "none permitted" for that caller;
///  - prompt-injection documents that share vocabulary with real topics;
///  - noise documents that share only the generic filler.
///
/// Labels: a question's gold chunks are those that WHOLLY contain the answer sentence(s) (the generator re-pads and
/// retries until every answer sentence sits whole inside at least one chunk), and tests prove no other chunk of the
/// tenant contains the answer text. All data is invented; there is no real personal data.
/// </summary>
internal static class HardCorpusGenerator
{
    private const int Editions = 5; // 4 superseded + 1 current for versioned topics
    private static readonly string[] NoiseCopySuffixes = { "", " Addendum", " Annex", " Appendix" };
    private const int MinMultiGap = 650; // chars between the two facts of a multi-chunk answer (> chunk size)

    public static SyntheticCorpus Generate(int seed)
    {
        var tenants = CorpusGenerator.TenantList;
        var documents = new List<CorpusDocument>();
        var chunks = new List<CorpusChunk>();
        var meta = new List<DocMeta>();
        var usedMarkers = new HashSet<string>(StringComparer.Ordinal);

        // Numbers first: the cross-tenant constraint (different current fact) couples the two tenants.
        var numbers = DrawNumbers(seed, tenants);

        var currentDoc = new Dictionary<(string Tenant, string Topic), CorpusDocument>();
        var answerParts = new Dictionary<(string Tenant, string Topic), List<string>>();

        foreach (var tenant in tenants)
        {
            foreach (var topic in HardTopics.All)
            {
                var (edNumbers, m) = numbers[(tenant.Slug, topic.Slug)];
                var versioned = topic.Profile == TopicProfile.Versioned;
                var topicText = topic.Title.ToLowerInvariant();
                var fillOrder = SplitMix64.ForStream(seed, $"hard:fill:{tenant.Slug}:{topic.Slug}")
                    .Shuffled(Enumerable.Range(0, HardTopics.Fillers.Length)).ToList();

                string F(int k) => Fill(fillOrder[k % fillOrder.Count], topicText, tenant.Name);

                // ---- editions (versioned topics: 1..4 superseded, 5 current; others: only the current one)
                var totalEditions = versioned ? Editions : 1;
                for (var edition = 1; edition <= totalEditions; edition++)
                {
                    var isCurrent = edition == totalEditions;
                    var n = edNumbers[edition - 1];
                    var fact = topic.Fact.Replace("{n}", n.ToString(), StringComparison.Ordinal);
                    var kind = isCurrent ? HardDocKinds.Current : HardDocKinds.Superseded;
                    var title = !versioned ? topic.Title
                        : isCurrent ? $"{topic.Title} (Edition {edition})"
                        : $"{topic.Title} (Edition {edition}, superseded)";

                    var needles = new List<string> { fact };
                    // Second fact is tenant-specific too (otherwise the other tenant would hold an identical sentence).
                    var fact2 = topic.Fact2 is null ? null : $"{topic.Fact2} This rule applies at {tenant.Name}.";
                    if (fact2 is not null) needles.Add(fact2);

                    var doc = BuildDocument(seed, tenant, topic.Slug, kind, edition, title, topic.Level, false, usedMarkers,
                        needles, pad =>
                        {
                            var edNote = !versioned ? ""
                                : isCurrent ? $" This is edition {edition}, which replaces all earlier editions of the {topicText}."
                                : $" This is edition {edition}, which was withdrawn and replaced by a later edition and no longer applies.";
                            var p1 = $"{title}. This document sets out the {topicText} for {tenant.Name}.{edNote} {F(0)} {F(1)} {F(2)}";
                            var padding = string.Concat(Enumerable.Range(0, pad).Select(i => F(10 + i) + " "));

                            if (fact2 is null)
                            {
                                var p2 = $"{padding}{F(3)} {fact} {F(4)} {F(5)}";
                                var p3 = $"{F(6)} {F(7)} {F(8)} {F(9)} {F(11)} {F(12)} {F(13)} {F(14)}";
                                return new[] { p1, p2, p3 };
                            }

                            // Multi-chunk: fact, a long run of filler (> one chunk), then the second fact.
                            var gapFillers = new List<string>();
                            var gapLen = 0;
                            for (var i = 0; gapLen < MinMultiGap; i++)
                            {
                                var s = F(5 + i);
                                gapFillers.Add(s);
                                gapLen += s.Length + 1;
                            }

                            var pA = $"{padding}{F(3)} {fact} {F(4)}";
                            var pGap = string.Join(' ', gapFillers);
                            var pB = $"{F(15)} {fact2} {F(14)}";
                            return new[] { p1, pA, pGap, pB };
                        });

                    documents.Add(doc.Doc);
                    chunks.AddRange(doc.Chunks);
                    meta.Add(new DocMeta(doc.Doc.Id, tenant.Slug, topic.Slug, kind, edition));

                    if (isCurrent)
                    {
                        currentDoc[(tenant.Slug, topic.Slug)] = doc.Doc;
                        answerParts[(tenant.Slug, topic.Slug)] = needles;
                    }
                }

                // ---- distractor documents
                var heavy = topic.Profile == TopicProfile.Distractor;
                var distract = topic.Distract.Replace("{m}", m.ToString(), StringComparison.Ordinal);
                var dTitle = $"{topic.Title} Guidance Notes";
                var gd = BuildDocument(seed, tenant, topic.Slug, HardDocKinds.Distractor, 1, dTitle, topic.Level, false, usedMarkers,
                    Array.Empty<string>(), pad =>
                    {
                        var p1 = $"{dTitle}. These notes cover common questions about the {topicText} at {tenant.Name}. {F(0)} {F(1)}";
                        var p2 = $"{string.Concat(Enumerable.Range(0, pad).Select(i => F(10 + i) + " "))}{F(2)} {distract} {F(3)}";
                        var p3 = $"{F(4)} {F(5)} {F(6)} {F(7)} {F(8)} {F(9)}";
                        return new[] { p1, p2, p3 };
                    });
                documents.Add(gd.Doc);
                chunks.AddRange(gd.Chunks);
                meta.Add(new DocMeta(gd.Doc.Id, tenant.Slug, topic.Slug, HardDocKinds.Distractor, 1));

                if (heavy)
                {
                    // FAQ document: the question wording, verbatim, each in its own chunk, with no answer given.
                    var fTitle = $"{topic.Title} FAQ";
                    var faq = BuildDocument(seed, tenant, topic.Slug, HardDocKinds.Distractor, 2, fTitle, topic.Level, false, usedMarkers,
                        Array.Empty<string>(), pad =>
                        {
                            const string Deflect = "Please check with the policy owner for the figure that applies to you.";
                            var parts = new List<string> { $"{fTitle}. Frequently asked questions about the {topicText} at {tenant.Name}. {F(0)}" };
                            var qs = new[] { topic.Q1, topic.P1, topic.P2 };
                            for (var i = 0; i < qs.Length; i++)
                            {
                                var fillers = string.Concat(Enumerable.Range(0, 3 + (i == 0 ? pad : 0)).Select(j => F(1 + i * 4 + j) + " ")).TrimEnd();
                                parts.Add($"Frequently asked: {qs[i]} {Deflect} {fillers}");
                            }

                            return parts.ToArray();
                        });
                    documents.Add(faq.Doc);
                    chunks.AddRange(faq.Chunks);
                    meta.Add(new DocMeta(faq.Doc.Id, tenant.Slug, topic.Slug, HardDocKinds.Distractor, 2));
                }
            }

            // ---- noise documents
            foreach (var (baseTitle, level, copy) in HardTopics.Noise.SelectMany(nz => Enumerable.Range(0, NoiseCopySuffixes.Length).Select(c => (nz.Title, nz.Level, c))))
            {
                var title = baseTitle + NoiseCopySuffixes[copy];
                var slug = "noise-" + Slugify(title);
                var topicText = title.ToLowerInvariant();
                var order = SplitMix64.ForStream(seed, $"hard:fill:{tenant.Slug}:{slug}")
                    .Shuffled(Enumerable.Range(0, HardTopics.Fillers.Length)).ToList();
                string F(int k) => Fill(order[k % order.Count], topicText, tenant.Name);

                var d = BuildDocument(seed, tenant, slug, HardDocKinds.Noise, 1, title, level, false, usedMarkers,
                    Array.Empty<string>(), pad => new[]
                    {
                        $"{title}. This document sets out the {topicText} for {tenant.Name}. {F(0)} {F(1)} {F(2)}",
                        $"{string.Concat(Enumerable.Range(0, pad).Select(i => F(10 + i) + " "))}{F(3)} {F(4)} {F(5)} {F(6)}",
                        $"{F(7)} {F(8)}",
                    });
                documents.Add(d.Doc);
                chunks.AddRange(d.Chunks);
                meta.Add(new DocMeta(d.Doc.Id, tenant.Slug, slug, HardDocKinds.Noise, 1));
            }

            // ---- prompt-injection documents (no labelled question targets them)
            foreach (var (title, level, payload) in HardTopics.Injections)
            {
                var slug = "injection-" + Slugify(title);
                var topicText = title.ToLowerInvariant();
                var order = SplitMix64.ForStream(seed, $"hard:fill:{tenant.Slug}:{slug}")
                    .Shuffled(Enumerable.Range(0, HardTopics.Fillers.Length)).ToList();
                string F(int k) => Fill(order[k % order.Count], topicText, tenant.Name);

                var d = BuildDocument(seed, tenant, slug, HardDocKinds.Injection, 1, title, level, true, usedMarkers,
                    Array.Empty<string>(), pad => new[]
                    {
                        $"{title}. This notice relates to the {topicText} for {tenant.Name}. {F(0)} {F(1)}",
                        $"{string.Concat(Enumerable.Range(0, pad).Select(i => F(10 + i) + " "))}{F(2)} {HardTopics.InjectionPayloads[payload]} {F(3)} {F(4)}",
                        $"{F(5)} {F(6)} {F(7)}",
                    });
                documents.Add(d.Doc);
                chunks.AddRange(d.Chunks);
                meta.Add(new DocMeta(d.Doc.Id, tenant.Slug, slug, HardDocKinds.Injection, 1));
            }
        }

        var manifest = BuildManifest(tenants, documents);
        var visibleByCaller = manifest.ToDictionary(m => (m.TenantSlug, m.Role), m => m.VisibleChunkIds.ToHashSet());

        var gold = BuildGold(tenants, currentDoc, answerParts, chunks, visibleByCaller);

        // Compatibility view for the standard consumers: permitted instances only, with non-empty expected chunks.
        var questions = gold
            .Where(g => g.AnswerPermitted)
            .Select(g => new LabelledQuestion(g.Id, g.TenantSlug, g.Role, g.Kind, g.Text, g.DocId, g.PermittedGoldChunkIds))
            .ToList();

        return new SyntheticCorpus(
            seed, ChunkingParameters.Describe(), tenants, documents, chunks, questions, manifest,
            Array.Empty<AdversarialQuery>(), new HardExtras(gold, meta));
    }

    // ------------------------------------------------------------------------------------------ numbers

    private static Dictionary<(string Tenant, string Topic), (int[] Editions, int M)> DrawNumbers(int seed, IReadOnlyList<CorpusTenant> tenants)
    {
        var result = new Dictionary<(string, string), (int[], int)>();
        foreach (var topic in HardTopics.All)
        {
            var count = topic.Profile == TopicProfile.Versioned ? Editions : 1;
            var range = Enumerable.Range(topic.Min, topic.Max - topic.Min + 1).ToList();
            if (range.Count < count + 1)
                throw new InvalidOperationException($"Topic {topic.Slug}: number range {topic.Min}..{topic.Max} is too narrow for {count} editions.");

            var used = new HashSet<int>();
            foreach (var (tenant, index) in tenants.Select((t, i) => (t, i)))
            {
                var rng = SplitMix64.ForStream(seed, $"hard:num:{topic.Slug}:{tenant.Slug}");
                List<int> pool;
                if (index == 0)
                {
                    pool = rng.Shuffled(range);
                }
                else
                {
                    // The current value of a later tenant differs from EVERY value the earlier tenant used.
                    var fresh = range.Where(v => !used.Contains(v)).ToList();
                    var rest = range.Where(used.Contains).ToList();
                    pool = rng.Shuffled(fresh).Concat(rng.Shuffled(rest)).ToList();
                }

                // Editions are ordered oldest to current, current LAST, all distinct within the tenant.
                var values = pool.Take(count).ToArray();
                if (index > 0) Array.Reverse(values); // pool[0] is guaranteed fresh; make it the current (last) edition
                foreach (var v in values) used.Add(v);

                var m = SplitMix64.ForStream(seed, $"hard:m:{topic.Slug}:{tenant.Slug}").Between(topic.MMin, topic.MMax);
                result[(tenant.Slug, topic.Slug)] = (values, m);
            }
        }

        return result;
    }

    // ------------------------------------------------------------------------------------------ documents

    private sealed record Built(CorpusDocument Doc, List<CorpusChunk> Chunks);

    /// <summary>
    /// Builds one document. compose(pad) returns the paragraphs; pad is the number of extra filler sentences placed
    /// before the fact paragraph. The pad grows until EVERY needle and the secret sentence sit whole inside at least
    /// one chunk, so a gold chunk always contains the entire answer sentence it is labelled for.
    /// </summary>
    private static Built BuildDocument(
        int seed, CorpusTenant tenant, string topicSlug, string kind, int edition, string title, int level, bool injection,
        HashSet<string> usedMarkers, IReadOnlyList<string> needles, Func<int, string[]> compose)
    {
        var docKey = $"{tenant.Slug}|{topicSlug}|{kind}|{edition}";
        var docId = CorpusGenerator.NameGuid($"doc|hard|{docKey}");

        var secretRng = SplitMix64.ForStream(seed, $"hard:secret:{docKey}");
        string marker;
        do
        {
            var hex = secretRng.NextUInt64().ToString("X16")[..8];
            marker = $"SECRET-{tenant.Slug.ToUpperInvariant()}-{Slugify(topicSlug).ToUpperInvariant()}-{kind.ToUpperInvariant()}{edition}-{hex}";
        }
        while (!usedMarkers.Add(marker));

        var topicText = title.ToLowerInvariant();
        var secretParagraph = $"The reference code for this document is {marker}. Quote it in any correspondence about the {topicText}.";

        for (var pad = 0; pad < 12; pad++)
        {
            var text = string.Join("\n\n", compose(pad).Append(secretParagraph));
            var textChunks = Chunker.Chunk(text);
            var chunkIds = textChunks.Select(c => ChunkIdentity.For(docId, CorpusGenerator.DocumentVersion, c.Index)).ToList();

            var factChunks = new List<Guid>();
            var ok = true;
            foreach (var needle in needles)
            {
                var whole = Whole(text, needle, textChunks, chunkIds);
                if (whole.Count == 0) { ok = false; break; }
                factChunks.AddRange(whole);
            }

            var secretChunks = Whole(text, marker, textChunks, chunkIds);
            if (secretChunks.Count == 0) ok = false;
            if (!ok) continue;

            var doc = new CorpusDocument(
                docId, tenant.Slug, tenant.Id, topicSlug, title, level, CorpusGenerator.DocumentVersion, injection, marker,
                text, chunkIds, factChunks.Distinct().ToList(), secretChunks);
            var built = textChunks
                .Select(c => new CorpusChunk(chunkIds[c.Index], docId, tenant.Slug, level, c.Index, c.Text))
                .ToList();
            return new Built(doc, built);
        }

        throw new InvalidOperationException($"Could not place answer sentences whole inside a chunk for {docKey}.");
    }

    private static List<Guid> Whole(string text, string needle, IReadOnlyList<TextChunk> textChunks, List<Guid> chunkIds)
    {
        var s = text.IndexOf(needle, StringComparison.Ordinal);
        if (s < 0) throw new InvalidOperationException($"Needle not found in document text: {needle}");
        var e = s + needle.Length;
        return textChunks.Where(c => c.Start <= s && c.End >= e).Select(c => chunkIds[c.Index]).ToList();
    }

    private static string Fill(int index, string topic, string tenantName) =>
        HardTopics.Fillers[index]
            .Replace("{topic}", topic, StringComparison.Ordinal)
            .Replace("{tenant}", tenantName, StringComparison.Ordinal);

    private static string Slugify(string s) =>
        string.Concat(s.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')).Trim('-');

    // ------------------------------------------------------------------------------------------ manifest and gold

    private static List<ManifestEntry> BuildManifest(IReadOnlyList<CorpusTenant> tenants, IReadOnlyList<CorpusDocument> documents)
    {
        var manifest = new List<ManifestEntry>();
        foreach (var tenant in tenants)
        {
            foreach (var role in Roles.All)
            {
                var visible = documents
                    .Where(d => d.TenantSlug == tenant.Slug && Roles.CanSee(role, d.RequiredLevel))
                    .ToList();
                manifest.Add(new ManifestEntry(
                    tenant.Slug, role, Roles.LevelOf(role),
                    visible.Select(d => d.Id).OrderBy(g => g.ToString("D"), StringComparer.Ordinal).ToList(),
                    visible.SelectMany(d => d.ChunkIds).OrderBy(g => g.ToString("D"), StringComparer.Ordinal).ToList()));
            }
        }

        return manifest;
    }

    /// <summary>Lowercases the first letter so a question can follow a lead-in clause.</summary>
    private static string LcFirst(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    private static List<GoldLabel> BuildGold(
        IReadOnlyList<CorpusTenant> tenants,
        Dictionary<(string Tenant, string Topic), CorpusDocument> currentDoc,
        Dictionary<(string Tenant, string Topic), List<string>> answerParts,
        IReadOnlyList<CorpusChunk> chunks,
        Dictionary<(string, string), HashSet<Guid>> visibleByCaller)
    {
        var chunksByDoc = chunks.GroupBy(c => c.DocId).ToDictionary(g => g.Key, g => g.ToList());

        // One group per needle: the chunks of the answer document whose text contains it. A chunk is a slice of the
        // document text, so "contains the needle" is exactly "wholly covers it" (the needle is unique in its document).
        List<IReadOnlyList<Guid>> Groups(Guid docId, IEnumerable<string> needles) =>
            needles.Select(nd => (IReadOnlyList<Guid>)chunksByDoc[docId]
                .Where(c => c.Text.Contains(nd, StringComparison.Ordinal))
                .Select(c => c.Id)
                .OrderBy(g => g.ToString("D"), StringComparer.Ordinal)
                .ToList()).ToList();

        var gold = new List<GoldLabel>();
        var n = 0;

        foreach (var tenant in tenants)
        {
            foreach (var topic in HardTopics.All)
            {
                var doc = currentDoc[(tenant.Slug, topic.Slug)];
                var needles = answerParts[(tenant.Slug, topic.Slug)];
                var versioned = topic.Profile == TopicProfile.Versioned;

                // The versioned topics ask about the CURRENT edition explicitly: an old edition is not "wrong text",
                // it is a withdrawn answer, so the question has to say which one it means.
                string Q(string text) => versioned ? "As things stand today, " + LcFirst(text) : text;

                var kwCategory = topic.Profile switch
                {
                    TopicProfile.Versioned => HardCategories.NearDup,
                    TopicProfile.Distractor => HardCategories.Distractor,
                    TopicProfile.Multi => HardCategories.MultiChunk,
                    _ => HardCategories.Keyword,
                };
                var paraCategory = topic.Profile == TopicProfile.Multi ? HardCategories.MultiChunk : HardCategories.Paraphrase;

                var factGold = doc.FactChunkIds;
                var refGold = doc.SecretChunkIds;
                var versionTitle = topic.Title.ToLowerInvariant();

                var asks = new (string Kind, string Category, string Text, IReadOnlyList<Guid> Gold, List<string> Needles)[]
                {
                    ("fact", kwCategory, Q(topic.Q1), factGold, needles),
                    ("fact", paraCategory, Q(topic.P1), factGold, needles),
                    ("fact", paraCategory, Q(topic.P2), factGold, needles),
                    ("reference", HardCategories.Reference,
                        $"What is the reference code for the current {versionTitle} document?", refGold, new List<string> { doc.SecretMarker }),
                };

                foreach (var ask in asks)
                {
                    foreach (var role in Roles.All)
                    {
                        var visible = visibleByCaller[(tenant.Slug, role)];
                        var permitted = ask.Gold.Where(visible.Contains).ToList();
                        gold.Add(new GoldLabel(
                            $"h-{++n:D4}", tenant.Slug, role, ask.Category, ask.Kind, ask.Text, doc.Id,
                            ask.Gold.OrderBy(g => g.ToString("D"), StringComparer.Ordinal).ToList(),
                            permitted.OrderBy(g => g.ToString("D"), StringComparer.Ordinal).ToList(),
                            permitted.Count > 0,
                            ask.Needles,
                            Groups(doc.Id, ask.Needles)));
                    }
                }
            }
        }

        return gold;
    }
}
