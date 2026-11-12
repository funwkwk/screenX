using System;
using System.Collections.Generic;
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
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Ellipse = System.Windows.Shapes.Ellipse;
using Line = System.Windows.Shapes.Line;
using Rectangle = System.Windows.Shapes.Rectangle;
using WPath = System.Windows.Shapes.Path;

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

        ff = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg.");
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
            throw new InvalidOperationException("ffmpeg could not save the video.\n" + (error?.Message ?? "") + "\n" + msg);
        }
    }

    public void Kill()
    {
        running = false;
        try { if (!ff.HasExited) ff.Kill(); } catch { }
        ReleasePeriod();
    }
}

/// <summary>
/// Records the microphone or the system output (WASAPI loopback) to a WAV file that stays in step with the
/// video clock: silence is inserted when the device delivers nothing (loopback is silent when nothing plays)
/// and audio is dropped while the recording is paused.
/// </summary>
public sealed class AudioTrack
{
    readonly IWaveIn cap;
    readonly WaveFileWriter writer;
    readonly WaveFormat fmt;
    readonly Func<TimeSpan> clock;
    readonly Func<bool> paused;
    readonly ManualResetEventSlim stopped = new();
    readonly object gate = new();
    long written;
    bool closed;

    public string Path { get; }

    public AudioTrack(bool loopback, string path, Func<TimeSpan> clock, Func<bool> paused)
    {
        Path = path;
        this.clock = clock;
        this.paused = paused;
        cap = loopback ? new WasapiLoopbackCapture() : new WasapiCapture(new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console));
        fmt = cap.WaveFormat;
        writer = new WaveFileWriter(path, fmt);
        cap.DataAvailable += OnData;
        cap.RecordingStopped += (_, _) => stopped.Set();
    }

    public void Start() => cap.StartRecording();

    long Expected()
    {
        long bytes = (long)(clock().TotalSeconds * fmt.AverageBytesPerSecond);
        return bytes - bytes % fmt.BlockAlign;
    }

    void Silence(long bytes)
    {
        bytes -= bytes % fmt.BlockAlign;
        var zero = new byte[Math.Min(bytes, 1 << 16)];
        while (bytes > 0)
        {
            int n = (int)Math.Min(bytes, zero.Length);
            writer.Write(zero, 0, n);
            written += n;
            bytes -= n;
        }
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        lock (gate)
        {
            if (closed || paused() || e.BytesRecorded == 0) return;
            long gap = Expected() - written - e.BytesRecorded;
            if (gap > fmt.AverageBytesPerSecond / 10) Silence(gap);
            writer.Write(e.Buffer, 0, e.BytesRecorded);
            written += e.BytesRecorded;
        }
    }

    public void Stop()
    {
        try { cap.StopRecording(); stopped.Wait(2000); } catch { }
        lock (gate)
        {
            if (closed) return;
            long gap = Expected() - written;
            if (gap > 0) Silence(gap);
            closed = true;
            writer.Dispose();
        }
        cap.Dispose();
    }
}

/// <summary>
/// Full-screen layer for the recording region. Before recording: the screen is dimmed outside the region,
/// which can be moved, resized from its corners / edges, or redrawn. While recording: only a dotted red
/// frame is left and the whole layer is click-through.
/// </summary>
public sealed class RecordRegionWindow : Window
{
    [Flags] enum Edge { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }
    enum Drag { None, New, Move, Resize }

