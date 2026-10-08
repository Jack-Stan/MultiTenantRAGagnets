namespace MultiTenantRAGagnets.Core.Retrieval;

public sealed class RetrievalOptions
{
    public int DefaultK { get; set; } = 5;
    public int MaxK { get; set; } = 20;
    public int MaxQuestionLength { get; set; } = 2000;

    /// <summary>
    /// Prompt budget in characters. Retrieved chunks beyond it are NOT sent to the LLM; that is exactly
    /// why the audit log records retrieved and sent ids separately.
    /// </summary>
    public int MaxContextChars { get; set; } = 8000;

    /// <summary>The answer when no permitted chunk exists. The LLM is not called in that case.</summary>
    public string NoContextAnswer { get; set; } = "I could not find anything you are permitted to see that answers that.";
}
