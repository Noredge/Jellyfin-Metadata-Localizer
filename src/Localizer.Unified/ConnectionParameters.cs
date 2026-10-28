using System.Text.Json;

namespace Localizer.Unified;

public sealed class ConnectionParameterException(string parameter) : ArgumentException("InvalidConnectionParameter")
{
    public string Parameter { get; } = parameter;
}

public static class ConnectionParameters
{
    public static void Validate(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new ConnectionParameterException("parameters");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            var p = property.Value;
            var valid = property.Name switch
            {
                "MaxOutputTokens" or "OverviewMaxOutputTokens" => p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var value) && value is >= 64 and <= 32768,
                "UseJsonResponseFormat" => p.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "Store" => p.ValueKind is JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False,
                "TokenParameter" => p.ValueKind == JsonValueKind.String && p.GetString() is "max_tokens" or "max_completion_tokens",
                "ReasoningEffort" => p.ValueKind == JsonValueKind.Null || p.ValueKind == JsonValueKind.String && p.GetString() is "none" or "minimal" or "low" or "medium" or "high",
                _ => false
            };
            if (!valid || !seen.Add(property.Name)) throw new ConnectionParameterException(property.Name);
        }
    }
}
