using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Promenade;

// The keys a walk handles, whatever the OS calls them. Move: WASD/arrows, turn Q/E, jump Space, fly Tab, exit Esc.
// View: lens 1–5, lens zoom -/=, two-point T, display mode V, screenshot P. Body: eye height H, measure M.
// Scene: save view N, previous/next saved view , and ., sun time [ and ]. Frame: aspect F, guides G.
// Block mode B. Debug HUD F3 and ` (grave). Record R, clip X, floor up/down Page Up/Page Down.
// These are actions: KeyMap says which physical key each one is (keymap.json, WalkKeys).
internal enum WalkKey
{
    W, A, S, D, Q, E, Space, Tab, Escape, Left, Right, Up, Down,
    Digit1, Digit2, Digit3, Digit4, Digit5, Minus, Equal, T, V, P, H, M, N, Comma, Period,
    LeftBracket, RightBracket, F, G, B, F3, Grave, R, X, PageUp, PageDown,
}

// Which key each WalkKey is, from the user's keymap.json (read at every walk start). Pure, so tests/ can run it.
// A key name means the key at that position on a US keyboard. macOS key codes are positional already, so on
// AZERTY/QWERTZ Macs WASD sits where it does on QWERTY with no setting. Windows virtual keys follow the active
// layout instead (VK 'W' is the key labelled W), so there "layout" moves the letters back to their US positions.
// ponytail: the presets move letters only; Windows punctuation keeps its US virtual key (VK_OEM_*), whatever
// label that key has. Scan codes (lParam bits 16–23) would make Windows positional like macOS.
internal sealed class KeyMap
{
    internal static readonly string[] Layouts = ["qwerty", "azerty", "qwertz"];

    // Every key a WalkKey may be. Escape is not one: Esc always exits and is never anything else.
    internal static readonly string[] KeyNames =
    [
        .. Enumerable.Range('A', 26).Select(c => ((char)c).ToString()),
        .. Enumerable.Range(0, 10).Select(d => d.ToString()),
        "Space", "Tab", "Left", "Right", "Up", "Down", "PageUp", "PageDown",
        .. Enumerable.Range(1, 12).Select(f => "F" + f),
        "Minus", "Equal", "Comma", "Period", "LeftBracket", "RightBracket", "Grave", "Semicolon", "Quote", "Slash", "Backslash",
    ];

    internal string Layout { get; private set; } = "qwerty";
    internal bool InvertMouseY { get; private set; }
    readonly Dictionary<WalkKey, string> _keys = Enum.GetValues<WalkKey>().ToDictionary(k => k, DefaultName);

    internal string this[WalkKey key] => _keys[key];

    internal static string DefaultName(WalkKey key) => key.ToString().Replace("Digit", "");

    internal static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Promenade", "keymap.json");

    // The user's map, or the defaults when there is no file. Never throws: what is wrong goes to problems
    // and that setting (or the whole file, if unreadable) keeps its default.
    internal static KeyMap Load(List<string> problems)
    {
        try { return File.Exists(FilePath) ? Parse(File.ReadAllText(FilePath), problems) : new KeyMap(); }
        catch (Exception ex)
        {
            problems.Add($"{FilePath}: {ex.Message}; using the default keys");
            return new KeyMap();
        }
    }

    internal static KeyMap Parse(string json, List<string> problems)
    {
        var map = new KeyMap();
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) { problems.Add("keymap.json is not a JSON object"); return map; }

        foreach (var p in root.EnumerateObject())
            switch (p.Name)
            {
                case "layout":
                    string? layout = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()!.ToLowerInvariant() : null;
                    if (layout is not null && Layouts.Contains(layout)) map.Layout = layout;
                    else problems.Add($"layout: {p.Value} is not one of {string.Join(", ", Layouts)}");
                    break;
                case "invertMouseY":
                    if (p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) map.InvertMouseY = p.Value.GetBoolean();
                    else problems.Add($"invertMouseY: {p.Value} is not true or false");
                    break;
                case "keys":
                    if (p.Value.ValueKind != JsonValueKind.Object) { problems.Add("keys: not an object of action: key"); break; }
                    foreach (var k in p.Value.EnumerateObject()) map.Set(k.Name, k.Value, problems);
                    break;
                default:
                    problems.Add($"{p.Name}: not a setting (layout, invertMouseY, keys)");
                    break;
            }

