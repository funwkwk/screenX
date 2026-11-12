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
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

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