    readonly Int32Rect vs;
    Int32Rect region;
    double scale = 1;
    readonly Canvas root = new() { Background = Brushes.Transparent, Cursor = Cursors.Cross };
    // dim = four rectangles around the region (top, bottom, left, right)
    readonly Rectangle[] dim = new Rectangle[4];
    readonly Rectangle mover = new() { Fill = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), Cursor = Cursors.SizeAll };
    readonly WPath outline = new() { IsHitTestVisible = false };
    readonly List<(FrameworkElement Host, Edge Edges)> handles = new();
    Drag drag;
    Edge dragEdges;
    Int32Rect dragStartRect;
    (int X, int Y) dragStartPt;
    bool recording;

    public Int32Rect Region => region;
    public event Action? RegionChanged, StartRequested, CancelRequested;

    public RecordRegionWindow(Int32Rect virtualScreen, Int32Rect region)
    {
        vs = virtualScreen;
        this.region = region;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; Topmost = true; ResizeMode = ResizeMode.NoResize; Title = "screenX — Recording area";
        UseLayoutRounding = true;

        var dimBrush = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0));
        dimBrush.Freeze();
        for (int i = 0; i < 4; i++) root.Children.Add(dim[i] = new Rectangle { Fill = dimBrush, IsHitTestVisible = false });
        root.Children.Add(mover);
        root.Children.Add(outline);
        BuildHandles();
        Content = root;

        root.MouseLeftButtonDown += (_, e) => Begin(e, Drag.New, Edge.None);
        mover.MouseLeftButtonDown += (_, e) => Begin(e, Drag.Move, Edge.None);
        root.MouseMove += (_, e) => Move(e);
        root.MouseLeftButtonUp += (_, e) => End(e);
        KeyDown += (_, e) =>
        {
            if (recording) return;
            if (e.Key == Key.Escape) CancelRequested?.Invoke();
            else if (e.Key == Key.Enter) StartRequested?.Invoke();
        };

        SourceInitialized += (_, _) =>
        {
            WinUtil.IgnoreDpiChanges(this);
            WinUtil.AddExStyle(this, Native.WS_EX_TOOLWINDOW);
            WinUtil.ExcludeFromCapture(this);
        };
    }

    public void ShowPinned()
    {
        Show();
        Native.SetWindowPos(WinUtil.Hwnd(this), Native.HWND_TOPMOST, vs.X, vs.Y, vs.Width, vs.Height, Native.SWP_SHOWWINDOW);
        scale = WinUtil.Scale(this);
        Redraw();
    }

    Rect ToDip(Int32Rect r) => new((r.X - vs.X) / scale, (r.Y - vs.Y) / scale, r.Width / scale, r.Height / scale);

    (int X, int Y) ToPhys(Point dip) =>
        (Math.Clamp((int)Math.Round(dip.X * scale) + vs.X, vs.X, vs.X + vs.Width), Math.Clamp((int)Math.Round(dip.Y * scale) + vs.Y, vs.Y, vs.Y + vs.Height));

    void BuildHandles()
    {
        var shadow = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 0.6 };
        shadow.Freeze();
        foreach (var edges in new[]
                 {
                     Edge.Left | Edge.Top, Edge.Top, Edge.Right | Edge.Top, Edge.Right,
                     Edge.Right | Edge.Bottom, Edge.Bottom, Edge.Left | Edge.Bottom, Edge.Left,
                 })
        {
            bool corner = edges is (Edge.Left | Edge.Top) or (Edge.Right | Edge.Top) or (Edge.Right | Edge.Bottom) or (Edge.Left | Edge.Bottom);
            FrameworkElement visual = corner
                ? new WPath
                {
                    Data = Geometry.Parse("M8,26 V8 H26"), Stroke = Brushes.White, StrokeThickness = 5, Width = 30, Height = 30,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                    RenderTransformOrigin = new Point(0.5, 0.5), Effect = shadow,
                    RenderTransform = new ScaleTransform(edges.HasFlag(Edge.Right) ? -1 : 1, edges.HasFlag(Edge.Bottom) ? -1 : 1)
                }
                : new Border
                {
                    Width = edges is Edge.Top or Edge.Bottom ? 34 : 5, Height = edges is Edge.Top or Edge.Bottom ? 5 : 34,
                    CornerRadius = new CornerRadius(2.5), Background = Brushes.White, Effect = shadow
                };
            var host = new Grid
            {
                Width = 30, Height = 30, Background = Brushes.Transparent,
                Cursor = edges switch
                {
                    Edge.Left | Edge.Top or Edge.Right | Edge.Bottom => Cursors.SizeNWSE,
                    Edge.Right | Edge.Top or Edge.Left | Edge.Bottom => Cursors.SizeNESW,
                    Edge.Left or Edge.Right => Cursors.SizeWE,
                    _ => Cursors.SizeNS,
                }
            };
            host.Children.Add(visual);
            var e2 = edges;
            host.MouseLeftButtonDown += (_, e) => Begin(e, Drag.Resize, e2);
            root.Children.Add(host);
            handles.Add((host, edges));
        }
    }

    void Begin(MouseButtonEventArgs e, Drag kind, Edge edges)
    {
        if (recording || drag != Drag.None) return;
        drag = kind;
        dragEdges = edges;
        dragStartRect = region;
        dragStartPt = ToPhys(e.GetPosition(root));
        root.CaptureMouse();
        e.Handled = true;
    }

    void Move(MouseEventArgs e)
    {
        if (drag == Drag.None) return;
        var (x, y) = ToPhys(e.GetPosition(root));
        int L = vs.X, T = vs.Y, R = vs.X + vs.Width, B = vs.Y + vs.Height;
        switch (drag)
        {
            case Drag.New:
                region = new Int32Rect(Math.Min(x, dragStartPt.X), Math.Min(y, dragStartPt.Y), Math.Abs(x - dragStartPt.X), Math.Abs(y - dragStartPt.Y));
                break;
            case Drag.Move:
                int nx = Math.Clamp(dragStartRect.X + x - dragStartPt.X, L, R - dragStartRect.Width);
                int ny = Math.Clamp(dragStartRect.Y + y - dragStartPt.Y, T, B - dragStartRect.Height);
                region = new Int32Rect(nx, ny, dragStartRect.Width, dragStartRect.Height);
                break;
            case Drag.Resize:
                int l = dragStartRect.X, t = dragStartRect.Y, r = l + dragStartRect.Width, b = t + dragStartRect.Height;
                if (dragEdges.HasFlag(Edge.Left)) l = x;
                if (dragEdges.HasFlag(Edge.Right)) r = x;
                if (dragEdges.HasFlag(Edge.Top)) t = y;
                if (dragEdges.HasFlag(Edge.Bottom)) b = y;
                int x1 = Math.Min(l, r), x2 = Math.Max(l, r), y1 = Math.Min(t, b), y2 = Math.Max(t, b);
                if (x2 - x1 < 32 || y2 - y1 < 32) return;
                region = new Int32Rect(x1, y1, x2 - x1, y2 - y1);
                break;
        }
        Redraw();
        RegionChanged?.Invoke();
    }

    void End(MouseButtonEventArgs e)
    {
        if (drag == Drag.None) return;
        if (drag == Drag.New && (region.Width < 32 || region.Height < 32)) region = dragStartRect; // a click, not a new area
        drag = Drag.None;
        root.ReleaseMouseCapture();
        Redraw();
        RegionChanged?.Invoke();
        e.Handled = true;
    }

    void Redraw()
    {
        var r = ToDip(region);
        var full = new RectangleGeometry(new Rect(0, 0, vs.Width / scale, vs.Height / scale));
        double W = full.Rect.Width, H = full.Rect.Height;
        void Put(Rectangle d, double x, double y, double w, double h)
        {
            Canvas.SetLeft(d, x); Canvas.SetTop(d, y);
            d.Width = Math.Max(0, w); d.Height = Math.Max(0, h);
        }
        Put(dim[0], 0, 0, W, r.Top);
        Put(dim[1], 0, r.Bottom, W, H - r.Bottom);
        Put(dim[2], 0, r.Top, r.Left, r.Height);
        Put(dim[3], r.Right, r.Top, W - r.Right, r.Height);
        // outline sits just outside the recorded pixels
        double o = 1 / scale;
        outline.Data = new RectangleGeometry(new Rect(r.X - o, r.Y - o, r.Width + 2 * o, r.Height + 2 * o));
        Canvas.SetLeft(mover, r.X); Canvas.SetTop(mover, r.Y);
        mover.Width = r.Width; mover.Height = r.Height;
        foreach (var (host, edges) in handles)
        {
            double x = edges.HasFlag(Edge.Left) ? r.Left : edges.HasFlag(Edge.Right) ? r.Right : r.Left + r.Width / 2;
            double y = edges.HasFlag(Edge.Top) ? r.Top : edges.HasFlag(Edge.Bottom) ? r.Bottom : r.Top + r.Height / 2;
            bool corner = edges is (Edge.Left | Edge.Top) or (Edge.Right | Edge.Top) or (Edge.Right | Edge.Bottom) or (Edge.Left | Edge.Bottom);
            double ox = corner ? (edges.HasFlag(Edge.Left) ? 10 : 20) : edges == Edge.Left ? 13 : edges == Edge.Right ? 17 : 15;
            double oy = corner ? (edges.HasFlag(Edge.Top) ? 10 : 20) : edges == Edge.Top ? 13 : edges == Edge.Bottom ? 17 : 15;
            Canvas.SetLeft(host, x - ox);
            Canvas.SetTop(host, y - oy);
        }
        ApplyLook(false);
    }

    void ApplyLook(bool paused)
    {
        if (!recording)
        {
            outline.Stroke = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
            outline.StrokeThickness = 1.5;
            outline.StrokeDashArray = new DoubleCollection { 3, 2 };
        }
        else
        {
            outline.Stroke = new SolidColorBrush(paused ? Color.FromRgb(0xFF, 0xB0, 0x20) : Color.FromRgb(0xFF, 0x4D, 0x4F));
            outline.StrokeThickness = 1.5;
            outline.StrokeDashArray = new DoubleCollection { 1.5, 1.5 };
        }
    }

    /// <summary>Switch to the recording look: no dim, no handles, dotted red frame, clicks go through.</summary>
    public void SetRecording(bool paused = false)
    {
        if (!recording)
        {
            recording = true;
            foreach (var d in dim) d.Visibility = Visibility.Collapsed;
            mover.Visibility = Visibility.Collapsed;
            foreach (var (h, _) in handles) h.Visibility = Visibility.Collapsed;
            root.Background = null;
            root.Cursor = null;
            WinUtil.AddExStyle(this, Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED | Native.WS_EX_NOACTIVATE);
        }
        ApplyLook(paused);
    }
}

