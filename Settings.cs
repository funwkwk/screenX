using System;
using System.IO;
using System.Text.Json;

namespace ScreenX;

public enum CaptureMode { Rectangle, Freeform, Window, Fullscreen, Record, ColorPicker, Text }

public sealed class Settings
{
    public CaptureMode LastSnipMode { get; set; } = CaptureMode.Rectangle;
    public bool AutoSave { get; set; } = true;
    /// <summary>Editor: copy the snip to the clipboard again after every mark-up change.</summary>
    public bool AutoCopyChanges { get; set; } = true;
    /// <summary>Editor: ask before closing a snip with unsaved mark-ups.</summary>
    public bool AskToSaveEdited { get; set; } = false;
    /// <summary>Editor "New": open each snip in its own window instead of reusing the current one.</summary>
    public bool MultipleWindows { get; set; } = false;
    public bool AddBorder { get; set; } = false;
    public string BorderColor { get; set; } = "#E81123";
    public double BorderSize { get; set; } = 2;
    /// <summary>Also open screenX with the Print Screen key.</summary>
    public bool UsePrintScreen { get; set; } = false;
    /// <summary>Global shortcut that opens a new snip, e.g. "Ctrl+Shift+S"; "" = none.</summary>
    public string SnipHotkey { get; set; } = Hotkey.DefaultSnip;
    /// <summary>Global shortcut that starts (or stops) a screen recording; "" = none (the default).</summary>
    public string RecordHotkey { get; set; } = Hotkey.DefaultRecord;
    /// <summary>Play a shutter sound when a still snip is taken.</summary>
    public bool PlaySnipSound { get; set; } = true;
    /// <summary>Stay on screen with the markup bar after selecting a snip (blue toggle in the toolbar).</summary>
    public bool MarkupAfterSnip { get; set; } = true;
    public string PenColor { get; set; } = "#E81123";
    public double PenSize { get; set; } = 4;
    public string HighlighterColor { get; set; } = "#FCE100";
    public double HighlighterSize { get; set; } = 18;
    /// <summary>Shape fill colour; empty = no fill.</summary>
    public string ShapeFill { get; set; } = "";
    public string ShapeOutline { get; set; } = "#E81123";
    public double ShapeSize { get; set; } = 4;
    public bool RecordCursor { get; set; } = true;
    public int RecordFps { get; set; } = 30;
    /// <summary>Seconds of countdown after pressing Start before a recording begins; 0 = off.</summary>
    public int RecordCountdown { get; set; } = 3;
    public bool RecordMic { get; set; } = false;
    public bool RecordSystemAudio { get; set; } = true;
    /// <summary>Editor "New" button: seconds to wait before the snip starts.</summary>
    public int SnipDelay { get; set; } = 0;
    public string ScreenshotFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "screenX");
    public string VideoFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "screenX");

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "screenX", "settings.json");

    public static Settings Current { get; } = Load();

    static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return Fix(JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings());
        }
        catch { }
        return new Settings();
    }

    /// <summary>Older files have no shortcut entries (defaults are kept); unreadable text falls back to the default.</summary>
    static Settings Fix(Settings s)
    {
        s.SnipHotkey = s.SnipHotkey == null ? Hotkey.DefaultSnip
            : s.SnipHotkey.Trim() == "" ? "" : Hotkey.TryParse(s.SnipHotkey, out var a) ? a.ToString() : Hotkey.DefaultSnip;
        s.RecordHotkey = Hotkey.Normalize(s.RecordHotkey);
        s.RecordCountdown = Math.Clamp(s.RecordCountdown, 0, 10);
        return s;
    }

    /// <summary>Text for messages, e.g. "Press Ctrl+Shift+S to snip" or, with no shortcut, a hint at the tray icon.</summary>
    public string SnipHotkeyHint(string withKey, string withoutKey) =>
        string.IsNullOrEmpty(SnipHotkey) ? withoutKey : string.Format(withKey, SnipHotkey);

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
