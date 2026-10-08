using MultiTenantRAGagnets.Eval;

// Usage: dotnet run --project src/MultiTenantRAGagnets.Eval -- [--seed N] [--out DIR] [--queries N]
var seed = CorpusGenerator.DefaultSeed;
var count = CorpusGenerator.DefaultAdversarialCount;
var outDir = Path.Combine("eval-out", "corpus");

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--seed" when i + 1 < args.Length: seed = int.Parse(args[++i]); break;
        case "--queries" when i + 1 < args.Length: count = int.Parse(args[++i]); break;
        case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
        default:
            Console.Error.WriteLine($"Unknown or incomplete argument: {args[i]}");
            return 2;
    }
}

var corpus = CorpusGenerator.Generate(seed, count);
foreach (var path in CorpusWriter.WriteAll(corpus, outDir))
{
    Console.WriteLine(path);
}

Console.WriteLine(
    $"seed={corpus.Seed} tenants={corpus.Tenants.Count} documents={corpus.Documents.Count} chunks={corpus.Chunks.Count} " +
    $"questions={corpus.Questions.Count} adversarial={corpus.AdversarialQueries.Count}");
Console.WriteLine($"chunking: {corpus.ChunkingParameters}");
return 0;
