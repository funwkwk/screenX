using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;

namespace ScreenX;

/// <summary>Classic RegisterHotKey-based global hotkeys on a message-only window.</summary>
public sealed class HotkeyManager : IDisposable
{
    readonly HwndSource source;
    readonly Dictionary<int, Action> actions = new();
    int nextId = 1;

    public HotkeyManager()
    {
        source = new HwndSource(new HwndSourceParameters("screenX.hotkeys") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 });
        source.AddHook(WndProc);
    }

    /// <summary>Registers a global hotkey. Returns its id, or 0 when the combination is taken.</summary>
    public int Register(uint modifiers, uint vk, Action action)
    {
        int id = nextId++;
        if (!Native.RegisterHotKey(source.Handle, id, modifiers | Native.MOD_NOREPEAT, vk)) return 0;
        actions[id] = action;
        return id;
    }

    public int Register(Hotkey hk, Action action) => Register(hk.Modifiers, hk.Vk, action);

    public void Unregister(int id)
    {
        if (actions.Remove(id)) Native.UnregisterHotKey(source.Handle, id);
    }

    public void UnregisterAll()
    {
        foreach (var id in actions.Keys.ToArray()) Unregister(id);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && actions.TryGetValue(wParam.ToInt32(), out var a))
        {
            a();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        source.Dispose();
    }
}

/// <summary>
/// A global shortcut: MOD_* flags plus a virtual-key code. Stored in the settings as readable text such as
/// "Ctrl+Shift+S" (modifiers in the order Ctrl, Alt, Shift, Win; "" = no shortcut).
/// </summary>
public readonly record struct Hotkey(uint Modifiers, uint Vk)
{
    public const string DefaultSnip = "Ctrl+Shift+S";
    public const string DefaultRecord = "";
    public const uint VK_SNAPSHOT = 0x2C;

    static readonly Dictionary<uint, string> Names = BuildNames();
    static readonly Dictionary<string, uint> Codes = BuildCodes();

    static Dictionary<uint, string> BuildNames()
    {
        var d = new Dictionary<uint, string>();
        for (uint c = 'A'; c <= 'Z'; c++) d[c] = ((char)c).ToString();
        for (uint c = '0'; c <= '9'; c++) d[c] = ((char)c).ToString();
        for (uint i = 0; i < 24; i++) d[0x70 + i] = "F" + (i + 1);
        for (uint i = 0; i < 10; i++) d[0x60 + i] = "Num" + i;
        d[0x6A] = "Num*"; d[0x6B] = "Num+"; d[0x6D] = "Num-"; d[0x6E] = "Num."; d[0x6F] = "Num/";
        d[VK_SNAPSHOT] = "PrintScreen";
        d[0x13] = "Pause"; d[0x91] = "ScrollLock";
        d[0x08] = "Backspace"; d[0x09] = "Tab"; d[0x0D] = "Enter"; d[0x20] = "Space";
        d[0x21] = "PageUp"; d[0x22] = "PageDown"; d[0x23] = "End"; d[0x24] = "Home";
        d[0x25] = "Left"; d[0x26] = "Up"; d[0x27] = "Right"; d[0x28] = "Down";
        d[0x2D] = "Insert"; d[0x2E] = "Delete";
        d[0xBA] = ";"; d[0xBB] = "="; d[0xBC] = ","; d[0xBD] = "-"; d[0xBE] = "."; d[0xBF] = "/";
        d[0xC0] = "`"; d[0xDB] = "["; d[0xDC] = "\\"; d[0xDD] = "]"; d[0xDE] = "'";
        return d;
    }

    static Dictionary<string, uint> BuildCodes()
    {
        var d = Names.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);
        d["PrtSc"] = VK_SNAPSHOT; d["PrtScn"] = VK_SNAPSHOT; d["Print"] = VK_SNAPSHOT; d["Snapshot"] = VK_SNAPSHOT;
        d["Return"] = 0x0D; d["Del"] = 0x2E; d["Ins"] = 0x2D; d["PgUp"] = 0x21; d["PgDn"] = 0x22;
        return d;
    }

    /// <summary>True for keys that may be the main key of a shortcut (not a modifier, not Esc).</summary>
    public static bool IsSupportedKey(uint vk) => Names.ContainsKey(vk);

    /// <summary>A shortcut needs a modifier and a main key; Print Screen may also stand alone.</summary>
    public bool IsValid => IsSupportedKey(Vk) && ((Modifiers & 0xF) != 0 || Vk == VK_SNAPSHOT);

    public override string ToString()
    {
        var parts = new List<string>();
        if ((Modifiers & Native.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((Modifiers & Native.MOD_ALT) != 0) parts.Add("Alt");
        if ((Modifiers & Native.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((Modifiers & Native.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(Names.TryGetValue(Vk, out var n) ? n : $"0x{Vk:X2}");
        return string.Join("+", parts);
    }

    /// <summary>Parses "Ctrl+Shift+S" style text (any order and case). Fails for empty or invalid text.</summary>
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim();
        // The key is after the last '+' separator; a trailing '+' belongs to the key ("Ctrl+Num+").
        int sep = t.EndsWith('+') ? (t.Length >= 2 ? t.LastIndexOf('+', t.Length - 2) : -1) : t.LastIndexOf('+');
        string keyName = t[(sep + 1)..].Trim();
        var tokens = new List<string>();
        if (sep > 0) tokens.AddRange(t[..sep].Split('+').Select(x => x.Trim()));
        tokens.Add(keyName);

        uint mods = 0;
        for (int i = 0; i < tokens.Count - 1; i++)
        {
            switch (tokens[i].ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= Native.MOD_CONTROL; break;
                case "alt" or "option": mods |= Native.MOD_ALT; break;
                case "shift": mods |= Native.MOD_SHIFT; break;
                case "win" or "windows" or "super" or "meta": mods |= Native.MOD_WIN; break;
                default: return false;
            }
        }
        if (!Codes.TryGetValue(tokens[^1], out uint vk)) return false;
        hotkey = new Hotkey(mods, vk);
        return hotkey.IsValid;
    }

    /// <summary>Canonical form of stored text: "" when empty or invalid.</summary>
    public static string Normalize(string? text) => TryParse(text, out var hk) ? hk.ToString() : "";
}
