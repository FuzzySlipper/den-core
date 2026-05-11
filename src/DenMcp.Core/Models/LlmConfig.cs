namespace DenMcp.Core.Models;

public sealed class LlmConfig
{
    public string Endpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public int MaxTokens { get; set; } = 4096;
    public int ContextTokenBudget { get; set; } = 8000;
}
