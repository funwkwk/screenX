using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ScreenX;

/// <summary>Notification-area icon with the app menu.</summary>
public sealed class Tray : IDisposable
{
    readonly NotifyIcon icon;
    readonly App app;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public Tray(App app)
    {
        this.app = app;
        icon = new NotifyIcon
        {
            Icon = LoadIcon(SystemInformation.SmallIconSize),
            Text = "screenX — Ctrl+Shift+S",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) app.StartCapture(Settings.Current.LastSnipMode); };
    }

    public static Icon LoadIcon(Size size)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("screenX.ico");
        return s != null ? new Icon(s, size) : SystemIcons.Application;
    }

    ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
        var s = Settings.Current;

        m.Items.Add(new ToolStripMenuItem("New snip", null, (_, _) => app.StartCapture(s.LastSnipMode)) { ShortcutKeyDisplayString = "Ctrl+Shift+S", Font = new Font(m.Font, FontStyle.Bold) });
        m.Items.Add(new ToolStripMenuItem("Full screen snip", null, (_, _) => app.StartCapture(CaptureMode.Fullscreen)));
        m.Items.Add(new ToolStripMenuItem("Screen record", null, (_, _) => app.StartCapture(CaptureMode.Record)));
        m.Items.Add(new ToolStripMenuItem("Color picker", null, (_, _) => app.StartCapture(CaptureMode.ColorPicker)));
        m.Items.Add(new ToolStripMenuItem("Text extractor", null, (_, _) => app.StartCapture(CaptureMode.Text)));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("Open screenshots folder", null, (_, _) => OpenFolder(s.ScreenshotFolder)));
        m.Items.Add(new ToolStripMenuItem("Open recordings folder", null, (_, _) => OpenFolder(s.VideoFolder)));
        m.Items.Add(new ToolStripSeparator());

        var settings = new ToolStripMenuItem("Settings");
        settings.DropDownItems.Add(Toggle("Auto-save screenshots", () => s.AutoSave, v => s.AutoSave = v));
        settings.DropDownItems.Add(Toggle("Show cursor in recordings", () => s.RecordCursor, v => s.RecordCursor = v));
        var fps = new ToolStripMenuItem("Recording frame rate");
        foreach (var f in new[] { 15, 24, 30, 60 })
        {
            var item = new ToolStripMenuItem($"{f} fps") { Checked = s.RecordFps == f };
            item.Click += (_, _) =>
            {
                s.RecordFps = f; s.Save();
                foreach (ToolStripMenuItem i in fps.DropDownItems) i.Checked = i == item;
            };
            fps.DropDownItems.Add(item);
        }
        settings.DropDownItems.Add(fps);
        settings.DropDownItems.Add(Toggle("Start with Windows", IsAutoStart, SetAutoStart));
        m.Items.Add(settings);

        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => app.Quit()));
        return m;
    }

    static ToolStripMenuItem Toggle(string text, Func<bool> get, Action<bool> set)
    {
        var item = new ToolStripMenuItem(text) { Checked = get(), CheckOnClick = true };
        item.CheckedChanged += (_, _) => { set(item.Checked); Settings.Current.Save(); };
        return item;
    }

    static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        WinUtil.OpenPath(path);
    }

    static bool IsAutoStart()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("screenX") != null;
    }

    static void SetAutoStart(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue("screenX", $"\"{Environment.ProcessPath}\" --background");
        else k.DeleteValue("screenX", false);
    }

    public void Balloon(string title, string text)
    {
        icon.BalloonTipTitle = title;
        icon.BalloonTipText = text;
        icon.ShowBalloonTip(4000);
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
    }
}
