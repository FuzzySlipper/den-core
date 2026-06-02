using System.Collections;
using System.Text.Json;

namespace DenCore.Service.Tools;

internal static class ToolArgumentJson
{
    public static List<string>? ParseStringArray(object? value, string fieldName)
    {
        if (value is null)
            return null;

        if (value is string text)
            return ParseStringArrayText(text, fieldName);

        if (value is JsonElement element)
            return ParseStringArrayElement(element, fieldName);

        if (value is IEnumerable<string> strings)
            return NormalizeStrings(strings);

        if (value is IEnumerable enumerable)
        {
            var parsed = new List<string>();
            foreach (var item in enumerable)
            {
                if (item is null)
                    continue;
                if (item is not string itemText)
                    throw new InvalidOperationException($"{fieldName} entries must be strings; found {item.GetType().Name}.");
                if (!string.IsNullOrWhiteSpace(itemText))
                    parsed.Add(itemText);
            }
            return parsed.Count > 0 ? parsed : null;
        }

        throw new InvalidOperationException($"{fieldName} must be a JSON array of strings.");
    }

    private static List<string>? ParseStringArrayText(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(value);
            return ParseStringArrayElement(doc.RootElement, fieldName);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{fieldName} must be a valid JSON array of strings.", ex);
        }
    }

    private static List<string>? ParseStringArrayElement(JsonElement element, string fieldName)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        if (element.ValueKind == JsonValueKind.String)
            return ParseStringArrayText(element.GetString() ?? string.Empty, fieldName);

        if (element.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"{fieldName} must be a JSON array of strings; found {element.ValueKind}.");

        var parsed = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException($"{fieldName} entries must be strings; found {item.ValueKind}.");
            var text = item.GetString();
            if (!string.IsNullOrWhiteSpace(text))
                parsed.Add(text);
        }

        return parsed.Count > 0 ? parsed : null;
    }

    private static List<string>? NormalizeStrings(IEnumerable<string> values)
    {
        var parsed = values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        return parsed.Count > 0 ? parsed : null;
    }
}
