using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DenMcp.Core.Models;
using Microsoft.Extensions.Logging;

namespace DenMcp.Core.Llm;

public sealed class OpenAiCompatibleLlmClient : ILlmClient
{
    private readonly HttpClient _http = new();
    private readonly LlmConfig _config;
    private readonly ILogger<OpenAiCompatibleLlmClient> _logger;

    public OpenAiCompatibleLlmClient(LlmConfig config, ILogger<OpenAiCompatibleLlmClient> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
    {
        var endpoint = _config.Endpoint.TrimEnd('/');
        var url = $"{endpoint}/chat/completions";

        var payload = JsonSerializer.Serialize(new
        {
            model = _config.Model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage }
            },
            max_tokens = _config.MaxTokens
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        if (!string.IsNullOrEmpty(_config.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);

        _logger.LogDebug("Librarian LLM request to {Url} with model {Model}", url, _config.Model);

        var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";

        _logger.LogDebug("Librarian LLM response: {Length} chars", content.Length);
        return content;
    }
}