        // A key doing two things: the actions the file moved onto it go back to their defaults. A default can
        // only clash with a moved one, so this ends once no moved action clashes.
        for (bool clashed = true; clashed;)
        {
            clashed = false;
            foreach (var group in map._keys.GroupBy(kv => kv.Value).Where(g => g.Count() > 1).ToList())
                foreach (var (action, name) in group.Where(kv => kv.Value != DefaultName(kv.Key)).ToList())
                {
                    var others = group.Where(kv => kv.Key != action).Select(kv => kv.Key);
                    problems.Add($"keys.{action}: {name} is already the key for {string.Join(", ", others)}; keeping {DefaultName(action)}");
                    map._keys[action] = DefaultName(action);
                    clashed = true;
                }
        }
        return map;
    }

    void Set(string action, JsonElement value, List<string> problems)
    {
        if (!Enum.TryParse<WalkKey>(action, out var key) || !Enum.IsDefined(key) || action != key.ToString())
        {
            problems.Add($"keys.{action}: not an action (see the default file from WalkKeys)");
            return;
        }
        if (key == WalkKey.Escape) { problems.Add("keys.Escape: Esc always exits and cannot be moved"); return; }
        string? name = value.ValueKind == JsonValueKind.String
            ? KeyNames.FirstOrDefault(n => n.Equals(value.GetString(), StringComparison.OrdinalIgnoreCase))
            : null;
        if (name is null) problems.Add($"keys.{action}: {value} is not a key name; keeping {DefaultName(key)}");
        else _keys[key] = name;
    }

    // macOS virtual key code (ANSI positions) of a key name, Escape included.
    internal static ushort MacCode(string name) => name switch
    {
        "A" => 0, "S" => 1, "D" => 2, "F" => 3, "H" => 4, "G" => 5, "Z" => 6, "X" => 7, "C" => 8, "V" => 9,
        "B" => 11, "Q" => 12, "W" => 13, "E" => 14, "R" => 15, "Y" => 16, "T" => 17, "O" => 31, "U" => 32,
        "I" => 34, "P" => 35, "L" => 37, "J" => 38, "K" => 40, "N" => 45, "M" => 46,
        "1" => 18, "2" => 19, "3" => 20, "4" => 21, "5" => 23, "6" => 22, "7" => 26, "8" => 28, "9" => 25, "0" => 29,
        "Equal" => 24, "Minus" => 27, "RightBracket" => 30, "LeftBracket" => 33, "Quote" => 39, "Semicolon" => 41,
        "Backslash" => 42, "Comma" => 43, "Slash" => 44, "Period" => 47, "Grave" => 50,
        "Tab" => 48, "Space" => 49, "Escape" => 53, "PageUp" => 116, "PageDown" => 121,
        "Left" => 123, "Right" => 124, "Down" => 125, "Up" => 126,
        // F keys need "Use F1, F2, etc. keys as standard function keys", or fn.
        "F1" => 122, "F2" => 120, "F3" => 99, "F4" => 118, "F5" => 96, "F6" => 97,
        "F7" => 98, "F8" => 100, "F9" => 101, "F10" => 109, "F11" => 103, "F12" => 111,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name),
    };

    // Windows virtual key of a key name, Escape included; letters moved to their US positions for the layout.
    // The punctuation ones (VK_OEM_*) are named for the US layout.
    internal int WinVk(string name) => name switch
    {
        { Length: 1 } when char.IsAsciiLetterUpper(name[0]) => (Layout, name[0]) switch
        {
            ("azerty", 'A') => 'Q', ("azerty", 'Q') => 'A', ("azerty", 'W') => 'Z', ("azerty", 'Z') => 'W',
            ("qwertz", 'Y') => 'Z', ("qwertz", 'Z') => 'Y',
            (_, var c) => c,
        },
        { Length: 1 } => name[0], // digits
        "Space" => 0x20, "Tab" => 0x09, "Escape" => 0x1B, "PageUp" => 0x21, "PageDown" => 0x22, // VK_PRIOR, VK_NEXT
        "Left" => 0x25, "Up" => 0x26, "Right" => 0x27, "Down" => 0x28,
        _ when name.StartsWith('F') && int.TryParse(name.AsSpan(1), out int f) => 0x6F + f, // VK_F1 = 0x70
        "Semicolon" => 0xBA, "Equal" => 0xBB, "Comma" => 0xBC, "Minus" => 0xBD, "Period" => 0xBE, "Slash" => 0xBF,
        "Grave" => 0xC0, "LeftBracket" => 0xDB, "Backslash" => 0xDC, "RightBracket" => 0xDD, "Quote" => 0xDE,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name),
    };

    // The default keymap.json: every action listed with its default key, so the file documents itself.
    internal static string DefaultJson()
    {
        var text = new StringBuilder("""
            // Promenade keymap, read at every walk start (Walk). Delete this file to get the defaults back.
            // "keys" maps an action (left) to a key (right). A key name means the key at that position on a US
            // keyboard: A-Z, 0-9, Space, Tab, Left, Right, Up, Down, PageUp, PageDown, F1-F12, Minus, Equal, Comma,
            // Period, LeftBracket, RightBracket, Grave, Semicolon, Quote, Slash, Backslash.
            // macOS reads keys by position, so WASD works on AZERTY and QWERTZ keyboards as is and "layout" changes
            // nothing. Windows reads them by label, following the active layout: set "layout" to "azerty" or
            // "qwertz" to keep the letters at their US positions (AZERTY: "W" is the key labelled Z, "A" is Q,
            // "Q" is A; QWERTZ swaps Y and Z). Other keys stay their US Windows keys, whatever their label.
            // Esc always exits and cannot be moved. A key used twice, or an unknown name, keeps its default
            // (the walk says so on the command line). Shift (run) and the mouse are not remappable.
            {
              "layout": "qwerty",
              "invertMouseY": false,
              "keys": {

            """);
        var actions = Enum.GetValues<WalkKey>().Where(k => k != WalkKey.Escape).ToList();
        foreach (var key in actions)
            text.Append($"    \"{key}\": \"{DefaultName(key)}\"{(key == actions[^1] ? "" : ",")} // {Describe(key)}\n");
        return text.Append("  }\n}\n").ToString();
    }

    static string Describe(WalkKey key) => key switch
    {
        WalkKey.W or WalkKey.Up => "forward", WalkKey.S or WalkKey.Down => "back",
        WalkKey.A or WalkKey.Left => "left", WalkKey.D or WalkKey.Right => "right",
        WalkKey.Q => "turn left (fly: down)", WalkKey.E => "turn right (fly: up)",
        WalkKey.Space => "jump", WalkKey.Tab => "walk / fly",
        WalkKey.Digit1 => "lens 14 mm", WalkKey.Digit2 => "lens 18 mm", WalkKey.Digit3 => "lens 24 mm",
        WalkKey.Digit4 => "lens 35 mm", WalkKey.Digit5 => "lens 50 mm",
        WalkKey.Minus => "lens wider", WalkKey.Equal => "lens longer", WalkKey.T => "two-point perspective",
        WalkKey.V => "display mode", WalkKey.P => "screenshot", WalkKey.H => "eye height", WalkKey.M => "measure",
        WalkKey.N => "save view", WalkKey.Comma => "previous view", WalkKey.Period => "next view",
        WalkKey.LeftBracket => "sun earlier", WalkKey.RightBracket => "sun later",
        WalkKey.F => "frame aspect", WalkKey.G => "composition guides", WalkKey.B => "block mode",
        WalkKey.F3 or WalkKey.Grave => "debug HUD", WalkKey.R => "record", WalkKey.X => "clip",
        WalkKey.PageUp => "floor up", WalkKey.PageDown => "floor down",
        _ => "",
    };
}
