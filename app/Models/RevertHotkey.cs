using System.Globalization;

namespace DesktopSplitter.Models;

/// <summary>Portable parser; values are Win32 MOD_* flags and virtual-key codes.</summary>
public sealed record RevertHotkey(uint Modifiers, uint VirtualKey)
{
    public const string DefaultGesture = "Ctrl+Alt+Shift+R";
    public const uint Alt = 1, Control = 2, Shift = 4, Windows = 8;

    public static RevertHotkey Parse(string text)
    {
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw new FormatException("Use at least one modifier and a key, e.g. Ctrl+Alt+Shift+R.");
        uint modifiers = 0;
        foreach (string part in parts[..^1])
        {
            uint flag = part.ToUpperInvariant() switch
            { "CTRL" or "CONTROL" => Control, "ALT" => Alt, "SHIFT" => Shift, "WIN" or "WINDOWS" => Windows,
              _ => throw new FormatException("Modifiers: Ctrl, Alt, Shift, Win.") };
            if ((modifiers & flag) != 0) throw new FormatException("A modifier was repeated.");
            modifiers |= flag;
        }
        string key = parts[^1].ToUpperInvariant();
        uint code;
        if (key is "ESC" or "ESCAPE") code = 0x1B;
        else if (key.Length == 1 && ((key[0] >= 'A' && key[0] <= 'Z') || (key[0] >= '0' && key[0] <= '9'))) code = key[0];
        else if (key.StartsWith('F') && int.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int f) && f >= 1 && f <= 24)
            code = (uint)(0x70 + f - 1);
        else throw new FormatException("Keys: A–Z, 0–9, Esc or F1–F24 (except reserved F12).");
        if (code == 0x7B) throw new FormatException("F12 is reserved for the Windows debugger.");
        if (modifiers == (Control | Shift) && code == 0x1B)
            throw new FormatException("Ctrl+Shift+Esc is reserved for Task Manager.");
        return new(modifiers, code);
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if ((Modifiers & Control) != 0) parts.Add("Ctrl");
        if ((Modifiers & Alt) != 0) parts.Add("Alt");
        if ((Modifiers & Shift) != 0) parts.Add("Shift");
        if ((Modifiers & Windows) != 0) parts.Add("Win");
        parts.Add(VirtualKey == 0x1B ? "Esc" : VirtualKey >= 0x70 && VirtualKey <= 0x87
            ? $"F{VirtualKey - 0x70 + 1}" : ((char)VirtualKey).ToString());
        return string.Join("+", parts);
    }
}
