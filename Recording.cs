using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Ellipse = System.Windows.Shapes.Ellipse;
using System.Windows.Threading;

namespace ScreenX;

/// <summary>
/// Captures a screen region with GDI at a fixed frame rate and pipes raw frames into ffmpeg (H.264 MP4).
/// </summary>
public sealed class RecordingSession
{
    readonly Int32Rect region;
    readonly int fps;
    readonly bool cursor;
    readonly Process ff;
    readonly Thread thread;
    readonly Stopwatch active = new();
    readonly StringBuilder log = new();
    volatile bool running = true;
    volatile bool paused;
    int periodHeld;
    Exception? error;

    public string OutputPath { get; }
    public TimeSpan Elapsed => active.Elapsed;
    public bool Paused
    {
        get => paused;
        set { paused = value; if (value) active.Stop(); else active.Start(); }
    }

    public RecordingSession(string ffmpeg, Int32Rect region, string outputPath, int fps, bool cursor)
    {
        this.region = region; this.fps = Math.Clamp(fps, 5, 60); this.cursor = cursor;
        OutputPath = outputPath;
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true,
        };
        foreach (var a in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-y",
                     "-f", "rawvideo", "-pix_fmt", "bgr0", "-video_size", $"{region.Width}x{region.Height}",
                     "-framerate", this.fps.ToString(), "-i", "-",
                     "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
                     "-movflags", "+faststart", outputPath
                 })
            psi.ArgumentList.Add(a);

        ff = Process.Start(psi) ?? throw new InvalidOperationException("Gagal menjalankan ffmpeg.");
        ff.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (log) log.AppendLine(e.Data); };
        ff.OutputDataReceived += (_, _) => { };
        ff.BeginErrorReadLine();
        ff.BeginOutputReadLine();

        thread = new Thread(Loop) { IsBackground = true, Name = "screenX recorder", Priority = ThreadPriority.AboveNormal };
    }

    void ReleasePeriod()
    {
        if (Interlocked.Exchange(ref periodHeld, 0) == 1) Native.timeEndPeriod(1);
    }

    public void Start()
    {
        Native.timeBeginPeriod(1);
        periodHeld = 1;
        active.Start();
        thread.Start();
    }

    void Loop()
    {
        try
        {
            using var cap = new GdiCapturer(region.Width, region.Height);
            int len = cap.Stride * cap.Height;
            var buf = new byte[len];
            var stdin = ff.StandardInput.BaseStream;
            long written = 0;
            while (running)
            {
                if (paused) { Thread.Sleep(15); continue; }
                cap.Capture(region.X, region.Y, cursor);
                Marshal.Copy(cap.Bits, buf, 0, len);

                // Keep the output timeline in sync with wall-clock time: duplicate frames if we fell behind.
                long target = (long)(active.Elapsed.TotalSeconds * fps) + 1;
                if (written == 0) target = 1;
                while (written < target) { stdin.Write(buf, 0, len); written++; }

                double next = written / (double)fps;
                int wait = (int)((next - active.Elapsed.TotalSeconds) * 1000);
                if (wait > 0) Thread.Sleep(wait);
            }
            stdin.Flush();
        }
        catch (Exception ex) { error = ex; }
    }

    /// <summary>Stops capturing and waits for ffmpeg to finalize the file.</summary>
    public async Task StopAsync()
    {
        running = false;
        paused = false;
        await Task.Run(() => thread.Join());
        ReleasePeriod();
        try { ff.StandardInput.Close(); } catch { }
        await ff.WaitForExitAsync();
        if (ff.ExitCode != 0 || !File.Exists(OutputPath))
        {
            string msg; lock (log) msg = log.ToString();
            throw new InvalidOperationException("ffmpeg gagal menyimpan video.\n" + (error?.Message ?? "") + "\n" + msg);
        }
    }

    public void Kill()
    {
        running = false;
        try { if (!ff.HasExited) ff.Kill(); } catch { }
        ReleasePeriod();
    }
}

/// <summary>Click-through red frame drawn just outside the recorded region.</summary>
public sealed class RegionFrame : Window
{
    readonly Int32Rect region;
    readonly Border border;
    const int Pad = 3;

