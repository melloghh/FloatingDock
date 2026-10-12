namespace FloatingDock.Core.Services;

/// <summary>Parses strings like "Ctrl+Alt+Space" into RegisterHotKey modifiers and a Windows virtual-key code.</summary>
public static class HotkeyParser
{
    public const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    private static readonly Dictionary<string, uint> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["space"] = 0x20, ["enter"] = 0x0D, ["return"] = 0x0D, ["esc"] = 0x1B, ["escape"] = 0x1B, ["tab"] = 0x09, ["backspace"] = 0x08,
        ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28, ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pagedown"] = 0x22,
        ["insert"] = 0x2D, ["delete"] = 0x2E, ["del"] = 0x2E,
    };

    public static bool TryParse(string? text, out uint modifiers, out uint vk)
    {
        modifiers = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        bool haveKey = false;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= ModControl; continue;
                case "alt": modifiers |= ModAlt; continue;
                case "shift": modifiers |= ModShift; continue;
                case "win": case "windows": case "super": modifiers |= ModWin; continue;
            }
            if (haveKey) return false;
            if (Named.TryGetValue(raw, out var code)) vk = code;
            else if (raw.Length == 1 && char.IsAsciiLetterOrDigit(raw[0])) vk = char.ToUpperInvariant(raw[0]);
            else if (raw.Length is 2 or 3 && (raw[0] is 'f' or 'F') && int.TryParse(raw.AsSpan(1), out var n) && n is >= 1 and <= 24) vk = (uint)(0x6F + n);
            else return false;
            haveKey = true;
        }
        // a global hotkey without any modifier would hijack a normal key
        return haveKey && modifiers != 0;
    }
}
