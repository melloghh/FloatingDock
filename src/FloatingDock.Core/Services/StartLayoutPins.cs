using System.Text.Json;

namespace FloatingDock.Core.Services;

public sealed record StartLayoutPin(string Kind, string Value);

/// <summary>Reads only pins from Export-StartLayout, preserving Start order and flattening pinned folders.</summary>
public static class StartLayoutPins
{
    public static IReadOnlyList<StartLayoutPin> Parse(string json)
    {
        if (json.Length > 1024 * 1024) throw new InvalidDataException("Start layout is too large.");
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        if (!doc.RootElement.TryGetProperty("pinnedList", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The exported Start layout has no pinnedList.");
        var pins = new List<StartLayoutPin>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Read(JsonElement array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (pins.Count >= 256) throw new InvalidDataException("Start layout has too many pins.");
                if (item.ValueKind != JsonValueKind.Object) continue;
                foreach (var key in new[] { "desktopAppLink", "desktopAppId", "packagedAppId" })
                {
                    if (!item.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String) continue;
                    var text = value.GetString();
                    if (!string.IsNullOrWhiteSpace(text) && text.Length <= 32768 && seen.Add(key + ":" + text)) pins.Add(new(key, text));
                    break;
                }
                if (item.TryGetProperty("folder", out var folder) && folder.ValueKind == JsonValueKind.Object && folder.TryGetProperty("pinnedList", out var children) && children.ValueKind == JsonValueKind.Array) Read(children);
            }
        }
        Read(list);
        return pins;
    }
}
