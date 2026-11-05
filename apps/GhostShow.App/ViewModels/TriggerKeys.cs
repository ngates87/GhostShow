namespace GhostShow.App.ViewModels;

/// <summary>
/// Which keyboard keys can press a show button, and how they're shown. Keys are stored by Avalonia's name for them
/// ("F5", "D1" for the 1 key, "NumPad1"), which is plain text, so the show file doesn't depend on Avalonia.
/// </summary>
public static class TriggerKeys
{
    /// <summary>Keys that only change other keys: while learning a key, they're waited past.</summary>
    static readonly HashSet<string> Modifiers =
        ["LeftShift", "RightShift", "LeftCtrl", "RightCtrl", "LeftAlt", "RightAlt", "LWin", "RWin", "CapsLock", "Capital", "NumLock", "Scroll"];

    /// <summary>The numeric keypad's symbols, and keys a USB pedal or presentation clicker often sends.</summary>
    static readonly Dictionary<string, string> OtherKeys = new()
    {
        ["Multiply"] = "Num *", ["Add"] = "Num +", ["Subtract"] = "Num -", ["Divide"] = "Num /", ["Decimal"] = "Num .",
        ["PageUp"] = "Page Up", ["Prior"] = "Page Up", ["PageDown"] = "Page Down", ["Next"] = "Page Down",
        ["Insert"] = "Insert", ["Pause"] = "Pause",
    };

    public static bool IsModifier(string key) => Modifiers.Contains(key);

    /// <summary>
    /// Letters, digits, the numeric keypad, F keys and a few others. Everything else is left to GhostShow: Space plays
    /// and pauses, F2 renames, and Enter, Escape, Tab, Delete, Backspace, Home, End and the arrows work the screens.
    /// </summary>
    public static bool CanUse(string key) => key switch
    {
        "F2" => false,
        [>= 'A' and <= 'Z'] => true,
        ['D', >= '0' and <= '9'] => true,
        ['N', 'u', 'm', 'P', 'a', 'd', >= '0' and <= '9'] => true,
        ['F', >= '1' and <= '9', ..] => int.TryParse(key[1..], out _),
        _ => OtherKeys.ContainsKey(key),
    };

    /// <summary>
    /// Keys that type into a field: letters, digits and the numeric keypad. While a field has the cursor these go to
    /// the field; the rest (F keys, Page Up and Down, Insert, Pause) type nothing, so they press their buttons anyway.
    /// </summary>
    public static bool TypesText(string key) => key switch
    {
        [>= 'A' and <= 'Z'] => true,
        ['D', >= '0' and <= '9'] => true,
        ['N', 'u', 'm', 'P', 'a', 'd', >= '0' and <= '9'] => true,
        "Multiply" or "Add" or "Subtract" or "Divide" or "Decimal" => true,
        _ => false,
    };

    /// <summary>How a key is shown: "D1" as "1", "NumPad1" as "Num 1", "F5" as itself.</summary>
    public static string Label(string key) => key switch
    {
        ['D', >= '0' and <= '9'] => key[1..],
        ['N', 'u', 'm', 'P', 'a', 'd', >= '0' and <= '9'] => $"Num {key[^1]}",
        _ => OtherKeys.GetValueOrDefault(key, key),
    };
}
