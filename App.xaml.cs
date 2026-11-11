using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ScreenX;

public partial class App : Application
{
    const string MutexName = @"Local\screenX.SingleInstance";
    static readonly (string Arg, CaptureMode Mode)[] Commands =
    {
        ("--snip", CaptureMode.Rectangle), ("--fullscreen", CaptureMode.Fullscreen), ("--record", CaptureMode.Record),
        ("--color", CaptureMode.ColorPicker), ("--text", CaptureMode.Text),
    };

    Mutex? mutex;
    Tray? tray;
    HotkeyManager? hotkeys;
    OverlayWindow? overlay;
    RecordingController? recorder;
    bool capturing;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args.Select(a => a.ToLowerInvariant()).ToArray();

        if (args.Length >= 2 && args[0] == "--selftest")
        {
            _ = SelfTest.RunAsync(args[1]).ContinueWith(_ => Dispatcher.Invoke(Shutdown));
            return;
        }

        if (args.Length >= 2 && args[0] == "--lens-file")
        {
            // Test hook: run the Google Lens flow on an image file instead of a snip.
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(Path.GetFullPath(e.Args[1]));
            bmp.EndInit();
            bmp.Freeze();
            // A click on this button gives screenX the same foreground rights it has after a real snip click.
            var btn = new Button { Content = "Search sample with Google Lens", Padding = new Thickness(16, 10, 16, 10), Margin = new Thickness(20) };
            System.Windows.Automation.AutomationProperties.SetName(btn, "Search sample with Google Lens");
            var host = new Window { Title = "screenX Lens test", SizeToContent = SizeToContent.WidthAndHeight, Topmost = true, Content = btn, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            btn.Click += async (_, _) =>
            {
                host.Hide();
                await GoogleLens.SearchAsync(bmp);
                await Task.Delay(3000);
                Shutdown();
            };
            host.Show();
            return;
        }

        mutex = new Mutex(true, MutexName, out bool first);
        CaptureMode? requested = Commands.Where(c => args.Contains(c.Arg)).Select(c => (CaptureMode?)c.Mode).FirstOrDefault();

        if (!first)
        {
            // Already running: ask the existing instance to open the overlay.
            var mode = requested ?? (CaptureMode)(-1);
            try { EventWaitHandle.OpenExisting(EventName(mode)).Set(); } catch { }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show(ex.Exception.Message, "screenX", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        tray = new Tray(this);
        hotkeys = new HotkeyManager();
        // Ctrl+Shift+S opens screenX; Windows keeps its own Win+Shift+S Snipping Tool.
        bool hotkeyOk = hotkeys.Register(Native.MOD_CONTROL | Native.MOD_SHIFT, 'S', () => StartCapture(Settings.Current.LastSnipMode));
        ListenForOtherInstances();

        if (!hotkeyOk)
            tray.Balloon("Ctrl+Shift+S is already used by another app",
                "Close that app and restart screenX, or open screenX from the tray icon.");
        if (requested is { } m) StartCapture(m);
        else if (hotkeyOk && !args.Contains("--background"))
            tray.Balloon("screenX is running", "Press Ctrl+Shift+S to snip, record the screen, pick a colour or extract text.");
    }

    static string EventName(CaptureMode mode) => $@"Local\screenX.Activate.{(int)mode}";

    void ListenForOtherInstances()
    {
        var modes = new[] { (CaptureMode)(-1) }.Concat(Commands.Select(c => c.Mode)).ToArray();
        var handles = modes.Select(m => (WaitHandle)new EventWaitHandle(false, EventResetMode.AutoReset, EventName(m))).ToArray();
        var t = new Thread(() =>
        {
            while (true)
            {
                int i = WaitHandle.WaitAny(handles);
                var mode = modes[i];
                Dispatcher.BeginInvoke(() => StartCapture((int)mode < 0 ? Settings.Current.LastSnipMode : mode));
            }
        }) { IsBackground = true, Name = "screenX ipc" };
        t.Start();
    }

    public async void StartCapture(CaptureMode mode)
    {
        if (recorder != null)
        {
            // While recording, the record shortcut stops the recording.
            if (mode == CaptureMode.Record) recorder.StopFromOutside();
            return;
        }
        if (overlay != null) { overlay.Activate(); return; }
        if (capturing) return;
        capturing = true;
        try
        {
            if (Toast.IsShowing)
            {
                Toast.CloseCurrent();
                await Task.Delay(180); // let DWM remove the toast before we grab the screen
            }
            var vs = ScreenCapture.VirtualScreen;
            var windows = ScreenCapture.GetWindowRects();
            var shot = ScreenCapture.Capture(vs);
            overlay = new OverlayWindow(shot, vs, windows, mode);
            overlay.Completed += OnOverlayCompleted;
            overlay.Closed += (_, _) => overlay = null;
            overlay.ShowPinned();
        }
        finally { capturing = false; }
    }

    async void OnOverlayCompleted(OverlayResult r)
    {
        try
        {
            switch (r.Mode)
            {
                case CaptureMode.Rectangle or CaptureMode.Freeform or CaptureMode.Window or CaptureMode.Fullscreen:
                    if (r.Mode != CaptureMode.Fullscreen && Settings.Current.LastSnipMode != r.Mode)
                    {
                        Settings.Current.LastSnipMode = r.Mode;
                        Settings.Current.Save();
                    }
                    await HandleSnipAction(r.Image!, r.Action);
                    break;
                case CaptureMode.Text:
                    await HandleText(r.Image!);
                    break;
                case CaptureMode.ColorPicker:
                    HandleColor(r.Color);
                    break;
                case CaptureMode.Record:
                    recorder = new RecordingController(r.Region);
                    recorder.Ended += () => recorder = null;
                    recorder.Show();
                    break;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "screenX", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    static async Task HandleSnipAction(BitmapSource img, SnipAction action)
    {
        switch (action)
        {
            case SnipAction.Capture:
                HandleSnip(img);
                break;
            case SnipAction.CopyOnly:
                ClipboardUtil.SetImage(img);
                Toast.Notify("Copied to clipboard", "Click to open in the editor.", Thumb(img), () => new EditorWindow(img, null).Show());
                break;
            case SnipAction.SaveTo:
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "PNG (*.png)|*.png|JPEG (*.jpg)|*.jpg|BMP (*.bmp)|*.bmp",
                    FileName = $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss}.png",
                    InitialDirectory = Settings.Current.ScreenshotFolder
                };
                Directory.CreateDirectory(Settings.Current.ScreenshotFolder);
                if (dlg.ShowDialog() == true)
                {
                    ImageUtil.Save(img, dlg.FileName);
                    string saved = dlg.FileName;
                    Toast.Notify("Snip saved", saved, Thumb(img), () => WinUtil.RevealFile(saved),
                        ("Open", (Action)(() => WinUtil.OpenPath(saved))), ("Open folder", (Action)(() => WinUtil.RevealFile(saved))));
                }
                break;
            case SnipAction.Share:
                await ShareHelper.ShareAsync(img);
                break;
            case SnipAction.Text:
                await HandleText(img);
                break;
            case SnipAction.Search:
                await GoogleLens.SearchAsync(img);
                break;
        }
    }

    static Image Thumb(BitmapSource img)
    {
        var thumb = new Image { Source = img, MaxHeight = 170, MaxWidth = 330, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
        RenderOptions.SetBitmapScalingMode(thumb, BitmapScalingMode.HighQuality);
        return thumb;
    }

    static void HandleSnip(BitmapSource img)
    {
        ClipboardUtil.SetImage(img);
        string? path = null;
        if (Settings.Current.AutoSave)
        {
            try { path = ImageUtil.AutoSave(img); } catch { }
        }
        var thumb = new Image { Source = img, MaxHeight = 170, MaxWidth = 330, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
        RenderOptions.SetBitmapScalingMode(thumb, BitmapScalingMode.HighQuality);
        Toast.Notify("Snip copied to clipboard",
            (path != null ? "Auto-saved to the screenX folder. " : "") + "Click to mark up and share.",
            thumb, () => new EditorWindow(img, path).Show());
    }

    static async Task HandleText(BitmapSource img)
    {
        string text = await OcrService.RecognizeAsync(img);
        if (string.IsNullOrWhiteSpace(text))
        {
            Toast.Notify("No text detected", "Try selecting a larger or clearer area.");
            return;
        }
        ClipboardUtil.SetText(text);
        Toast.Notify("Text copied to clipboard", text.Length > 220 ? text[..220] + "…" : text,
            null, () => new TextResultWindow(text).Show(), ("View / edit", (Action)(() => new TextResultWindow(text).Show())));
    }

    static void HandleColor(Color c)
    {
        string hex = ColorUtil.Hex(c);
        ClipboardUtil.SetText(hex);

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new Border
        {
            Width = 56, Height = 56, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(c),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1)
        });
        var values = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (var v in new[] { hex, ColorUtil.Rgb(c), ColorUtil.Hsl(c) })
            values.Children.Add(new TextBlock { Text = v, Foreground = Brushes.White, FontFamily = new FontFamily("Consolas"), FontSize = 13 });
        panel.Children.Add(values);

        Toast.Notify($"Colour {hex} copied to clipboard", null, panel, null,
            ("Copy HEX", (Action)(() => ClipboardUtil.SetText(hex))),
            ("Copy RGB", (Action)(() => ClipboardUtil.SetText(ColorUtil.Rgb(c)))),
            ("Copy HSL", (Action)(() => ClipboardUtil.SetText(ColorUtil.Hsl(c)))));
    }

    public void Quit()
    {
        if (recorder != null)
        {
            recorder.Ended += Quit; // finish writing the video first
            recorder.StopFromOutside();
            return;
        }
        hotkeys?.Dispose();
        tray?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        tray?.Dispose();
        base.OnExit(e);
    }
}