    public RegionFrame(Int32Rect region)
    {
        this.region = region;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; Topmost = true; ShowActivated = false; ResizeMode = ResizeMode.NoResize;
        border = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x4F)), BorderThickness = new Thickness(2) };
        Content = border;
        SourceInitialized += (_, _) =>
        {
            WinUtil.IgnoreDpiChanges(this);
            WinUtil.AddExStyle(this, Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            WinUtil.ExcludeFromCapture(this);
        };
    }

    public void ShowPinned()
    {
        Show();
        Native.SetWindowPos(WinUtil.Hwnd(this), Native.HWND_TOPMOST, region.X - Pad, region.Y - Pad,
            region.Width + Pad * 2, region.Height + Pad * 2, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        double s = WinUtil.Scale(this);
        border.BorderThickness = new Thickness((Pad - 1) / s);
    }

    public void SetRecording(bool rec) =>
        border.BorderBrush = new SolidColorBrush(rec ? Color.FromRgb(0xFF, 0x4D, 0x4F) : Color.FromRgb(0xFF, 0xB0, 0x20));
}

/// <summary>Floating control bar: Start → countdown → timer / pause / stop / discard.</summary>
public sealed class RecordingBar : Window
{
    readonly Int32Rect region;
    readonly StackPanel row = new() { Orientation = Orientation.Horizontal };
    readonly TextBlock timeText = new() { Foreground = Brushes.White, FontSize = 14, FontFamily = new FontFamily("Segoe UI Semibold"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 10, 0), MinWidth = 52 };
    readonly Ellipse dot = new() { Width = 10, Height = 10, Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x4F)), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    Button? pauseBtn;

    public event Action? StartClicked, StopClicked, DiscardClicked, CancelClicked, PauseClicked;

    public RecordingBar(Int32Rect region)
    {
        this.region = region;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; Topmost = true; ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = new FontFamily("Segoe UI");
        Title = "screenX — Rekam";

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF5, 0x2B, 0x2B, 0x2B)), CornerRadius = new CornerRadius(8),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            Padding = new Thickness(4), Margin = new Thickness(12), Child = row,
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.45 }
        };
        card.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };
        Content = card;
        ShowIdle();

        SourceInitialized += (_, _) => WinUtil.ExcludeFromCapture(this);
        Loaded += (_, _) => Place();
    }

    static Button IconButton(string glyph, string tip, Brush? fg = null) => new()
    {
        Content = glyph, ToolTip = tip, Width = 40, Height = 36, FontSize = 15,
        FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Foreground = fg ?? Brushes.White,
        Cursor = Cursors.Hand, Template = Toast.FlatButtonTemplate(), Margin = new Thickness(1)
    };

    void ShowIdle()
    {
        row.Children.Clear();
        var start = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x4F)), VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = "Mulai", Foreground = Brushes.White, FontSize = 13.5, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }
                }
            },
            Padding = new Thickness(14, 8, 14, 8), Cursor = Cursors.Hand, Template = Toast.PillButtonTemplate(), Margin = new Thickness(2)
        };
        start.Click += (_, _) => StartClicked?.Invoke();
        var cancel = IconButton("", "Batal");
        cancel.Click += (_, _) => CancelClicked?.Invoke();
        row.Children.Add(start);
        row.Children.Add(new TextBlock { Text = $"{region.Width} × {region.Height}", Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0)), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) });
        row.Children.Add(cancel);
    }

    public void ShowCountdown(int n)
    {
        row.Children.Clear();
        row.Children.Add(new TextBlock { Text = $"Mulai dalam {n}…", Foreground = Brushes.White, FontSize = 14, Margin = new Thickness(14, 8, 14, 8) });
    }

    public void ShowRecording()
    {
        row.Children.Clear();
        row.Children.Add(dot);
        row.Children.Add(timeText);
        pauseBtn = IconButton("", "Jeda");
        pauseBtn.Click += (_, _) => PauseClicked?.Invoke();
        var stop = IconButton("", "Berhenti & simpan", new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)));
        stop.Click += (_, _) => StopClicked?.Invoke();
        var discard = IconButton("", "Buang rekaman");
        discard.Click += (_, _) => DiscardClicked?.Invoke();
        row.Children.Add(pauseBtn);
        row.Children.Add(stop);
        row.Children.Add(discard);
        UpdateTime(TimeSpan.Zero, false);
    }

    public void ShowSaving()
    {
        row.Children.Clear();
        row.Children.Add(new TextBlock { Text = "Menyimpan video…", Foreground = Brushes.White, FontSize = 13.5, Margin = new Thickness(14, 8, 14, 8) });
    }

    public void UpdateTime(TimeSpan t, bool paused)
    {
        timeText.Text = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
        dot.Opacity = paused ? 0.35 : (DateTime.Now.Millisecond < 500 ? 1 : 0.4);
        if (pauseBtn != null)
        {
            pauseBtn.Content = paused ? "" : "";
            pauseBtn.ToolTip = paused ? "Lanjutkan" : "Jeda";
        }
    }

    /// <summary>Put the bar centered above the region (or below / inside when there is no room).</summary>
    void Place()
    {
        var h = WinUtil.Hwnd(this);
        Native.GetWindowRect(h, out var r);
        int bw = r.Right - r.Left, bh = r.Bottom - r.Top;
        var mon = System.Windows.Forms.Screen.FromRectangle(new System.Drawing.Rectangle(region.X, region.Y, region.Width, region.Height)).Bounds;
        int x = region.X + region.Width / 2 - bw / 2;
        int y = region.Y - bh;
        if (y < mon.Top) y = region.Y + region.Height;
        if (y + bh > mon.Bottom) y = region.Y + 8;
        x = Math.Clamp(x, mon.Left, Math.Max(mon.Left, mon.Right - bw));
        Native.SetWindowPos(h, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }
}