/// <summary>
/// Floating control bar above the region.
/// Ready: [● Start] 00:00:00 [mic] [system audio] [✕].   Recording: [❚❚] [■] 00:00:09 [mic] [system audio] [🗑].
/// </summary>
public sealed class RecordingBar : Window
{
    static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF));
    static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));

    readonly StackPanel row = new() { Orientation = Orientation.Horizontal };
    readonly TextBlock timeText = new()
    {
        Text = "00:00:00", Foreground = Brushes.White, FontSize = 15, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(12, 0, 10, 0), FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI")
    };
    Button? startBtn, pauseBtn;
    readonly Button micBtn, audioBtn;

    public event Action? StartClicked, StopClicked, DiscardClicked, CancelClicked, PauseClicked;

    public RecordingBar()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; Topmost = true; ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Title = "screenX — Record";

        micBtn = IconButton("", "Microphone");
        micBtn.Click += (_, _) => { Settings.Current.RecordMic = !Settings.Current.RecordMic; Settings.Current.Save(); RefreshAudio(); };
        audioBtn = IconButton("", "System audio");
        audioBtn.Click += (_, _) => { Settings.Current.RecordSystemAudio = !Settings.Current.RecordSystemAudio; Settings.Current.Save(); RefreshAudio(); };

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF7, 0x2B, 0x2B, 0x2B)), CornerRadius = new CornerRadius(8),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            Padding = new Thickness(6), Margin = new Thickness(14), Child = row,
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.5 }
        };
        card.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };
        Content = card;
        ShowIdle();

        KeyDown += (_, e) =>
        {
            if (startBtn == null) return;
            if (e.Key == Key.Escape) CancelClicked?.Invoke();
            else if (e.Key == Key.Enter && startBtn.IsEnabled) StartClicked?.Invoke();
        };
        SourceInitialized += (_, _) => WinUtil.ExcludeFromCapture(this);
    }

    static Button IconButton(string glyph, string tip, Brush? fg = null)
    {
        var b = new Button
        {
            Content = glyph, ToolTip = tip, Width = 40, Height = 38, FontSize = 16, FontFamily = Icons,
            Foreground = fg ?? Brushes.White, Cursor = Cursors.Arrow, Template = Toast.FlatButtonTemplate(), Margin = new Thickness(1, 0, 1, 0)
        };
        System.Windows.Automation.AutomationProperties.SetName(b, tip);
        return b;
    }

    /// <summary>Glyph with a diagonal slash when the source is off (muted look).</summary>
    static UIElement AudioIcon(string glyph, bool on, bool slash)
    {
        var g = new Grid { Width = 22, Height = 22 };
        g.Children.Add(new TextBlock
        {
            Text = glyph, FontFamily = Icons, FontSize = 16, Foreground = on ? Brushes.White : Muted,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        if (!on && slash)
            g.Children.Add(new Line { X1 = 3, Y1 = 3, X2 = 19, Y2 = 19, Stroke = Muted, StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        return g;
    }

    void RefreshAudio()
    {
        bool mic = Settings.Current.RecordMic, sys = Settings.Current.RecordSystemAudio;
        micBtn.Content = AudioIcon(mic ? "" : "", mic, slash: false);
        micBtn.ToolTip = mic ? "Microphone on" : "Microphone off";
        audioBtn.Content = AudioIcon("", sys, slash: true);
        audioBtn.ToolTip = sys ? "System audio on" : "System audio off";
        System.Windows.Automation.AutomationProperties.SetName(micBtn, (string)micBtn.ToolTip);
        System.Windows.Automation.AutomationProperties.SetName(audioBtn, (string)audioBtn.ToolTip);
    }

    static ControlTemplate StartTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border), "Bd");
        bd.SetValue(Border.BackgroundProperty, Accent);
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        bd.SetValue(Border.PaddingProperty, new Thickness(16, 0, 20, 0));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        t.VisualTree = bd;
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x6C, 0xCE, 0xFF)), "Bd"));
        t.Triggers.Add(hover);
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x3A, 0x8C, 0xB8)), "Bd"));
        t.Triggers.Add(disabled);
        return t;
    }

    static UIElement StartContent(string label)
    {
        var ring = new Grid { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
        var dark = new SolidColorBrush(Color.FromRgb(0x0B, 0x25, 0x32));
        ring.Children.Add(new Ellipse { Stroke = dark, StrokeThickness = 1.6 });
        ring.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = dark });
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { ring, new TextBlock { Text = label, Foreground = dark, FontSize = 15, Margin = new Thickness(10, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center } }
        };
    }

    static Rectangle Divider() => new()
    {
        Width = 1, Height = 24, Margin = new Thickness(6, 0, 6, 0), Fill = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)),
        VerticalAlignment = VerticalAlignment.Center
    };

    void ShowIdle()
    {
        row.Children.Clear();
        startBtn = new Button { Content = StartContent("Start"), Height = 38, MinWidth = 120, Cursor = Cursors.Arrow, Template = StartTemplate(), Margin = new Thickness(0, 0, 4, 0), ToolTip = "Start recording (Enter)" };
        System.Windows.Automation.AutomationProperties.SetName(startBtn, "Start");
        startBtn.Click += (_, _) => StartClicked?.Invoke();
        var cancel = IconButton("", "Close (Esc)");
        cancel.Click += (_, _) => CancelClicked?.Invoke();
        timeText.Text = "00:00:00";
        row.Children.Add(startBtn);
        row.Children.Add(timeText);
        row.Children.Add(micBtn);
        row.Children.Add(audioBtn);
        row.Children.Add(Divider());
        row.Children.Add(cancel);
        RefreshAudio();
    }

    public void ShowCountdown(int n)
    {
        if (startBtn == null) return;
        startBtn.IsEnabled = false;
        startBtn.Content = StartContent($"Starting in {n}");
        micBtn.IsEnabled = audioBtn.IsEnabled = false;
    }

    public void ShowRecording()
    {
        row.Children.Clear();
        startBtn = null;
        pauseBtn = IconButton("", "Pause");
        pauseBtn.Click += (_, _) => PauseClicked?.Invoke();
        var stop = new Button
        {
            Content = new Rectangle { Width = 15, Height = 15, RadiusX = 2.5, RadiusY = 2.5, Fill = new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23)) },
            ToolTip = "Stop and save", Width = 40, Height = 38, Cursor = Cursors.Arrow, Template = Toast.FlatButtonTemplate(), Margin = new Thickness(1, 0, 1, 0)
        };
        System.Windows.Automation.AutomationProperties.SetName(stop, "Stop and save");
        stop.Click += (_, _) => StopClicked?.Invoke();
        var discard = IconButton("", "Discard recording");
        discard.Click += (_, _) => DiscardClicked?.Invoke();
        micBtn.IsEnabled = audioBtn.IsEnabled = false; // the sources are fixed once recording started
        micBtn.Opacity = audioBtn.Opacity = 1;
        row.Children.Add(pauseBtn);
        row.Children.Add(stop);
        row.Children.Add(timeText);
        row.Children.Add(micBtn);
        row.Children.Add(audioBtn);
        row.Children.Add(Divider());
        row.Children.Add(discard);
        UpdateTime(TimeSpan.Zero, false);
    }

    public void ShowSaving()
    {
        row.Children.Clear();
        row.Children.Add(new TextBlock { Text = "Saving video…", Foreground = Brushes.White, FontSize = 14, Margin = new Thickness(16, 9, 16, 9) });
    }

    public void UpdateTime(TimeSpan t, bool paused)
    {
        timeText.Text = t.ToString(@"hh\:mm\:ss");
        timeText.Opacity = paused && DateTime.Now.Millisecond >= 500 ? 0.4 : 1;
        if (pauseBtn != null)
        {
            pauseBtn.Content = paused ? "" : "";
            pauseBtn.ToolTip = paused ? "Resume" : "Pause";
            System.Windows.Automation.AutomationProperties.SetName(pauseBtn, (string)pauseBtn.ToolTip);
        }
    }

    /// <summary>Centre the bar above the region (or below / inside when there is no room).</summary>
    public void Place(Int32Rect region)
    {
        if (!IsLoaded) return;
        var h = WinUtil.Hwnd(this);
        Native.GetWindowRect(h, out var r);
        int bw = r.Right - r.Left, bh = r.Bottom - r.Top;
        var mon = System.Windows.Forms.Screen.FromRectangle(new System.Drawing.Rectangle(region.X, region.Y, Math.Max(1, region.Width), Math.Max(1, region.Height))).Bounds;
        int x = region.X + region.Width / 2 - bw / 2;
        int y = region.Y - bh + 6;
        if (y < mon.Top) y = region.Y + region.Height - 6;
        if (y + bh > mon.Bottom) y = region.Y + 6;
        x = Math.Clamp(x, mon.Left, Math.Max(mon.Left, mon.Right - bw));
        y = Math.Clamp(y, mon.Top, Math.Max(mon.Top, mon.Bottom - bh));
        Native.SetWindowPos(h, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }
}

