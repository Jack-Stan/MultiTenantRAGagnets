using System.Text;
using System.Text.Json;

namespace MultiTenantRAGagnets.Eval;

/// <summary>Writes the corpus as JSON. Same corpus in, byte-identical files out (LF, UTF-8 no BOM).</summary>
public static class CorpusWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Renders each output file to a string, keyed by file name (sorted by name).</summary>
    public static IReadOnlyDictionary<string, string> Render(SyntheticCorpus corpus)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["corpus.json"] = Serialize(new
            {
                corpus.Seed,
                corpus.ChunkingParameters,
                corpus.Tenants,
                corpus.Documents,
            }),
            ["chunks.json"] = Serialize(corpus.Chunks),
            ["questions.json"] = Serialize(corpus.Questions),
            ["manifest.json"] = Serialize(corpus.Manifest),
            ["adversarial.json"] = Serialize(corpus.AdversarialQueries),
        };
        return files;
    }

    public static IReadOnlyList<string> WriteAll(SyntheticCorpus corpus, string directory)
    {
        Directory.CreateDirectory(directory);
        var written = new List<string>();
        foreach (var (name, content) in Render(corpus))
        {
            var path = Path.Combine(directory, name);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            written.Add(path);
        }

        return written;
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options) + "\n";
}
