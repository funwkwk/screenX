using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ellipse = System.Windows.Shapes.Ellipse;
using WPath = System.Windows.Shapes.Path;

namespace ScreenX;

public enum SnipAction { Capture, SaveTo, CopyOnly, Share, Text, Search }

public sealed record OverlayResult(CaptureMode Mode, BitmapSource? Image, Int32Rect Region, Color Color,
    SnipAction Action = SnipAction.Capture);

/// <summary>
/// Full-virtual-screen overlay: frozen screenshot, top capture toolbar, and an in-place markup bar
/// that appears under the selection. Selection math is in physical pixels relative to the virtual screen.
/// </summary>
public partial class OverlayWindow : Window
{
    enum InkTool { Pen, Highlighter, Eraser, Shapes, Emoji }
    enum ShapeKind { Rectangle, Ellipse, Arrow, Line }

    const int MagCells = 13;

    static readonly string[] Palette =
    {
        "#000000", "#FFFFFF", "#E1E1E1", "#C8C8C8", "#A0A0A0", "#7A7A7A",
        "#E3008C", "#E81123", "#F7630C", "#FF8C00", "#FFB900", "#FCE100",
        "#8CBD18", "#16C60C", "#0B6A0B", "#00B7C3", "#0078D4", "#4F4BD9",
        "#744DA9", "#B146C2", "#F7D7C4", "#D8A66C", "#CA5010", "#8E562E",
        "#FF8DE1", "#E9C46A", "#BAB22A", "#00CC6A", "#2EE6E6", "#C8C2F5",
    };

    readonly BitmapSource shot;
    readonly byte[] px;
    readonly int pw, ph;
    readonly Int32Rect vs;
    readonly List<Int32Rect> windows;

    CaptureMode mode;
    CaptureMode lastShape;
    double scale = 1;
    bool dragging, finished, markup;
    int sx, sy;
    Int32Rect sel;
    readonly List<Point> pts = new();
    Int32Rect? hoverWin;

    // markup state
    InkTool tool = InkTool.Pen;
    ShapeKind shapeKind = ShapeKind.Rectangle;
    InkTool flyoutFor;
    WPath? shapePath;
    string currentEmoji = "\U0001F44D";
    bool paletteForFill;
    Point shapeStart;
    bool applyingHistory;
    readonly Stack<Edit> undo = new(), redo = new();

    public event Action<OverlayResult>? Completed;

    public OverlayWindow(BitmapSource shot, Int32Rect virtualScreen, List<Int32Rect> windows, CaptureMode mode)
    {
        InitializeComponent();
        this.shot = shot;
        vs = virtualScreen;
        this.windows = windows;
        this.mode = mode;
        lastShape = IsSnip(mode) && mode != CaptureMode.Fullscreen ? mode : Settings.Current.LastSnipMode;
        pw = shot.PixelWidth; ph = shot.PixelHeight;
        px = new byte[pw * ph * 4];
        shot.CopyPixels(px, pw * 4, 0);
        Shot.Source = shot;
        HintBox.Visibility = Visibility.Collapsed;
        BtnMarkup.IsChecked = Settings.Current.MarkupAfterSnip;

        Left = vs.X; Top = vs.Y; Width = vs.Width; Height = vs.Height;

        BuildShapeMenu();
        BuildShapesFlyout();
        Ink.Strokes.StrokesChanged += Strokes_Changed;
        Ink.PreviewMouseLeftButtonDown += Ink_ShapeDown;
        Ink.PreviewMouseMove += Ink_ShapeMove;
        Ink.PreviewMouseLeftButtonUp += Ink_ShapeUp;

        SourceInitialized += (_, _) => WinUtil.IgnoreDpiChanges(this);
        Loaded += (_, _) => { scale = WinUtil.Scale(this); SetMode(this.mode, false); LayoutToolbar(); };
        SizeChanged += (_, _) => { scale = WinUtil.Scale(this); LayoutToolbar(); Redraw(); };
        TopPanel.SizeChanged += (_, _) => LayoutToolbar();
        PreviewKeyDown += OnKey;
    }

    static bool IsSnip(CaptureMode m) => m is CaptureMode.Rectangle or CaptureMode.Freeform or CaptureMode.Window or CaptureMode.Fullscreen;

    public void ShowPinned()
    {
        Show();
        var h = WinUtil.Hwnd(this);
        Native.SetWindowPos(h, Native.HWND_TOPMOST, vs.X, vs.Y, vs.Width, vs.Height, Native.SWP_SHOWWINDOW);
        Activate();
        Native.SetForegroundWindow(h);
        Focus();
        Keyboard.Focus(this);
    }

    // =================================================================== top toolbar