/// <summary>Glues the region layer, the bar, the video session and the audio tracks together.</summary>
public sealed class RecordingController
{
    readonly RecordRegionWindow layer;
    readonly RecordingBar bar;
    readonly DispatcherTimer ticker = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly List<AudioTrack> tracks = new();
    RecordingSession? session;
    string? finalPath;
    bool ending;

    public event Action? Ended;

    public RecordingController(Int32Rect selection)
    {
        var vs = ScreenCapture.VirtualScreen;
        if (selection.Width < 32 || selection.Height < 32)
        {
            // no area picked: start with a centred area on the primary screen
            var p = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            selection = new Int32Rect(p.X + p.Width / 6, p.Y + p.Height / 6, p.Width * 2 / 3, p.Height * 2 / 3);
        }
        layer = new RecordRegionWindow(vs, selection);
        bar = new RecordingBar();
        layer.RegionChanged += () => bar.Place(layer.Region);
        layer.StartRequested += () => _ = StartAsync();
        layer.CancelRequested += End;
        bar.Loaded += (_, _) => bar.Place(layer.Region);
        bar.StartClicked += () => _ = StartAsync();
        bar.CancelClicked += End;
        bar.PauseClicked += () =>
        {
            if (session == null) return;
            session.Paused = !session.Paused;
            layer.SetRecording(session.Paused);
            bar.UpdateTime(session.Elapsed, session.Paused);
        };
        bar.StopClicked += () => _ = StopAsync(keep: true);
        bar.DiscardClicked += () => _ = StopAsync(keep: false);
        ticker.Tick += (_, _) => { if (session != null) bar.UpdateTime(session.Elapsed, session.Paused); };
    }

