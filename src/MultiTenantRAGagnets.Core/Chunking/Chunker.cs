namespace MultiTenantRAGagnets.Core.Chunking;

/// <summary>
/// Deterministic fixed-window chunker using <see cref="ChunkingParameters"/>.
/// Pure function of the input text: same text, same chunks, same offsets.
/// </summary>
public static class Chunker
{
    public static IReadOnlyList<TextChunk> Chunk(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var chunks = new List<TextChunk>();
        var n = text.Length;
        var start = 0;
        var index = 0;

        while (start < n)
        {
            var hardEnd = Math.Min(start + ChunkingParameters.ChunkSize, n);
            var end = hardEnd;

            if (hardEnd < n)
            {
                // Snap back to a whitespace boundary, but always stay past the overlap so the
                // next window start (end - Overlap) is strictly greater than this start.
                var floor = Math.Max(hardEnd - ChunkingParameters.WordSnapWindow, start + ChunkingParameters.Overlap);
                for (var i = hardEnd; i > floor; i--)
                {
                    if (char.IsWhiteSpace(text[i - 1]))
                    {
                        end = i;
                        break;
                    }
                }
            }

            var slice = text.Substring(start, end - start);
            if (!string.IsNullOrWhiteSpace(slice))
            {
                chunks.Add(new TextChunk(index++, start, end - start, slice));
            }

            if (end >= n)
            {
                break;
            }

            start = end - ChunkingParameters.Overlap;
        }

        return chunks;
    }
}
