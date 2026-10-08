namespace MultiTenantRAGagnets.Core.Chunking;

/// <summary>
/// The FIXED chunking parameters (IMPLEMENTATION_PLAN step 7: chunking params fixed and
/// recorded). They are constants on purpose: they swing recall@k, so a result file must be
/// able to state exactly what was used, and nothing may change them at runtime.
/// Changing any value changes every chunk id and therefore requires regenerating the eval
/// corpus/manifest and re-ingesting.
/// </summary>
public static class ChunkingParameters
{
    /// <summary>Maximum chunk length in UTF-16 characters.</summary>
    public const int ChunkSize = 500;

    /// <summary>Characters shared between two consecutive chunks.</summary>
    public const int Overlap = 100;

    /// <summary>
    /// A chunk that would end mid-word is shortened back to the last whitespace, but by at
    /// most this many characters (and never so far that it would not advance past the overlap).
    /// </summary>
    public const int WordSnapWindow = 80;

    public const string Strategy = "fixed-window-chars, whitespace-snap, no trimming";

    /// <summary>One-line statement of the parameters for reports and results files.</summary>
    public static string Describe() =>
        $"{Strategy}; size={ChunkSize} chars; overlap={Overlap} chars; wordSnapWindow={WordSnapWindow} chars";
}