    public bool IsRecording => session != null;

    public void Show()
    {
        layer.ShowPinned();
        bar.Show();
        bar.Activate();
    }

    bool starting;

    async Task StartAsync()
    {
        if (starting || session != null || ending) return;
        starting = true;
        string? ffmpeg = FfmpegLocator.Find();
        if (ffmpeg == null)
        {
            MessageBox.Show("Screen recording needs ffmpeg.\n\nInstall it with:\n    winget install Gyan.FFmpeg\n\nor put ffmpeg.exe in the same folder as screenX.exe.",
                "screenX", MessageBoxButton.OK, MessageBoxImage.Information);
            End();
            return;
        }
        layer.SetRecording();
        for (int i = 3; i >= 1; i--)
        {
            bar.ShowCountdown(i);
            await Task.Delay(1000);
            if (ending) return;
        }

        // H.264 / yuv420p needs even dimensions.
        var sel = layer.Region;
        var region = new Int32Rect(sel.X, sel.Y, Math.Max(16, sel.Width & ~1), Math.Max(16, sel.Height & ~1));
        try
        {
            finalPath = ImageUtil.UniquePath(Settings.Current.VideoFolder, "Screen Recording", ".mp4");
            bool audio = Settings.Current.RecordMic || Settings.Current.RecordSystemAudio;
            string tmp = Path.Combine(Path.GetTempPath(), "screenX", "rec-" + Guid.NewGuid().ToString("N")[..8]);
            if (audio) Directory.CreateDirectory(tmp);
            string videoPath = audio ? Path.Combine(tmp, "video.mp4") : finalPath;

            session = new RecordingSession(ffmpeg, region, videoPath, Settings.Current.RecordFps, Settings.Current.RecordCursor);
            var s = session;
            var problems = new List<string>();
            foreach (var (on, loop, name) in new[] { (Settings.Current.RecordSystemAudio, true, "system"), (Settings.Current.RecordMic, false, "mic") })
            {
                if (!on) continue;
                try { tracks.Add(new AudioTrack(loop, Path.Combine(tmp, name + ".wav"), () => s.Elapsed, () => s.Paused)); }
                catch (Exception ex) { problems.Add((loop ? "System audio: " : "Microphone: ") + ex.Message); }
            }
            session.Start();
            foreach (var t in tracks) t.Start();
            bar.ShowRecording();
            ticker.Start();
            if (problems.Count > 0)
                Toast.Notify("Recording without some audio", string.Join("\n", problems));
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not start recording:\n" + ex.Message, "screenX", MessageBoxButton.OK, MessageBoxImage.Error);
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
        string output = finalPath!;
        try
        {
            await s.StopAsync();
            foreach (var t in tracks) await Task.Run(t.Stop);

            if (!keep)
            {
                Cleanup(s.OutputPath, output);
                try { File.Delete(output); } catch { }
                End();
                return;
            }

            string? warning = null;
            if (tracks.Count > 0)
            {
                if (!await MuxAsync(FfmpegLocator.Find()!, s.OutputPath, tracks, output))
                {
                    File.Move(s.OutputPath, output, true);
                    warning = "The audio could not be added; the video was saved without sound.";
                }
                Cleanup(s.OutputPath, output);
            }
            End();
            ClipboardUtil.SetFile(output);
            Toast.Notify("Screen recording saved", Path.GetFileName(output) + "\n" + (warning ?? "The video file is copied to your clipboard."),
                null, () => WinUtil.OpenPath(output),
                ("Play", (Action)(() => WinUtil.OpenPath(output))),
                ("Open folder", (Action)(() => WinUtil.RevealFile(output))));
        }
        catch (Exception ex)
        {
            s.Kill();
            End();
            MessageBox.Show(ex.Message, "screenX", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void Cleanup(string videoPath, string output)
    {
        if (tracks.Count == 0) return;
        foreach (var t in tracks) try { File.Delete(t.Path); } catch { }
        if (videoPath != output) try { File.Delete(videoPath); } catch { }
        try { Directory.Delete(Path.GetDirectoryName(videoPath)!, false); } catch { }
    }

    /// <summary>Add the WAV tracks to the video (mixed when there are two) without re-encoding the video.</summary>
    static async Task<bool> MuxAsync(string ffmpeg, string video, List<AudioTrack> audio, string output)
    {
        foreach (bool normalizeOpt in new[] { true, false }) // amix's normalize option needs ffmpeg 4.4+
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-i", video };
            foreach (var t in audio) { args.Add("-i"); args.Add(t.Path); }
            if (audio.Count == 1)
                args.AddRange(new[] { "-map", "0:v", "-map", "1:a" });
            else
            {
                string mix = $"[1:a][2:a]amix=inputs=2:duration=longest:dropout_transition=0{(normalizeOpt ? ":normalize=0" : "")}[a]";
                args.AddRange(new[] { "-filter_complex", mix, "-map", "0:v", "-map", "[a]" });
            }
            args.AddRange(new[] { "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-shortest", "-movflags", "+faststart", output });

            var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return false;
            _ = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode == 0 && File.Exists(output)) return true;
            if (audio.Count == 1) break;
        }
        return false;
    }

    void End()
    {
        ending = true;
        ticker.Stop();
        session?.Kill();
        foreach (var t in tracks) try { t.Stop(); } catch { }
        layer.Close();
        bar.Close();
        Ended?.Invoke();
        Ended = null;
    }
}
