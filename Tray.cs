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
        icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) app.StartCapture(Settings.Current.LastSnipMode); };    }

    public static Icon LoadIcon(Size size)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("screenX.ico");
        return s != null ? new Icon(s, size) : SystemIcons.Application;
    }

    ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
        var s = Settings.Current;

        m.Items.Add(new ToolStripMenuItem("Open screenX", null, (_, _) => app.OpenEditor()));
        m.Items.Add(new ToolStripMenuItem("New snip", null, (_, _) => app.StartCapture(s.LastSnipMode)) { ShortcutKeyDisplayString = "Ctrl+Shift+S", Font = new Font(m.Font, FontStyle.Bold) });
        m.Items.Add(new ToolStripMenuItem("Full screen snip", null, (_, _) => app.StartCapture(CaptureMode.Fullscreen)));
        m.Items.Add(new ToolStripMenuItem("Screen record", null, (_, _) => app.StartCapture(CaptureMode.Record)));
        m.Items.Add(new ToolStripMenuItem("Color picker", null, (_, _) => app.StartCapture(CaptureMode.ColorPicker)));
        m.Items.Add(new ToolStripMenuItem("Text extractor", null, (_, _) => app.StartCapture(CaptureMode.Text)));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("Open screenshots folder", null, (_, _) => OpenFolder(s.ScreenshotFolder)));
        m.Items.Add(new ToolStripMenuItem("Open recordings folder", null, (_, _) => OpenFolder(s.VideoFolder)));
        m.Items.Add(new ToolStripSeparator());

        m.Items.Add(new ToolStripMenuItem("Settings", null, (_, _) => SettingsWindow.Open()));

        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => app.Quit()));
        return m;
    }

    static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        WinUtil.OpenPath(path);
    }

    public static bool IsAutoStart()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("screenX") != null;
    }

    public static void SetAutoStart(bool on)
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