    void LayoutToolbar()
    {
        if (scale <= 0) return;
        Native.GetCursorPos(out var c);
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(c.X, c.Y)).Bounds;
        double monLeft = (screen.X - vs.X) / scale, monTop = (screen.Y - vs.Y) / scale, monW = screen.Width / scale;
        double w = Toolbar.ActualWidth;
        TopPanel.Margin = new Thickness(monLeft + (monW - w) / 2, monTop + 14, 0, 0);
        TopPanel.Width = w;
    }

    UIElement Glyph(string g, double size = 17) => new TextBlock
    {
        Text = g, FontFamily = (FontFamily)FindResource("Icons"), FontSize = size,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };

    UIElement PathIcon(object geometry) => new WPath
    {
        Data = geometry as Geometry ?? Geometry.Parse((string)geometry), Stroke = Brushes.White, StrokeThickness = 1.4,
        Width = 20, Height = 20, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
    };

    UIElement ShapeModeIcon(CaptureMode m) => m switch
    {
        CaptureMode.Rectangle => PathIcon(FindResource("IcoRectSnip")),
        CaptureMode.Freeform => Glyph(""),
        CaptureMode.Window => Glyph(""),
        _ => Glyph(""),
    };

    Button MenuRow(UIElement icon, string label, string? shortcut, Action onClick)
    {
        var dock = new DockPanel();
        if (shortcut != null)
        {
            var sc = new TextBlock { Text = shortcut, Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(sc, Dock.Right);
            dock.Children.Add(sc);
        }
        dock.Children.Add(new Border { Width = 22, Child = icon });
        dock.Children.Add(new TextBlock { Text = label, FontSize = 14, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Style = (Style)FindResource("MenuRow"), Content = dock };
        System.Windows.Automation.AutomationProperties.SetName(b, label);
        b.Click += (_, _) => onClick();
        return b;
    }

    void BuildShapeMenu()
    {
        foreach (var (m, label) in new[] { (CaptureMode.Rectangle, "Rectangle"), (CaptureMode.Window, "Window"), (CaptureMode.Fullscreen, "Full screen"), (CaptureMode.Freeform, "Freeform") })
            ShapeMenuList.Children.Add(MenuRow(ShapeModeIcon(m), label, null, () =>
            {
                ShapeMenu.Visibility = Visibility.Collapsed;
                if (m != CaptureMode.Fullscreen) lastShape = m;
                SetMode(m, true);
            }));
    }

    void ShapeMenu_Click(object sender, RoutedEventArgs e)
    {
        bool open = ShapeMenu.Visibility != Visibility.Visible;
        ShapeMenu.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open)
        {
            var p = BtnShape.TranslatePoint(new Point(0, 0), TopPanel);
            ShapeMenu.Margin = new Thickness(p.X, 6, 0, 0);
        }
    }

    void Photo_Click(object sender, RoutedEventArgs e) => SetMode(lastShape, true);
    void Video_Click(object sender, RoutedEventArgs e) => SetMode(CaptureMode.Record, true);
    void Color_Click(object sender, RoutedEventArgs e) => SetMode(CaptureMode.ColorPicker, true);
    void Text_Click(object sender, RoutedEventArgs e) => SetMode(CaptureMode.Text, true);

    void Markup_Click(object sender, RoutedEventArgs e)
    {
        Settings.Current.MarkupAfterSnip = BtnMarkup.IsChecked == true;
        Settings.Current.Save();
    }

    void SetMode(CaptureMode m, bool fromUser)
    {
        mode = m;
        dragging = false; sel = default; pts.Clear(); hoverWin = null;
        if (Surface.IsMouseCaptured) Surface.ReleaseMouseCapture();
        ShapeMenu.Visibility = Visibility.Collapsed;

        BtnPhoto.IsChecked = IsSnip(m);
        BtnVideo.IsChecked = m == CaptureMode.Record;
        BtnColor.IsChecked = m == CaptureMode.ColorPicker;
        BtnText.IsChecked = m == CaptureMode.Text;
        bool photo = m != CaptureMode.Record;
        // Keep every button in place (stable toolbar width); video mode just disables the snip-only ones.
        BtnShape.IsEnabled = photo;
        BtnMarkup.IsEnabled = photo;
        ShapeIcon.Content = ShapeModeIcon(IsSnip(m) ? m : lastShape);

        SelOutline.Stroke = m == CaptureMode.Record ? new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x4F)) : Brushes.White;
        SelOutline.StrokeDashArray = m == CaptureMode.Record ? new DoubleCollection { 4, 3 } : null;
        Magnifier.Visibility = m == CaptureMode.ColorPicker ? Visibility.Visible : Visibility.Collapsed;
        SizeLabel.Visibility = Visibility.Collapsed;

        if (m == CaptureMode.Fullscreen && fromUser)
        {
            sel = new Int32Rect(0, 0, pw, ph);
            CompleteSelection();
            return;
        }

        var p = CursorPhys();
        if (m == CaptureMode.Window) UpdateHover(p);
        if (m == CaptureMode.ColorPicker) UpdateMagnifier(p, CursorDip());
        Redraw();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Cancel();

    void Cancel()
    {
        finished = true;
        Close();
    }

    // =================================================================== selection input

    (int X, int Y) ToPhys(Point dip) =>
        (Math.Clamp((int)Math.Round(dip.X * scale), 0, pw), Math.Clamp((int)Math.Round(dip.Y * scale), 0, ph));

    (int X, int Y) CursorPhys()
    {
        Native.GetCursorPos(out var c);
        return (Math.Clamp(c.X - vs.X, 0, pw - 1), Math.Clamp(c.Y - vs.Y, 0, ph - 1));
    }

    Point CursorDip()
    {
        var (x, y) = CursorPhys();
        return new Point(x / scale, y / scale);
    }

    void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ShapeMenu.Visibility = Visibility.Collapsed;
        if (markup) { CloseFlyouts(); return; }

        var p = ToPhys(e.GetPosition(Surface));
        switch (mode)
        {
            case CaptureMode.Rectangle:
            case CaptureMode.Text:
            case CaptureMode.Record:
                dragging = true; sx = p.X; sy = p.Y; sel = new Int32Rect(p.X, p.Y, 0, 0);
                Surface.CaptureMouse();
                break;
            case CaptureMode.Freeform:
                dragging = true; pts.Clear(); pts.Add(new Point(p.X, p.Y));
                Surface.CaptureMouse();
                break;
            case CaptureMode.Window:
                UpdateHover(p);
                if (hoverWin is { } w) { sel = w; CompleteSelection(); }
                break;
            case CaptureMode.ColorPicker:
                Finish(SnipAction.Capture);
                break;
            case CaptureMode.Fullscreen:
                sel = new Int32Rect(0, 0, pw, ph);
                CompleteSelection();
                break;
        }
        e.Handled = true;
    }

    void Surface_MouseMove(object sender, MouseEventArgs e)
    {
        if (markup) return;
        var dip = e.GetPosition(Surface);
        var p = ToPhys(dip);
        if (dragging)
        {
            if (mode == CaptureMode.Freeform)
            {
                var last = pts[^1];
                if (Math.Abs(last.X - p.X) + Math.Abs(last.Y - p.Y) >= 2) pts.Add(new Point(p.X, p.Y));
            }
            else
            {
                sel = new Int32Rect(Math.Min(sx, p.X), Math.Min(sy, p.Y), Math.Abs(p.X - sx), Math.Abs(p.Y - sy));
            }
            Redraw();
        }
        else if (mode == CaptureMode.Window)
        {
            UpdateHover(p);
            Redraw();
        }
        else if (mode == CaptureMode.ColorPicker)
        {
            UpdateMagnifier((Math.Min(p.X, pw - 1), Math.Min(p.Y, ph - 1)), dip);
        }
    }

    void Surface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!dragging) return;
        dragging = false;
        Surface.ReleaseMouseCapture();
        if (mode == CaptureMode.Freeform)
        {
            if (pts.Count > 2)
            {
                sel = Bounds(pts);
                if (sel.Width >= 3 && sel.Height >= 3) { CompleteSelection(); return; }
            }
            pts.Clear();
        }
        else if (sel.Width >= 3 && sel.Height >= 3)
        {
            CompleteSelection();
            return;
        }
        sel = default;
        Redraw();
    }

    void Surface_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (markup) return;
        if (dragging || sel.Width > 0 || pts.Count > 0)
        {
            dragging = false; sel = default; pts.Clear();
            if (Surface.IsMouseCaptured) Surface.ReleaseMouseCapture();
            Redraw();
        }
        else Cancel();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (e.Key == Key.Escape)
        {
            if (AnyFlyoutOpen()) CloseFlyouts();
            else Cancel();
            e.Handled = true;
            return;
        }
        if (markup)
        {
            if (ctrl && e.Key == Key.Enter) Finish(SnipAction.Capture);
            else if (ctrl && e.Key == Key.S) Finish(SnipAction.SaveTo);
            else if (ctrl && e.Key == Key.C) Finish(SnipAction.CopyOnly);
            else if (ctrl && e.Key == Key.Z) Undo_Click(this, e);
            else if (ctrl && e.Key == Key.Y) Redo_Click(this, e);
            else if (e.Key is Key.Delete or Key.Back && selectedEmoji != null) DeleteSelectedEmoji();
            else return;
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.Enter when mode == CaptureMode.ColorPicker:
                Finish(SnipAction.Capture);
                e.Handled = true;
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down when mode == CaptureMode.ColorPicker:
                Native.GetCursorPos(out var c);
                int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
                int dx = e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
                int dy = e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
                Native.SetCursorPos(c.X + dx, c.Y + dy);
                UpdateMagnifier(CursorPhys(), CursorDip());
                e.Handled = true;
                break;
        }
    }

    /// <summary>Selection is done: go into in-place markup for snips (when enabled) or finish right away.</summary>
    void CompleteSelection()
    {
        // The in-place Capture/markup bar is only for rectangle snips; other snip modes capture right away.
        if (mode == CaptureMode.Rectangle && BtnMarkup.IsChecked == true) EnterMarkup();
        else Finish(SnipAction.Capture);
    }

    // =================================================================== visuals

    void UpdateHover((int X, int Y) p)
    {
        hoverWin = null;
        int ax = p.X + vs.X, ay = p.Y + vs.Y;
        foreach (var w in windows)
        {
            if (ax >= w.X && ax < w.X + w.Width && ay >= w.Y && ay < w.Y + w.Height)
            {
                int x1 = Math.Max(w.X - vs.X, 0), y1 = Math.Max(w.Y - vs.Y, 0);
                int x2 = Math.Min(w.X + w.Width - vs.X, pw), y2 = Math.Min(w.Y + w.Height - vs.Y, ph);
                if (x2 > x1 && y2 > y1) hoverWin = new Int32Rect(x1, y1, x2 - x1, y2 - y1);
                break;
            }
        }
    }

    void UpdateMagnifier((int X, int Y) p, Point dip)
    {
        int half = MagCells / 2;
        var buf = new byte[MagCells * MagCells * 4];
        for (int j = 0; j < MagCells; j++)
        for (int i = 0; i < MagCells; i++)
        {
            int x = p.X - half + i, y = p.Y - half + j;
            if (x < 0 || y < 0 || x >= pw || y >= ph) continue;
            Buffer.BlockCopy(px, (y * pw + x) * 4, buf, (j * MagCells + i) * 4, 4);
        }
        MagImage.Source = BitmapSource.Create(MagCells, MagCells, 96, 96, PixelFormats.Bgr32, null, buf, MagCells * 4);

        var col = ColorAt(p.X, p.Y);
        MagSwatch.Background = new SolidColorBrush(col);
        MagText.Text = ColorUtil.Hex(col);

        Magnifier.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = Magnifier.DesiredSize;
        double left = dip.X + 22, top = dip.Y + 22;
        if (left + size.Width > Surface.ActualWidth) left = dip.X - 22 - size.Width;
        if (top + size.Height > Surface.ActualHeight) top = dip.Y - 22 - size.Height;
        Canvas.SetLeft(Magnifier, left);
        Canvas.SetTop(Magnifier, top);
    }

    Color ColorAt(int x, int y)
    {
        x = Math.Clamp(x, 0, pw - 1); y = Math.Clamp(y, 0, ph - 1);
        int i = (y * pw + x) * 4;
        return Color.FromRgb(px[i + 2], px[i + 1], px[i]);
    }

    Rect ToDip(Int32Rect r) => new(r.X / scale, r.Y / scale, r.Width / scale, r.Height / scale);

    void Redraw()
    {
        var full = new RectangleGeometry(new Rect(0, 0, pw / scale, ph / scale));
        Geometry? g = null;
        Int32Rect? labelRect = null;

        switch (mode)
        {
            case CaptureMode.Rectangle:
            case CaptureMode.Text:
            case CaptureMode.Record:
            case CaptureMode.Fullscreen:
                if (sel.Width > 0 && sel.Height > 0) { g = new RectangleGeometry(ToDip(sel)); labelRect = sel; }
                break;
            case CaptureMode.Freeform:
                if (pts.Count > 1) g = Polygon(pts, 0, 0, 1 / scale);
                break;
            case CaptureMode.Window:
                var w = markup ? sel : hoverWin;
                if (w is { Width: > 0 } ww) { g = new RectangleGeometry(ToDip(ww)); labelRect = ww; }
                break;
        }

        Dim.Data = mode == CaptureMode.ColorPicker ? null
            : g == null ? full : new CombinedGeometry(GeometryCombineMode.Exclude, full, g);
        SelOutline.Data = g;

        if (labelRect is { } lr && (!markup || resizingSel))
        {
            SizeText.Text = $"{lr.Width} × {lr.Height}";
            SizeLabel.Visibility = Visibility.Visible;
            var d = ToDip(lr);
            Canvas.SetLeft(SizeLabel, d.X);
            Canvas.SetTop(SizeLabel, d.Y >= 28 ? d.Y - 26 : d.Y + 4);
        }
        else SizeLabel.Visibility = Visibility.Collapsed;
    }

    static PathGeometry Polygon(IReadOnlyList<Point> points, double ox, double oy, double k)
    {
        var fig = new PathFigure { StartPoint = new Point((points[0].X - ox) * k, (points[0].Y - oy) * k), IsClosed = true, IsFilled = true };
        fig.Segments.Add(new PolyLineSegment(points.Skip(1).Select(p => new Point((p.X - ox) * k, (p.Y - oy) * k)), true));
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        return geo;
    }

    static Int32Rect Bounds(IReadOnlyList<Point> points)
    {
        int x1 = (int)points.Min(p => p.X), y1 = (int)points.Min(p => p.Y);
        int x2 = (int)points.Max(p => p.X), y2 = (int)points.Max(p => p.Y);
        return new Int32Rect(x1, y1, Math.Max(0, x2 - x1), Math.Max(0, y2 - y1));
    }

    // =================================================================== in-place markup

    void EnterMarkup()
    {
        markup = true;
        if (mode == CaptureMode.Fullscreen) sel = new Int32Rect(0, 0, pw, ph);
        TopPanel.Visibility = Visibility.Collapsed;
        Surface.Cursor = Cursors.Arrow;

        // The ink layer spans the whole overlay and is clipped to the selection, so the selection can be
        // resized afterwards without moving anything that has been drawn.
        Canvas.SetLeft(Ink, 0);
        Canvas.SetTop(Ink, 0);
        Ink.Width = pw / scale;
        Ink.Height = ph / scale;
        UpdateInkClip();
        Ink.Visibility = Visibility.Visible;

        SelOutline.StrokeDashArray = new DoubleCollection { 5, 4 };
        SelOutline.StrokeThickness = 1.2;
        Redraw();

        SetTool(InkTool.Pen);
        UpdateHistoryButtons();
        MarkupBar.Visibility = Visibility.Visible;
        MarkupBar.UpdateLayout();
        PositionBar();
        UpdateSelHandles();
        Activate();
        Keyboard.Focus(this);
    }

    void UpdateInkClip() =>
        Ink.Clip = mode == CaptureMode.Freeform ? Polygon(pts, 0, 0, 1 / scale) : new RectangleGeometry(ToDip(sel));

    // ---- resizable selection (like Snipping Tool: drag a corner or an edge)

    [Flags] enum Edge { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

    readonly List<(FrameworkElement Host, Edge Edges, Point Anchor)> selHandles = new();
    bool resizingSel;
    Int32Rect resizeStart;
    Edge resizeEdges;

    void BuildSelHandles()
    {
        if (selHandles.Count > 0) return;
        var white = Brushes.White;
        var shadow = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 0.6 };
        foreach (var edges in new[]
                 {
                     Edge.Left | Edge.Top, Edge.Top, Edge.Right | Edge.Top, Edge.Right,
                     Edge.Right | Edge.Bottom, Edge.Bottom, Edge.Left | Edge.Bottom, Edge.Left,
                 })
        {
            bool corner = edges is (Edge.Left | Edge.Top) or (Edge.Right | Edge.Top) or (Edge.Right | Edge.Bottom) or (Edge.Left | Edge.Bottom);
            FrameworkElement visual;
            Point anchor; // the point of the host that sits on the selection corner / edge midpoint
            double hw, hh;
            if (corner)
            {
                hw = hh = 30;
                // "L" bracket drawn for the top-left corner, mirrored for the others
                visual = new WPath
                {
                    Data = Geometry.Parse("M8,26 V8 H26"), Stroke = white, StrokeThickness = 5,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new ScaleTransform(edges.HasFlag(Edge.Right) ? -1 : 1, edges.HasFlag(Edge.Bottom) ? -1 : 1),
                    Effect = shadow, Width = hw, Height = hh
                };
                // corner of the bracket is 2px outside the selection
                anchor = new Point(edges.HasFlag(Edge.Right) ? hw - 10 : 10, edges.HasFlag(Edge.Bottom) ? hh - 10 : 10);
            }
            else
            {
                bool horizontal = edges is Edge.Top or Edge.Bottom;
                hw = horizontal ? 44 : 20;
                hh = horizontal ? 20 : 44;
                visual = new Border
                {
                    Width = horizontal ? 34 : 5, Height = horizontal ? 5 : 34, CornerRadius = new CornerRadius(2.5),
                    Background = white, Effect = shadow, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                };
                anchor = new Point(hw / 2 + (edges == Edge.Left ? 2 : edges == Edge.Right ? -2 : 0),
                                   hh / 2 + (edges == Edge.Top ? 2 : edges == Edge.Bottom ? -2 : 0));
            }
            var host = new Grid
            {
                Width = hw, Height = hh, Background = Brushes.Transparent,
                Cursor = edges switch
                {
                    Edge.Left | Edge.Top or Edge.Right | Edge.Bottom => Cursors.SizeNWSE,
                    Edge.Right | Edge.Top or Edge.Left | Edge.Bottom => Cursors.SizeNESW,
                    Edge.Left or Edge.Right => Cursors.SizeWE,
                    _ => Cursors.SizeNS,
                }
            };
            host.Children.Add(visual);
            System.Windows.Automation.AutomationProperties.SetName(host, "Resize " + edges);
            var e2 = edges;
            host.MouseLeftButtonDown += (_, e) =>
            {
                CloseFlyouts();
                resizingSel = true;
                resizeStart = sel;
                resizeEdges = e2;
                host.CaptureMouse();
                e.Handled = true;
            };
            host.MouseMove += (_, e) => { if (resizingSel) ResizeSelection(e.GetPosition(Surface)); };
            host.MouseLeftButtonUp += (_, e) =>
            {
                if (!resizingSel) return;
                resizingSel = false;
                host.ReleaseMouseCapture();
                Redraw();
                e.Handled = true;
            };
            SelHandles.Children.Add(host);
            selHandles.Add((host, edges, anchor));
        }
    }

    void ResizeSelection(Point dip)
    {
        var (px2, py2) = ToPhys(dip);
        int l = resizeStart.X, t = resizeStart.Y, r = resizeStart.X + resizeStart.Width, b = resizeStart.Y + resizeStart.Height;
        if (resizeEdges.HasFlag(Edge.Left)) l = px2;
        if (resizeEdges.HasFlag(Edge.Right)) r = px2;
        if (resizeEdges.HasFlag(Edge.Top)) t = py2;
        if (resizeEdges.HasFlag(Edge.Bottom)) b = py2;
        // dragging past the opposite edge flips the rectangle, like Snipping Tool
        int x1 = Math.Clamp(Math.Min(l, r), 0, pw), x2 = Math.Clamp(Math.Max(l, r), 0, pw);
        int y1 = Math.Clamp(Math.Min(t, b), 0, ph), y2 = Math.Clamp(Math.Max(t, b), 0, ph);
        if (x2 - x1 < 8 || y2 - y1 < 8) return;
        sel = new Int32Rect(x1, y1, x2 - x1, y2 - y1);
        UpdateInkClip();
        Redraw();
        UpdateSelHandles();
        PositionBar();
        UpdateEmojiAdorner();
    }

    void UpdateSelHandles()
    {
        bool show = markup && mode == CaptureMode.Rectangle;
        SelHandles.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        BuildSelHandles();
        var d = ToDip(sel);
        foreach (var (host, edges, anchor) in selHandles)
        {
            double x = edges.HasFlag(Edge.Left) ? d.Left : edges.HasFlag(Edge.Right) ? d.Right : d.X + d.Width / 2;
            double y = edges.HasFlag(Edge.Top) ? d.Top : edges.HasFlag(Edge.Bottom) ? d.Bottom : d.Y + d.Height / 2;
            Canvas.SetLeft(host, x - anchor.X);
            Canvas.SetTop(host, y - anchor.Y);
        }
    }

    Rect BarRect => new(Canvas.GetLeft(MarkupBar), Canvas.GetTop(MarkupBar), MarkupBar.ActualWidth, MarkupBar.ActualHeight);

    void PositionBar()
    {
        var d = ToDip(sel);
        double W = Surface.ActualWidth, H = Surface.ActualHeight;
        double bw = MarkupBar.ActualWidth, bh = MarkupBar.ActualHeight;
        double x = Math.Clamp(d.X + d.Width / 2 - bw / 2, 8, Math.Max(8, W - bw - 8));
        double y = d.Bottom + 12;
        if (y + bh > H - 8) y = d.Top - bh - 12;
        if (y < 8) y = d.Bottom - bh - 12;
        Canvas.SetLeft(MarkupBar, x);
        Canvas.SetTop(MarkupBar, Math.Max(8, y));
    }

    void Bar_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    Border[] AllFlyouts => new[] { CaptureMenu, InkFlyout, EraserFlyout, ShapesFlyout, EmojiFlyout, PaletteFlyout, ShapeMenu };

    bool AnyFlyoutOpen() => AllFlyouts.Any(f => f.IsVisible);

    void CloseFlyouts()
    {
        foreach (var f in AllFlyouts) f.Visibility = Visibility.Collapsed;
        UpdateShapeButtons();
    }

    /// <summary>Close the panels that hang off the shapes bar, keeping the shapes bar itself.</summary>
    void CloseSubFlyouts()
    {
        EmojiFlyout.Visibility = PaletteFlyout.Visibility = Visibility.Collapsed;
        UpdateShapeButtons();
    }

    static Rect RectOf(FrameworkElement e) => new(Canvas.GetLeft(e), Canvas.GetTop(e), e.ActualWidth, e.ActualHeight);

    /// <summary>
    /// Open a flyout anchored to a button, above or below <paramref name="within"/> (the markup bar by default).
    /// With <paramref name="within"/> set, the other top-level flyouts stay open.
    /// </summary>
    void OpenFlyout(Border flyout, FrameworkElement anchor, bool alignLeft = false, bool preferBelow = false, Border? within = null)
    {
        if (within == null) CloseFlyouts(); else CloseSubFlyouts();
        flyout.Visibility = Visibility.Visible;
        flyout.UpdateLayout();
        var host = within != null ? RectOf(within) : BarRect;
        var a = anchor.TranslatePoint(new Point(0, 0), Surface);
        double fw = flyout.ActualWidth, fh = flyout.ActualHeight, H = Surface.ActualHeight;
        double x = alignLeft ? host.X : a.X + anchor.ActualWidth / 2 - fw / 2;
        x = Math.Clamp(x, 8, Math.Max(8, Surface.ActualWidth - fw - 8));
        double below = host.Bottom + 6, above = host.Top - fh - 6;
        bool fitsBelow = below + fh <= H - 8, fitsAbove = above >= 8;
        double y = preferBelow ? (fitsBelow || !fitsAbove ? below : above) : (fitsAbove || !fitsBelow ? above : below);
        Canvas.SetLeft(flyout, x);
        Canvas.SetTop(flyout, Math.Clamp(y, 8, Math.Max(8, H - fh - 8)));
        UpdateShapeButtons();
    }

    void CaptureMenu_Click(object sender, RoutedEventArgs e)
    {
        if (CaptureMenu.IsVisible) CloseFlyouts();
        else OpenFlyout(CaptureMenu, BtnCaptureMenu, alignLeft: true, preferBelow: true);
    }

    void Capture_Click(object sender, RoutedEventArgs e) => Finish(SnipAction.Capture);
    void SaveTo_Click(object sender, RoutedEventArgs e) => Finish(SnipAction.SaveTo);
    void CopyOnly_Click(object sender, RoutedEventArgs e) => Finish(SnipAction.CopyOnly);
    void Share_Click(object sender, RoutedEventArgs e) => Finish(SnipAction.Share);
    void Search_Click(object sender, RoutedEventArgs e) => Finish(SnipAction.Search);

    // ---- tools

    void Tool_Click(object sender, RoutedEventArgs e)
    {
        var t = sender == ToolPen ? InkTool.Pen : sender == ToolHighlighter ? InkTool.Highlighter
            : sender == ToolEraser ? InkTool.Eraser : InkTool.Shapes;
        var btn = (FrameworkElement)sender;

        switch (t)
        {
            case InkTool.Pen or InkTool.Highlighter:
                bool wasActive = t == tool;
                SetTool(t);
                if (wasActive && !(InkFlyout.IsVisible && flyoutFor == t)) ShowInkFlyout(t, btn);
                else CloseFlyouts();
                break;
            case InkTool.Eraser:
                bool eraserActive = tool == InkTool.Eraser;
                SetTool(InkTool.Eraser);
                if (eraserActive && !EraserFlyout.IsVisible) OpenFlyout(EraserFlyout, btn, preferBelow: true);
                else CloseFlyouts();
                break;
            default:
                SetTool(tool is InkTool.Shapes or InkTool.Emoji ? tool : InkTool.Shapes);
                if (ShapesFlyout.IsVisible) CloseFlyouts();
                else OpenFlyout(ShapesFlyout, btn, preferBelow: true);
                break;
        }
    }

    void SetTool(InkTool t)
    {
        tool = t;
        ToolPen.IsChecked = t == InkTool.Pen;
        ToolHighlighter.IsChecked = t == InkTool.Highlighter;
        ToolEraser.IsChecked = t == InkTool.Eraser;
        ToolShapes.IsChecked = t is InkTool.Shapes or InkTool.Emoji;
        ApplyInk();
        UpdateShapeButtons();
    }

    static Color ParseColor(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    void ApplyInk()
    {
        var s = Settings.Current;
        var penColor = ParseColor(s.PenColor);
        var hlColor = ParseColor(s.HighlighterColor);
        PenTip.Fill = new SolidColorBrush(penColor);
        HlCap.Fill = new SolidColorBrush(hlColor);

        switch (tool)
        {
            case InkTool.Pen:
                Ink.EditingMode = InkCanvasEditingMode.Ink;
                Ink.Cursor = Cursors.Pen;
                Ink.DefaultDrawingAttributes = new DrawingAttributes
                {
                    Color = penColor, Width = s.PenSize, Height = s.PenSize, FitToCurve = true, IgnorePressure = true
                };
                break;
            case InkTool.Highlighter:
                Ink.EditingMode = InkCanvasEditingMode.Ink;
                Ink.Cursor = Cursors.Pen;
                Ink.DefaultDrawingAttributes = new DrawingAttributes
                {
                    Color = hlColor, IsHighlighter = true, Width = Math.Max(3, s.HighlighterSize / 2.5), Height = s.HighlighterSize,
                    StylusTip = StylusTip.Rectangle, FitToCurve = true, IgnorePressure = true
                };
                break;
            case InkTool.Eraser:
                Ink.EditingMode = InkCanvasEditingMode.EraseByStroke;
                break;
            case InkTool.Shapes:
                Ink.EditingMode = InkCanvasEditingMode.None;
                Ink.Cursor = Cursors.Cross;
                break;
            case InkTool.Emoji:
                Ink.EditingMode = InkCanvasEditingMode.None;
                Ink.Cursor = Cursors.Hand;
                break;
        }
    }

    Grid Swatch(string hex, bool selected, Action onPick)
    {
        var swatch = new Grid { Width = 44, Height = 44, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = hex };
        swatch.Children.Add(new Ellipse { Width = 40, Height = 40, Stroke = selected ? Brushes.White : Brushes.Transparent, StrokeThickness = 2 });
        swatch.Children.Add(new Ellipse
        {
            Width = selected ? 30 : 34, Height = selected ? 30 : 34, Fill = new SolidColorBrush(ParseColor(hex)),
            Stroke = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1
        });
        swatch.MouseLeftButtonUp += (_, ev) => { onPick(); ev.Handled = true; };
        return swatch;
    }

    void ShowInkFlyout(InkTool t, FrameworkElement anchor)
    {
        flyoutFor = t;
        var s = Settings.Current;
        string current = t == InkTool.Pen ? s.PenColor : s.HighlighterColor;

        ColourGrid.Children.Clear();
        foreach (var hex in Palette)
            ColourGrid.Children.Add(Swatch(hex, string.Equals(hex, current, StringComparison.OrdinalIgnoreCase), () =>
            {
                if (flyoutFor == InkTool.Pen) s.PenColor = hex; else s.HighlighterColor = hex;
                s.Save();
                ApplyInk();
                ShowInkFlyout(flyoutFor, anchor);
            }));

        SizeSlider.Maximum = t == InkTool.Pen ? 24 : 40;
        SizeSlider.Value = t == InkTool.Pen ? s.PenSize : s.HighlighterSize;
        UpdateSizePreview();
        OpenFlyout(InkFlyout, anchor);
    }

    void UpdateSizePreview()
    {
        var s = Settings.Current;
        bool pen = flyoutFor == InkTool.Pen;
        var c = ParseColor(pen ? s.PenColor : s.HighlighterColor);
        if (!pen) c.A = 0x90;
        SizePreview.Stroke = new SolidColorBrush(c);
        SizePreview.StrokeThickness = pen ? s.PenSize : s.HighlighterSize;
    }

    void SizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || SizePreview == null) return;
        var s = Settings.Current;
        double v = Math.Round(e.NewValue);
        if (flyoutFor == InkTool.Pen) s.PenSize = v; else s.HighlighterSize = v;
        s.Save();
        UpdateSizePreview();
        ApplyInk();
    }

    // ---- eraser

    void EraseAll_Click(object sender, RoutedEventArgs e)
    {
        CloseFlyouts();
        if (Ink.Strokes.Count == 0 && Ink.Children.Count == 0) return;
        var strokes = new StrokeCollection(Ink.Strokes);
        var els = Ink.Children.Cast<UIElement>().ToArray();
        applyingHistory = true;
        Ink.Strokes.Clear();
        Ink.Children.Clear();
        applyingHistory = false;
        UpdateEmojiAdorner();
        Push(new Edit(new StrokeCollection(), strokes, NoEls, els));
    }

    // ---- shapes bar (emoji, shape kinds, fill, outline)

    static readonly string[] Emojis =
    {
        "\U0001F497", "⭐", "❓", "✔️", "❌", "\U0001F525",
        "\U0001F44D", "\U0001F44E", "\U0001F44F", "\U0001F64C", "\U0001F440", "\U0001F4AF",
        "\U0001F642", "\U0001F641", "\U0001F62E", "\U0001F60D", "\U0001F602", "\U0001F92F",
    };

    void BuildShapesFlyout()
    {
        foreach (var emo in Emojis)
        {
            var cell = new Button
            {
                Width = 48, Height = 46, Cursor = Cursors.Hand, Template = Toast.FlatButtonTemplate(), FocusVisualStyle = null,
                Content = new Image { Width = 30, Height = 30, Source = EmojiRenderer.Render(emo, 60) }
            };
            System.Windows.Automation.AutomationProperties.SetName(cell, "Emoji " + emo);
            cell.Click += (_, _) =>
            {
                currentEmoji = emo;
                EmojiFlyout.Visibility = Visibility.Collapsed;
                SetTool(InkTool.Emoji);
            };
            EmojiGrid.Children.Add(cell);
        }
        UpdateShapeButtons();
    }

    void UpdateShapeButtons()
    {
        if (ShapeRect == null) return;
        bool shapes = tool == InkTool.Shapes;
        ShapeRect.IsChecked = shapes && shapeKind == ShapeKind.Rectangle;
        ShapeEllipse.IsChecked = shapes && shapeKind == ShapeKind.Ellipse;
        ShapeLine.IsChecked = shapes && shapeKind == ShapeKind.Line;
        ShapeArrow.IsChecked = shapes && shapeKind == ShapeKind.Arrow;
        BtnEmoji.IsChecked = tool == InkTool.Emoji || EmojiFlyout.IsVisible;

        var s = Settings.Current;
        bool noFill = string.IsNullOrEmpty(s.ShapeFill);
        FillSwatch.Fill = noFill ? null : new SolidColorBrush(ParseColor(s.ShapeFill));
        FillSwatch.Stroke = noFill ? new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)) : new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));
        FillNone.Visibility = noFill ? Visibility.Visible : Visibility.Collapsed;
        var outline = new SolidColorBrush(ParseColor(s.ShapeOutline));
        OutlineRing.Stroke = outline;
        OutlineDot.Fill = outline;
    }

    void ShapeKind_Click(object sender, RoutedEventArgs e)
    {
        shapeKind = sender == ShapeEllipse ? ShapeKind.Ellipse : sender == ShapeLine ? ShapeKind.Line
            : sender == ShapeArrow ? ShapeKind.Arrow : ShapeKind.Rectangle;
        SetTool(InkTool.Shapes);
        CloseSubFlyouts();
    }

    void Emoji_Click(object sender, RoutedEventArgs e)
    {
        if (EmojiFlyout.IsVisible) CloseSubFlyouts();
        else OpenFlyout(EmojiFlyout, BtnEmoji, alignLeft: true, within: ShapesFlyout);
    }

    void Fill_Click(object sender, RoutedEventArgs e) => TogglePalette(true, BtnFill);
    void Outline_Click(object sender, RoutedEventArgs e) => TogglePalette(false, BtnOutline);

    void TogglePalette(bool fill, FrameworkElement anchor, bool refresh = false)
    {
        if (!refresh && PaletteFlyout.IsVisible && paletteForFill == fill) { CloseSubFlyouts(); return; }
        paletteForFill = fill;
        var s = Settings.Current;
        PaletteTitle.Text = fill ? "Fill" : "Outline";
        BtnNoFill.Visibility = fill ? Visibility.Visible : Visibility.Collapsed;
        OutlineSizePanel.Visibility = fill ? Visibility.Collapsed : Visibility.Visible;
        OutlineSlider.Value = s.ShapeSize;
        string current = fill ? s.ShapeFill : s.ShapeOutline;
        PaletteGrid.Children.Clear();
        foreach (var hex in Palette)
            PaletteGrid.Children.Add(Swatch(hex, string.Equals(hex, current, StringComparison.OrdinalIgnoreCase), () =>
            {
                if (fill) s.ShapeFill = hex; else s.ShapeOutline = hex;
                s.Save();
                if (tool != InkTool.Shapes) SetTool(InkTool.Shapes);
                TogglePalette(fill, anchor, refresh: true);
            }));
        OpenFlyout(PaletteFlyout, anchor, within: ShapesFlyout);
    }

    void NoFill_Click(object sender, RoutedEventArgs e)
    {
        Settings.Current.ShapeFill = "";
        Settings.Current.Save();
        CloseSubFlyouts();
    }

    void OutlineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        Settings.Current.ShapeSize = Math.Round(e.NewValue);
        Settings.Current.Save();
    }

    // ---- shapes, emoji stamps and eraser hits on the ink layer

    void Ink_ShapeDown(object sender, MouseButtonEventArgs e)
    {
        if (tool != InkTool.Eraser && e.OriginalSource is Image hitEmoji && hitEmoji.Tag is EmojiInfo && Ink.Children.Contains(hitEmoji))
        {
            CloseSubFlyouts();
            SelectEmoji(hitEmoji);
            BeginEmojiDrag(EmojiDrag.Move, e, hitEmoji);
            return;
        }
        if (selectedEmoji != null)
        {
            // first click outside a selected emoji only deselects it (like Snipping Tool)
            bool stampTool = tool == InkTool.Emoji;
            SelectEmoji(null);
            if (stampTool) { e.Handled = true; return; }
        }
        if (tool == InkTool.Eraser)
        {
            // Stroke erasing is built in; shapes and emoji are elements, so remove the one that was clicked.
            if (e.OriginalSource is UIElement hit && hit != Ink && Ink.Children.Contains(hit))
            {
                Ink.Children.Remove(hit);
                Push(new Edit(new StrokeCollection(), new StrokeCollection(), NoEls, new[] { hit }));
                UpdateEmojiAdorner();
                e.Handled = true;
            }
            return;
        }
        if (tool is not (InkTool.Shapes or InkTool.Emoji)) return;
        CloseSubFlyouts();
        var pos = e.GetPosition(Ink);

        if (tool == InkTool.Emoji)
        {
            var img = CreateEmoji(currentEmoji, pos, 56);
            Ink.Children.Add(img);
            Push(new Edit(new StrokeCollection(), new StrokeCollection(), new UIElement[] { img }, NoEls));
            SelectEmoji(img);
            e.Handled = true;
            return;
        }

        var s = Settings.Current;
        var outline = new SolidColorBrush(ParseColor(s.ShapeOutline));
        Brush? fill = shapeKind switch
        {
            ShapeKind.Arrow => outline, // filled arrow head
            ShapeKind.Line => null,
            _ => string.IsNullOrEmpty(s.ShapeFill) ? null : new SolidColorBrush(ParseColor(s.ShapeFill)),
        };
        shapeStart = pos;
        shapePath = new WPath
        {
            Stroke = outline, StrokeThickness = s.ShapeSize, Fill = fill, StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Data = ShapeGeometry(pos, pos)
        };
        Ink.Children.Add(shapePath);
        Ink.CaptureMouse();
        e.Handled = true;
    }

    void Ink_ShapeMove(object sender, MouseEventArgs e)
    {
        if (emojiDrag != EmojiDrag.None) { MoveEmojiDrag(e); return; }
        if (tool != InkTool.Shapes || shapePath == null) return;
        var p = e.GetPosition(Ink);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && shapeKind is ShapeKind.Rectangle or ShapeKind.Ellipse)
        {
            double side = Math.Max(Math.Abs(p.X - shapeStart.X), Math.Abs(p.Y - shapeStart.Y));
            p = new Point(shapeStart.X + Math.Sign(p.X - shapeStart.X) * side, shapeStart.Y + Math.Sign(p.Y - shapeStart.Y) * side);
        }
        shapePath.Data = ShapeGeometry(shapeStart, p);
        e.Handled = true;
    }

    void Ink_ShapeUp(object sender, MouseButtonEventArgs e)
    {
        if (emojiDrag != EmojiDrag.None) { EndEmojiDrag(e); return; }
        if (tool != InkTool.Shapes || shapePath == null) return;
        Ink.ReleaseMouseCapture();
        var b = shapePath.Data.Bounds;
        if (b.Width < 3 && b.Height < 3) Ink.Children.Remove(shapePath);
        else Push(new Edit(new StrokeCollection(), new StrokeCollection(), new UIElement[] { shapePath }, NoEls));
        shapePath = null;
        e.Handled = true;
    }

    Geometry ShapeGeometry(Point a, Point b)
    {
        switch (shapeKind)
        {
            case ShapeKind.Rectangle: return new RectangleGeometry(new Rect(a, b));
            case ShapeKind.Ellipse: return new EllipseGeometry(new Rect(a, b));
            case ShapeKind.Line: return new LineGeometry(a, b);
            default:
                double ang = Math.Atan2(b.Y - a.Y, b.X - a.X);
                double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                double head = Math.Min(len, Math.Max(12, Settings.Current.ShapeSize * 3.5));
                var basePt = new Point(b.X - head * 0.8 * Math.Cos(ang), b.Y - head * 0.8 * Math.Sin(ang));
                var h1 = new Point(b.X - head * Math.Cos(ang - 0.42), b.Y - head * Math.Sin(ang - 0.42));
                var h2 = new Point(b.X - head * Math.Cos(ang + 0.42), b.Y - head * Math.Sin(ang + 0.42));
                var g = new PathGeometry();
                var shaft = new PathFigure { StartPoint = a, IsFilled = false };
                shaft.Segments.Add(new LineSegment(basePt, true));
                var tip = new PathFigure { StartPoint = b, IsClosed = true, IsFilled = true };
                tip.Segments.Add(new PolyLineSegment(new[] { h1, h2 }, true));
                g.Figures.Add(shaft);
                g.Figures.Add(tip);
                return g;
        }
    }

    // ---- undo / redo (ink strokes plus shape / emoji elements)

    sealed record Edit(StrokeCollection AddedStrokes, StrokeCollection RemovedStrokes, UIElement[] AddedEls, UIElement[] RemovedEls,
        Image? Target = null, EmojiState Before = default, EmojiState After = default);

    static readonly UIElement[] NoEls = Array.Empty<UIElement>();

    void Push(Edit e)
    {
        undo.Push(e);
        redo.Clear();
        UpdateHistoryButtons();
    }

    void Strokes_Changed(object? sender, StrokeCollectionChangedEventArgs e)
    {
        if (applyingHistory) return;
        Push(new Edit(e.Added, e.Removed, NoEls, NoEls));
    }

    void UpdateHistoryButtons()
    {
        BtnUndo.IsEnabled = undo.Count > 0;
        BtnRedo.IsEnabled = redo.Count > 0;
    }

    void Apply(Edit e, bool forward)
    {
        var (addS, remS, addE, remE) = forward
            ? (e.AddedStrokes, e.RemovedStrokes, e.AddedEls, e.RemovedEls)
            : (e.RemovedStrokes, e.AddedStrokes, e.RemovedEls, e.AddedEls);
        applyingHistory = true;
        if (remS.Count > 0) Ink.Strokes.Remove(remS);
        if (addS.Count > 0) Ink.Strokes.Add(addS);
        foreach (var el in remE) Ink.Children.Remove(el);
        foreach (var el in addE) Ink.Children.Add(el);
        if (e.Target != null) SetEmojiState(e.Target, forward ? e.After : e.Before, rerender: true);
        applyingHistory = false;
        UpdateEmojiAdorner();
    }

    // ---- emoji objects: select, move, resize, rotate

    sealed class EmojiInfo { public string Emoji = ""; }
    internal readonly record struct EmojiState(double X, double Y, double Size, double Angle);
    enum EmojiDrag { None, Move, Resize, Rotate }

    const double AdornerPad = 6;
    Image? selectedEmoji;
    EmojiDrag emojiDrag;
    EmojiState dragStart;
    Point dragOrigin;
    UIElement? dragCapture;
    bool adornerWired;

    int EmojiPx(double size) => (int)Math.Ceiling(Math.Clamp(size * scale, 16, 768));

    Image CreateEmoji(string emoji, Point center, double size)
    {
        var img = new Image
        {
            Width = size, Height = size, Tag = new EmojiInfo { Emoji = emoji }, Cursor = Cursors.SizeAll,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(0),
            Source = EmojiRenderer.Render(emoji, EmojiPx(size))
        };
        InkCanvas.SetLeft(img, center.X - size / 2);
        InkCanvas.SetTop(img, center.Y - size / 2);
        return img;
    }

    static double AngleOf(Image img) => img.RenderTransform is RotateTransform rt ? rt.Angle : 0;

    EmojiState GetEmojiState(Image img) => new(InkCanvas.GetLeft(img), InkCanvas.GetTop(img), img.Width, AngleOf(img));

    void SetEmojiState(Image img, EmojiState st, bool rerender)
    {
        InkCanvas.SetLeft(img, st.X);
        InkCanvas.SetTop(img, st.Y);
        img.Width = img.Height = st.Size;
        img.RenderTransform = new RotateTransform(st.Angle);
        if (rerender && img.Tag is EmojiInfo info) img.Source = EmojiRenderer.Render(info.Emoji, EmojiPx(st.Size));
    }

    void SelectEmoji(Image? img)
    {
        selectedEmoji = img;
        UpdateEmojiAdorner();
    }

    void WireAdorner()
    {
        if (adornerWired) return;
        adornerWired = true;
        foreach (var h in new FrameworkElement[] { HandleTL, HandleTR, HandleBL, HandleBR })
        {
            h.MouseLeftButtonDown += (_, e) => BeginEmojiDrag(EmojiDrag.Resize, e, h);
            h.MouseMove += (_, e) => { if (emojiDrag != EmojiDrag.None) MoveEmojiDrag(e); };
            h.MouseLeftButtonUp += (_, e) => { if (emojiDrag != EmojiDrag.None) EndEmojiDrag(e); };
        }
        RotateHandle.MouseLeftButtonDown += (_, e) => BeginEmojiDrag(EmojiDrag.Rotate, e, RotateHandle);
        RotateHandle.MouseMove += (_, e) => { if (emojiDrag != EmojiDrag.None) MoveEmojiDrag(e); };
        RotateHandle.MouseLeftButtonUp += (_, e) => { if (emojiDrag != EmojiDrag.None) EndEmojiDrag(e); };
    }

    void UpdateEmojiAdorner()
    {
        WireAdorner();
        if (selectedEmoji == null || !Ink.Children.Contains(selectedEmoji) || Ink.Visibility != Visibility.Visible)
        {
            selectedEmoji = null;
            EmojiAdorner.Visibility = Visibility.Collapsed;
            return;
        }
        var st = GetEmojiState(selectedEmoji);
        double w = st.Size + AdornerPad * 2;
        Canvas.SetLeft(EmojiAdorner, Canvas.GetLeft(Ink) + st.X - AdornerPad);
        Canvas.SetTop(EmojiAdorner, Canvas.GetTop(Ink) + st.Y - AdornerPad);
        EmojiAdorner.Width = EmojiAdorner.Height = w;
        AdornerBox.Width = AdornerBox.Height = w;
        void Place(FrameworkElement h, double x, double y) { Canvas.SetLeft(h, x - h.Width / 2); Canvas.SetTop(h, y - h.Height / 2); }
        Place(HandleTL, 0, 0);
        Place(HandleTR, w, 0);
        Place(HandleBL, 0, w);
        Place(HandleBR, w, w);
        Place(RotateHandle, w / 2, -RotateHandle.Height / 2 - 16);
        EmojiAdorner.RenderTransform = new RotateTransform(st.Angle, w / 2, w / 2);
        EmojiAdorner.Visibility = Visibility.Visible;
    }

    void BeginEmojiDrag(EmojiDrag kind, MouseButtonEventArgs e, UIElement captureOn)
    {
        if (selectedEmoji == null) return;
        emojiDrag = kind;
        dragStart = GetEmojiState(selectedEmoji);
        dragOrigin = e.GetPosition(Ink);
        dragCapture = captureOn;
        captureOn.CaptureMouse();
        e.Handled = true;
    }

    void MoveEmojiDrag(MouseEventArgs e)
    {
        if (selectedEmoji == null) return;
        var p = e.GetPosition(Ink);
        var c = new Point(dragStart.X + dragStart.Size / 2, dragStart.Y + dragStart.Size / 2);
        EmojiState next = dragStart;
        switch (emojiDrag)
        {
            case EmojiDrag.Move:
                next = dragStart with { X = dragStart.X + p.X - dragOrigin.X, Y = dragStart.Y + p.Y - dragOrigin.Y };
                break;
            case EmojiDrag.Resize:
                // resize around the centre: measure the pointer in the emoji's own (unrotated) frame
                double a = -dragStart.Angle * Math.PI / 180, dx = p.X - c.X, dy = p.Y - c.Y;
                double lx = dx * Math.Cos(a) - dy * Math.Sin(a), ly = dx * Math.Sin(a) + dy * Math.Cos(a);
                double size = Math.Clamp(2 * (Math.Max(Math.Abs(lx), Math.Abs(ly)) - AdornerPad), 16, 640);
                next = dragStart with { X = c.X - size / 2, Y = c.Y - size / 2, Size = size };
                break;
            case EmojiDrag.Rotate:
                double ang = Math.Atan2(p.Y - c.Y, p.X - c.X) * 180 / Math.PI + 90;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) ang = Math.Round(ang / 15) * 15;
                ang = ((ang % 360) + 360) % 360;
                next = dragStart with { Angle = ang };
                break;
        }
        SetEmojiState(selectedEmoji, next, rerender: false);
        UpdateEmojiAdorner();
        e.Handled = true;
    }

    void EndEmojiDrag(MouseButtonEventArgs e)
    {
        var kind = emojiDrag;
        emojiDrag = EmojiDrag.None;
        dragCapture?.ReleaseMouseCapture();
        dragCapture = null;
        e.Handled = true;
        if (selectedEmoji == null) return;
        var after = GetEmojiState(selectedEmoji);
        if (kind == EmojiDrag.Resize) SetEmojiState(selectedEmoji, after, rerender: true); // crisp at the new size
        if (after != dragStart)
            Push(new Edit(new StrokeCollection(), new StrokeCollection(), NoEls, NoEls, selectedEmoji, dragStart, after));
    }

    void DeleteSelectedEmoji()
    {
        if (selectedEmoji == null) return;
        var img = selectedEmoji;
        Ink.Children.Remove(img);
        Push(new Edit(new StrokeCollection(), new StrokeCollection(), NoEls, new UIElement[] { img }));
        SelectEmoji(null);
    }

    void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (undo.Count == 0) return;
        var edit = undo.Pop();
        Apply(edit, false);
        redo.Push(edit);
        UpdateHistoryButtons();
    }

    void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (redo.Count == 0) return;
        var edit = redo.Pop();
        Apply(edit, true);
        undo.Push(edit);
        UpdateHistoryButtons();
    }

    /// <summary>Trash icon: discard this snip and close the overlay.</summary>
    void Discard_Click(object sender, RoutedEventArgs e) => Cancel();

    // =================================================================== result

    BitmapSource Crop(Int32Rect r)
    {
        var buf = new byte[r.Width * r.Height * 4];
        for (int y = 0; y < r.Height; y++)
            Buffer.BlockCopy(px, ((r.Y + y) * pw + r.X) * 4, buf, y * r.Width * 4, r.Width * 4);
        var bmp = BitmapSource.Create(r.Width, r.Height, 96, 96, PixelFormats.Bgr32, null, buf, r.Width * 4);
        bmp.Freeze();
        return bmp;
    }

    BitmapSource CropFreeform(Int32Rect bbox)
    {
        var img = Crop(bbox);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushClip(Polygon(pts, bbox.X, bbox.Y, 1));
            dc.DrawImage(img, new Rect(0, 0, bbox.Width, bbox.Height));
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(bbox.Width, bbox.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>The selection with the in-place ink flattened on top, at physical pixel size.</summary>
    BitmapSource FlattenMarkup()
    {
        var baseImg = mode == CaptureMode.Freeform ? CropFreeform(sel) : Crop(sel);
        if (Ink.Strokes.Count == 0 && Ink.Children.Count == 0) return baseImg;
        var r = new Rect(0, 0, sel.Width / scale, sel.Height / scale);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            if (mode == CaptureMode.Freeform) dc.PushClip(Polygon(pts, sel.X, sel.Y, 1 / scale));
            dc.DrawImage(baseImg, r);
            dc.PushClip(new RectangleGeometry(r));
            var origin = ToDip(sel);
            dc.PushTransform(new TranslateTransform(-origin.X, -origin.Y));
            foreach (UIElement el in Ink.Children)
            {
                if (el is WPath p)
                    dc.DrawGeometry(p.Fill, new Pen(p.Stroke, p.StrokeThickness)
                    {
                        LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round
                    }, p.Data);
                else if (el is Image im)
                {
                    var st = GetEmojiState(im);
                    var src = im.Tag is EmojiInfo info ? EmojiRenderer.Render(info.Emoji, EmojiPx(st.Size)) : im.Source;
                    dc.PushTransform(new RotateTransform(st.Angle, st.X + st.Size / 2, st.Y + st.Size / 2));
                    dc.DrawImage(src, new Rect(st.X, st.Y, st.Size, st.Size));
                    dc.Pop();
                }
            }
            Ink.Strokes.Draw(dc);
            dc.Pop();
            dc.Pop();
            if (mode == CaptureMode.Freeform) dc.Pop();
        }
        var rtb = new RenderTargetBitmap(sel.Width, sel.Height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    void Finish(SnipAction action)
    {
        if (finished) return;
        finished = true;

        OverlayResult result;
        switch (mode)
        {
            case CaptureMode.ColorPicker:
                var (cx, cy) = CursorPhys();
                result = new OverlayResult(mode, null, default, ColorAt(cx, cy));
                break;
            case CaptureMode.Record:
                result = new OverlayResult(mode, null, new Int32Rect(sel.X + vs.X, sel.Y + vs.Y, sel.Width, sel.Height), default);
                break;
            default:
                if (mode == CaptureMode.Fullscreen) sel = new Int32Rect(0, 0, pw, ph);
                var img = markup ? FlattenMarkup() : mode == CaptureMode.Freeform ? CropFreeform(sel) : Crop(sel);
                result = new OverlayResult(mode, img, sel, default, action);
                break;
        }

        Close();
        Completed?.Invoke(result);
    }
}

// Hooks used only by SelfTest to render the markup UI states.
public partial class OverlayWindow
{
    internal void TestEnterMarkup(Int32Rect r)
    {
        mode = CaptureMode.Rectangle;
        sel = r;
        EnterMarkup();
        var s = new StylusPointCollection { new StylusPoint(r.X / scale + 30, r.Y / scale + 60), new StylusPoint(r.X / scale + 160, r.Y / scale + 40), new StylusPoint(r.X / scale + 300, r.Y / scale + 70) };
        Ink.Strokes.Add(new Stroke(s, Ink.DefaultDrawingAttributes.Clone()));
    }

    internal void TestOpen(string which)
    {
        switch (which)
        {
            case "capture": OpenFlyout(CaptureMenu, BtnCaptureMenu, true); break;
            case "colours": ShowInkFlyout(InkTool.Pen, ToolPen); break;
            case "shapes": SetTool(InkTool.Shapes); OpenFlyout(ShapesFlyout, ToolShapes, preferBelow: true); break;
            case "emoji": Emoji_Click(this, new RoutedEventArgs()); break;
            case "fill": TogglePalette(true, BtnFill); break;
            case "outline": TogglePalette(false, BtnOutline); break;
            case "eraser": SetTool(InkTool.Eraser); OpenFlyout(EraserFlyout, ToolEraser, preferBelow: true); break;
            case "shapemenu": ShapeMenu_Click(this, new RoutedEventArgs()); break;
            default: CloseFlyouts(); break;
        }
    }

    internal BitmapSource TestFlatten() => FlattenMarkup();

    /// <summary>Simulates dragging the bottom-right handle by (dx, dy) physical pixels.</summary>
    internal Int32Rect TestResize(int dx, int dy)
    {
        resizeStart = sel;
        resizeEdges = Edge.Right | Edge.Bottom;
        resizingSel = true;
        ResizeSelection(new Point((sel.X + sel.Width + dx) / scale, (sel.Y + sel.Height + dy) / scale));
        resizingSel = false;
        Redraw();
        return sel;
    }

    /// <summary>Distinct button heights (and the Capture pill) per bar, for alignment checks.</summary>
    internal string TestButtonHeights()
    {
        string Heights(FrameworkElement bar)
        {
            bar.UpdateLayout();
            var hs = new SortedSet<double>();
            void Walk(DependencyObject d)
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
                {
                    var c = VisualTreeHelper.GetChild(d, i);
                    if (c is ButtonBase b && b.IsVisible && b.Name != "BtnCapture" && b.Name != "BtnCaptureMenu") hs.Add(Math.Round(b.ActualHeight, 1));
                    Walk(c);
                }
            }
            Walk(bar);
            return string.Join("/", hs);
        }
        var capture = (FrameworkElement)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(BtnCapture));
        return $"top={Heights(Toolbar)} markup={Heights(MarkupBar)} shapes={Heights(ShapesFlyout)} capturePill={capture.ActualHeight}+margins={capture.Margin.Top + capture.Margin.Bottom}";
    }

    internal void TestAddShapes()
    {
        var o = ToDip(sel).TopLeft;
        Point P(double x, double y) => new(o.X + x, o.Y + y);
        shapeKind = ShapeKind.Arrow;
        var arrow = new WPath { Stroke = Brushes.Red, StrokeThickness = 4, Fill = Brushes.Red, Data = ShapeGeometry(P(420, 260), P(560, 160)) };
        shapeKind = ShapeKind.Rectangle;
        var rect = new WPath { Stroke = Brushes.Red, StrokeThickness = 4, Data = ShapeGeometry(P(600, 120), P(820, 260)) };
        var img = CreateEmoji("\U0001F525", P(328, 208), 56);
        SetEmojiState(img, GetEmojiState(img) with { Angle = 30 }, rerender: false);
        Ink.Children.Add(arrow); Ink.Children.Add(rect); Ink.Children.Add(img);
        SelectEmoji(img);
    }

    internal double TestToolbarWidth(CaptureMode m)
    {
        SetMode(m, false);
        Toolbar.UpdateLayout();
        return Toolbar.ActualWidth;
    }
}
