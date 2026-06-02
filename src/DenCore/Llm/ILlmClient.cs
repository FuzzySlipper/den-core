namespace DenCore.Llm;

public interface ILlmClient
{
    Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct = default);
}