/// <summary>Glues the frame, the bar and the session together.</summary>
public sealed class RecordingController
{
    readonly Int32Rect region;
    readonly RegionFrame frame;
    readonly RecordingBar bar;
    readonly DispatcherTimer ticker = new() { Interval = TimeSpan.FromMilliseconds(250) };
    RecordingSession? session;
    bool ending;

    public event Action? Ended;

    public RecordingController(Int32Rect selection)
    {
        // H.264 / yuv420p needs even dimensions.
        region = new Int32Rect(selection.X, selection.Y, Math.Max(16, selection.Width & ~1), Math.Max(16, selection.Height & ~1));
        frame = new RegionFrame(region);
        bar = new RecordingBar(region);
        bar.StartClicked += () => _ = StartAsync();
        bar.CancelClicked += () => End();
        bar.PauseClicked += () =>
        {
            if (session == null) return;
            session.Paused = !session.Paused;
            frame.SetRecording(!session.Paused);
            bar.UpdateTime(session.Elapsed, session.Paused);
        };
        bar.StopClicked += () => _ = StopAsync(keep: true);
        bar.DiscardClicked += () => _ = StopAsync(keep: false);
        ticker.Tick += (_, _) => { if (session != null) bar.UpdateTime(session.Elapsed, session.Paused); };
    }

    public bool IsRecording => session != null;

    public void Show()
    {
        frame.ShowPinned();
        frame.SetRecording(false);
        bar.Show();
        bar.Activate();
    }

    async Task StartAsync()
    {
        string? ffmpeg = FfmpegLocator.Find();
        if (ffmpeg == null)
        {
            MessageBox.Show("Perekaman layar membutuhkan ffmpeg.\n\nInstal dengan perintah:\n    winget install Gyan.FFmpeg\n\natau letakkan ffmpeg.exe di folder yang sama dengan screenX.exe.",
                "screenX", MessageBoxButton.OK, MessageBoxImage.Information);
            End();
            return;
        }
        for (int i = 3; i >= 1; i--)
        {
            bar.ShowCountdown(i);
            await Task.Delay(1000);
            if (ending) return;
        }
        try
        {
            var path = ImageUtil.UniquePath(Settings.Current.VideoFolder, "Rekaman layar", ".mp4");
            session = new RecordingSession(ffmpeg, region, path, Settings.Current.RecordFps, Settings.Current.RecordCursor);
            session.Start();
            frame.SetRecording(true);
            bar.ShowRecording();
            ticker.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Gagal memulai rekaman:\n" + ex.Message, "screenX", MessageBoxButton.OK, MessageBoxImage.Error);
            End();
        }
    }

    /// <summary>Stop from outside (e.g. tray menu or hotkey while recording).</summary>
    public void StopFromOutside()
    {
        if (session != null) _ = StopAsync(true);
        else End();
    }

    async Task StopAsync(bool keep)
    {
        if (session == null || ending) return;
        ending = true;
        ticker.Stop();
        bar.ShowSaving();
        var s = session;
        try
        {
            await s.StopAsync();
            if (!keep)
            {
                try { File.Delete(s.OutputPath); } catch { }
                End();
                return;
            }
            End();
            ClipboardUtil.SetFile(s.OutputPath);
            Toast.Notify("Rekaman layar disimpan", Path.GetFileName(s.OutputPath) + "\nFile video sudah disalin ke clipboard.",
                null, () => WinUtil.OpenPath(s.OutputPath),
                ("Putar", (Action)(() => WinUtil.OpenPath(s.OutputPath))),
                ("Buka folder", (Action)(() => WinUtil.RevealFile(s.OutputPath))));
        }
        catch (Exception ex)
        {
            s.Kill();
            End();
            MessageBox.Show(ex.Message, "screenX", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void End()
    {
        ending = true;
        ticker.Stop();
        session?.Kill();
        frame.Close();
        bar.Close();
        Ended?.Invoke();
        Ended = null;
    }
}
