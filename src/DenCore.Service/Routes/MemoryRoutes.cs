using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DenCore.Data;
using DenCore.Models;

namespace DenCore.Service.Routes;

public static partial class MemoryRoutes
{
    private const string MetadataTagPrefix = "memory-metadata-json-b64:";
    private const string ProvenanceTagPrefix = "memory-provenance-json-b64:";
    private const string TombstoneTag = "den-memory-tombstoned";
    private const string TombstoneReasonPrefix = "memory-tombstone-reason:";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] InitialSpaces = ["project", "task", "session", "global", "review"];

    public static void MapMemoryRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/projects/{projectId}/memory");

        group.MapGet("/spaces", (HttpContext http, DenCoreOptions options) =>
            IsAuthorized(http, options)
                ? Results.Ok(new MemorySpacesResponse(InitialSpaces))
                : Results.Unauthorized());

        group.MapPost("/entries", async (HttpContext http, DenCoreOptions options, IDocumentRepository repo, string projectId, StoreMemoryEntryRequest req) =>
        {
            if (!IsAuthorized(http, options))
                return Results.Unauthorized();

            var space = NormalizeSpace(req.Space);
            var key = NormalizeKey(req.Key);
            var slug = ToEntryId(space, key);
            var doc = await repo.UpsertAsync(new Document
            {
                ProjectId = projectId,
                Slug = slug,
                Title = key,
                Content = req.Content,
                DocType = DocType.Memory,
                Summary = req.Content.Length <= 240 ? req.Content : req.Content[..240],
                Tags = BuildTags(space, key, req.Metadata, req.Provenance)
            });

            return Results.Ok(ToEntryResponse(doc));
        });

        group.MapGet("/entries/{entryId}", async (HttpContext http, DenCoreOptions options, IDocumentRepository repo, string projectId, string entryId) =>
        {
            if (!IsAuthorized(http, options))
                return Results.Unauthorized();

            var doc = await repo.GetAsync(projectId, entryId);
            return doc is { DocType: DocType.Memory } && !IsTombstoned(doc)
                ? Results.Ok(ToEntryResponse(doc))
                : Results.NotFound(new { status = "not_found", entryId });
        });

        group.MapPost("/search", async (HttpContext http, DenCoreOptions options, IDocumentRepository repo, string projectId, SearchMemoryEntriesRequest req) =>
        {
            if (!IsAuthorized(http, options))
                return Results.Unauthorized();

            var spaces = req.Spaces is { Count: > 0 }
                ? req.Spaces.Select(NormalizeSpace).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : null;
            var limit = Math.Clamp(req.Limit ?? 10, 1, 100);
            var query = req.Query?.Trim();

            var candidates = (await repo.ListAsync(projectId, DocType.Memory))
                .Where(doc => string.IsNullOrWhiteSpace(query) || MatchesMemoryQuery(doc, query))
                .Select(ToSearchResponse);

            var includeTombstoned = req.IncludeTombstoned == true;
            var results = candidates
                .Where(result => includeTombstoned || !result.Tombstoned)
                .Where(result => spaces is null || spaces.Contains(result.Space))
                .Take(limit)
                .ToList();

            return Results.Ok(new MemorySearchResponse(results));
        });

        group.MapDelete("/entries/{entryId}", async (HttpContext http, DenCoreOptions options, IDocumentRepository repo, string projectId, string entryId) =>
        {
            if (!IsAuthorized(http, options))
                return Results.Unauthorized();

            var doc = await repo.GetAsync(projectId, entryId);
            if (doc is not { DocType: DocType.Memory })
                return Results.NotFound(new { status = "not_found", entryId });

            var tombstone = await ReadTombstoneRequestAsync(http);
            var reason = NormalizeTombstoneReason(tombstone?.TombstoneReason);
            var tags = (doc.Tags ?? []).Where(tag => tag != TombstoneTag && !tag.StartsWith(TombstoneReasonPrefix, StringComparison.Ordinal)).ToList();
            tags.Add(TombstoneTag);
            if (!string.IsNullOrWhiteSpace(reason))
                tags.Add(TombstoneReasonPrefix + reason);

            await repo.UpsertAsync(new Document
            {
                ProjectId = doc.ProjectId,
                Slug = doc.Slug,
                Title = doc.Title,
                Content = doc.Content,
                DocType = doc.DocType,
                Summary = doc.Summary,
                Tags = tags
            });

            return Results.Ok(new { status = "deleted", entryId });
        });
    }


    private static bool IsAuthorized(HttpContext http, DenCoreOptions options)
    {
        var configured = options.GatewayContract.ServiceToken;
        if (string.IsNullOrWhiteSpace(configured))
            return true;

        var supplied = GetSuppliedServiceToken(http);
        if (supplied is null)
            return false;

        var configuredBytes = Encoding.UTF8.GetBytes(configured);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return configuredBytes.Length == suppliedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(configuredBytes, suppliedBytes);
    }

    private static string? GetSuppliedServiceToken(HttpContext http)
    {
        if (http.Request.Headers.TryGetValue("X-Den-Service-Token", out var token) && !string.IsNullOrWhiteSpace(token))
            return token.ToString();

        var auth = http.Request.Headers.Authorization.ToString();
        const string bearerPrefix = "Bearer ";
        return auth.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? auth[bearerPrefix.Length..].Trim()
            : null;
    }

    private static List<string> BuildTags(
        string space,
        string key,
        Dictionary<string, object?>? metadata,
        Dictionary<string, object?>? provenance)
    {
        var tags = new List<string> { "den-memory", $"memory-space:{space}", $"memory-key:{key}" };
        AddTaggedJson(tags, MetadataTagPrefix, metadata);
        AddTaggedJson(tags, ProvenanceTagPrefix, provenance);
        return tags;
    }

    private static void AddTaggedJson(List<string> tags, string prefix, Dictionary<string, object?>? value)
    {
        if (value is null || value.Count == 0)
            return;
        var json = JsonSerializer.Serialize(value, JsonOptions);
        tags.Add(prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)));
    }

    private static Dictionary<string, object?> DecodeTaggedJson(List<string>? tags, string prefix)
    {
        var encoded = tags?.FirstOrDefault(tag => tag.StartsWith(prefix, StringComparison.Ordinal));
        if (encoded is null)
            return [];

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded[prefix.Length..]));
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(json, JsonOptions) ?? [];
        }
        catch (FormatException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool IsTombstoned(Document doc) => IsTombstoned(doc.Tags);

    private static bool IsTombstoned(List<string>? tags) => tags?.Contains(TombstoneTag) == true;

    private static async Task<TombstoneMemoryEntryRequest?> ReadTombstoneRequestAsync(HttpContext http)
    {
        if (http.Request.ContentLength == 0)
            return null;

        try
        {
            return await JsonSerializer.DeserializeAsync<TombstoneMemoryEntryRequest>(http.Request.Body, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? NormalizeTombstoneReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return null;
        var normalized = SlugSafe().Replace(reason.Trim().ToLowerInvariant(), "-").Trim('-');
        return normalized.Length == 0 ? null : normalized;
    }

    private static MemoryEntryResponse ToEntryResponse(Document doc)
    {
        var parsed = ParseEntryId(doc.Slug);
        return new MemoryEntryResponse(
            EntryId: doc.Slug,
            Key: parsed.Key,
            Space: parsed.Space,
            Content: doc.Content,
            Metadata: DecodeTaggedJson(doc.Tags, MetadataTagPrefix),
            Provenance: DecodeTaggedJson(doc.Tags, ProvenanceTagPrefix),
            UpdatedAt: doc.UpdatedAt);
    }

    private static MemorySearchResultResponse ToSearchResponse(DocumentSummary doc)
    {
        var parsed = ParseEntryId(doc.Slug);
        return new MemorySearchResultResponse(
            EntryId: doc.Slug,
            Key: parsed.Key,
            Space: parsed.Space,
            Snippet: doc.Summary ?? doc.Title,
            UpdatedAt: doc.UpdatedAt,
            Tombstoned: IsTombstoned(doc.Tags));
    }

    private static bool MatchesMemoryQuery(DocumentSummary doc, string query) =>
        doc.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        (doc.Summary?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);

    private static string ToEntryId(string space, string key) => $"memory-{space}-{SlugSafe().Replace(key, "-").Trim('-')}";

    private static (string Space, string Key) ParseEntryId(string entryId)
    {
        if (!entryId.StartsWith("memory-", StringComparison.OrdinalIgnoreCase))
            return ("project", entryId);

        var remainder = entryId["memory-".Length..];
        foreach (var space in InitialSpaces.OrderByDescending(s => s.Length))
        {
            var prefix = space + "-";
            if (remainder.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return (space, remainder[prefix.Length..]);
        }

        return ("project", remainder);
    }

    private static string NormalizeSpace(string? space)
    {
        var value = string.IsNullOrWhiteSpace(space) ? "project" : space.Trim().ToLowerInvariant();
        return SlugSafe().Replace(value, "-").Trim('-') is { Length: > 0 } safe ? safe : "project";
    }

    private static string NormalizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new BadHttpRequestException("Memory key is required.");
        return key.Trim();
    }

    [GeneratedRegex("[^A-Za-z0-9._-]+")]
    private static partial Regex SlugSafe();
}

public sealed record StoreMemoryEntryRequest(
    string Key,
    string? Space,
    string Content,
    Dictionary<string, object?>? Metadata = null,
    Dictionary<string, object?>? Provenance = null);

public sealed record SearchMemoryEntriesRequest(
    string? Query,
    List<string>? Spaces = null,
    int? Limit = null,
    bool? IncludeTombstoned = null);

public sealed record TombstoneMemoryEntryRequest([property: JsonPropertyName("tombstone_reason")] string? TombstoneReason = null);

public sealed record MemorySpacesResponse(string[] Spaces);

public sealed record MemoryEntryResponse(
    string EntryId,
    string Key,
    string Space,
    string Content,
    Dictionary<string, object?> Metadata,
    Dictionary<string, object?>? Provenance,
    DateTime UpdatedAt);

public sealed record MemorySearchResponse(List<MemorySearchResultResponse> Results);

public sealed record MemorySearchResultResponse(
    string EntryId,
    string Key,
    string Space,
    string Snippet,
    DateTime? UpdatedAt,
    bool Tombstoned = false);
