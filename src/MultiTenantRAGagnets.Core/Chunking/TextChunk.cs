namespace MultiTenantRAGagnets.Core.Chunking;

/// <summary>One slice of a source text. Invariant: <c>Text == source.Substring(Start, Length)</c>.</summary>
public sealed record TextChunk(int Index, int Start, int Length, string Text)
{
    /// <summary>Exclusive end offset in the source text.</summary>
    public int End => Start + Length;
}
