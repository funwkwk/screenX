using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ellipse = System.Windows.Shapes.Ellipse;
using WPath = System.Windows.Shapes.Path;

namespace ScreenX;

/// <summary>
/// Snipping-Tool style window: New / mode / delay on the left, markup tools and crop in the middle,
/// Edit in Paint / save / copy / share on the right, and the snip centred on a dark canvas.
/// </summary>
public partial class EditorWindow : Window
{
    enum InkTool { Pen, Highlighter, Eraser, Shapes }
    enum ShapeKind { Rectangle, Ellipse, Line, Arrow }

    static readonly string[] Palette =
    {
        "#000000", "#FFFFFF", "#E1E1E1", "#C8C8C8", "#A0A0A0", "#7A7A7A",
        "#E3008C", "#E81123", "#F7630C", "#FF8C00", "#FFB900", "#FCE100",
        "#8CBD18", "#16C60C", "#0B6A0B", "#00B7C3", "#0078D4", "#4F4BD9",
        "#744DA9", "#B146C2", "#F7D7C4", "#D8A66C", "#CA5010", "#8E562E",
    };

    BitmapSource? current;
    string? savedPath;
    InkTool tool = InkTool.Pen;
    ShapeKind shapeKind = ShapeKind.Rectangle;
    Popup? openPopup;
    readonly DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromSeconds(2.6) };

    public EditorWindow(BitmapSource? image, string? savedPath)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WinUtil.DarkTitleBar(this);
        DpiChanged += (_, _) => SizeBoard();
        Closing += OnClosing;
        statusTimer.Tick += (_, _) => { statusTimer.Stop(); StatusPill.Visibility = Visibility.Collapsed; };

        Ink.Strokes.StrokesChanged += Strokes_Changed;
        Ink.PreviewMouseLeftButtonDown += Ink_Down;
        Ink.PreviewMouseMove += Ink_Move;
        Ink.PreviewMouseLeftButtonUp += Ink_Up;

        RefreshMode();
        RefreshDelay();
        SetTool(InkTool.Pen);

        Bind(Key.N, ModifierKeys.Control, () => New_Click(this, new RoutedEventArgs()));
        Bind(Key.C, ModifierKeys.Control, () => { if (current != null && !cropping) Copy_Click(this, new RoutedEventArgs()); });
        Bind(Key.S, ModifierKeys.Control, () => { if (current != null && !cropping) Save_Click(this, new RoutedEventArgs()); });
        Bind(Key.Z, ModifierKeys.Control, () => Undo_Click(this, new RoutedEventArgs()));
        Bind(Key.Y, ModifierKeys.Control, () => Redo_Click(this, new RoutedEventArgs()));
        PreviewKeyDown += (_, e) =>
        {
            if (!cropping) return;
            if (e.Key == Key.Enter) { CropApply_Click(this, e); e.Handled = true; }
            else if (e.Key == Key.Escape) { CropCancel_Click(this, e); e.Handled = true; }
        };

        LoadImage(image, savedPath, fit: false);
        Loaded += (_, _) => { if (current != null) FitToImage(); };
    }

    void Bind(Key key, ModifierKeys mods, Action run) => InputBindings.Add(new KeyBinding(new RelayCommand(run), key, mods));

    // =================================================================== image

    /// <summary>Show a new snip (from the constructor or the New button), resetting mark-ups and history.</summary>
    public void LoadImage(BitmapSource? image, string? path, bool fit = true)
    {
        if (cropping) EndCrop();
        current = image;
        savedPath = path;
        Img.Source = image;
        applyingHistory = true;
        Ink.Strokes.Clear();
        Ink.Children.Clear();
        applyingHistory = false;
        undo.Clear(); redo.Clear();
        dirty = false;
        bool has = image != null;
        Fit.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        ToolsPanel.IsEnabled = has;
        foreach (var b in new UIElement[] { BtnSave, BtnCopy, BtnShare }) b.IsEnabled = has;
        UpdateHistoryButtons();
        if (has)
        {
            SizeBoard();
            if (fit && IsLoaded) FitToImage();
            Flash($"{image!.PixelWidth} × {image.PixelHeight} px" + (path != null ? "  ·  saved to " + Path.GetFileName(path) : ""));
        }
    }

    public bool HasImage => current != null;

    public void RestoreAndActivate()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        Native.SetForegroundWindow(WinUtil.Hwnd(this));
    }

    void SizeBoard()
    {
        if (current == null) return;
        double s = WinUtil.Scale(this);
        Paper.Width = current.PixelWidth / s;
        Paper.Height = current.PixelHeight / s;
    }

    void FitToImage()
    {
        SizeBoard();
        var wa = SystemParameters.WorkArea;
        Width = Math.Clamp(Paper.Width + 100, MinWidth, wa.Width * 0.92);
        Height = Math.Clamp(Paper.Height + 160, MinHeight, wa.Height * 0.92);
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top + (wa.Height - Height) / 2;
        Activate();
    }

    void Flash(string text)
    {
        Status.Text = text;
        StatusPill.Visibility = Visibility.Visible;
        statusTimer.Stop();
        statusTimer.Start();
    }

    /// <summary>The snip with mark-ups flattened onto it, at the original pixel size.</summary>
    BitmapSource Flatten()
    {
        var img = current!;
        if (Ink.Strokes.Count == 0 && Ink.Children.Count == 0) return img;
        double s = WinUtil.Scale(this);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var r = new Rect(0, 0, Paper.ActualWidth, Paper.ActualHeight);
            dc.DrawImage(img, r);
            dc.DrawRectangle(new VisualBrush(Ink) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = r }, null, r);
        }
        var rtb = new RenderTargetBitmap(img.PixelWidth, img.PixelHeight, 96 * s, 96 * s, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    // =================================================================== popups

    Popup ShowPopup(FrameworkElement anchor, UIElement content, bool alignRight = false)
    {
        openPopup?.SetCurrentValue(Popup.IsOpenProperty, false);
        var p = new Popup
        {
            PlacementTarget = anchor, Placement = alignRight ? PlacementMode.Custom : PlacementMode.Bottom,
            AllowsTransparency = true, StaysOpen = false, PopupAnimation = PopupAnimation.Fade,
            Child = new Border { Style = (Style)FindResource("Card"), Child = content }
        };
        // the popup inherits fonts from its anchor (an icon-font button), so reset text to the UI font
        TextElement.SetFontFamily(p.Child, FontFamily);
        TextElement.SetForeground(p.Child, Brushes.White);
        if (alignRight)
            p.CustomPopupPlacementCallback = (size, target, _) =>
                new[] { new CustomPopupPlacement(new Point(target.Width - size.Width + 8, target.Height - 4), PopupPrimaryAxis.Horizontal) };
        p.Closed += (_, _) => { if (openPopup == p) openPopup = null; };
        openPopup = p;
        p.IsOpen = true;
        return p;
    }

    void ClosePopup() { if (openPopup != null) openPopup.IsOpen = false; }

    Button MenuRow(UIElement icon, string label, bool selected, Action onClick)
    {
        var dock = new DockPanel { MinWidth = 190 };
        var check = new TextBlock
        {
            Text = selected ? "" : "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 12,
            Foreground = (Brush)FindResource("Accent"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0)
        };
        DockPanel.SetDock(check, Dock.Right);
        dock.Children.Add(check);
        dock.Children.Add(new Border { Width = 22, Child = icon });
        dock.Children.Add(new TextBlock { Text = label, FontSize = 14, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Style = (Style)FindResource("MenuRow"), Content = dock };
        System.Windows.Automation.AutomationProperties.SetName(b, label);
        b.Click += (_, _) => { ClosePopup(); onClick(); };
        return b;
    }

    UIElement Glyph(string g, double size = 16) => new TextBlock
    {
        Text = g, FontFamily = (FontFamily)FindResource("Icons"), FontSize = size, Foreground = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };

    UIElement PathIcon(string key) => new WPath
    {
        Data = (Geometry)FindResource(key), Stroke = Brushes.White, StrokeThickness = 1.4, Width = 20, Height = 20,
        StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
    };

    UIElement ModeIconFor(CaptureMode m) => m switch
    {
        CaptureMode.Freeform => Glyph(""),
        CaptureMode.Window => Glyph(""),
        CaptureMode.Fullscreen => Glyph(""),
        _ => PathIcon("IcoRectSnip"),
    };

    // =================================================================== left: new / mode / delay

    bool videoMode;

    void Photo_Click(object sender, RoutedEventArgs e) { videoMode = false; RefreshMode(); }
    void Video_Click(object sender, RoutedEventArgs e) { videoMode = true; RefreshMode(); }

    void RefreshMode()
    {
        BtnPhoto.IsChecked = !videoMode;
        BtnVideo.IsChecked = videoMode;
        BtnMode.IsEnabled = !videoMode;
        BtnDelay.IsEnabled = !videoMode;
        ModeIcon.Content = ModeIconFor(Settings.Current.LastSnipMode);
        BtnNew.ToolTip = videoMode ? "New recording (Ctrl+N)" : "New snip (Ctrl+N)";
    }

    void Mode_Click(object sender, RoutedEventArgs e)
    {
        var list = new StackPanel();
        foreach (var (m, label) in new[] { (CaptureMode.Rectangle, "Rectangle"), (CaptureMode.Window, "Window"), (CaptureMode.Fullscreen, "Full screen"), (CaptureMode.Freeform, "Freeform") })
            list.Children.Add(MenuRow(ModeIconFor(m), label, Settings.Current.LastSnipMode == m, () =>
            {
                Settings.Current.LastSnipMode = m;
                Settings.Current.Save();
                RefreshMode();
            }));
        ShowPopup(BtnMode, list);
    }

    void RefreshDelay()
    {
        int d = Settings.Current.SnipDelay;
        DelayIcon.Data = (Geometry)FindResource(d == 0 ? "IcoNoDelay" : "IcoDelay");
        BtnDelay.ToolTip = d == 0 ? "No delay" : $"Snip in {d} seconds";
    }

    void Delay_Click(object sender, RoutedEventArgs e)
    {
        var list = new StackPanel();
        foreach (var (d, label) in new[] { (0, "No delay"), (3, "3 seconds"), (5, "5 seconds"), (10, "10 seconds") })
            list.Children.Add(MenuRow(PathIcon(d == 0 ? "IcoNoDelay" : "IcoDelay"), label, Settings.Current.SnipDelay == d, () =>
            {
                Settings.Current.SnipDelay = d;
                Settings.Current.Save();
                RefreshDelay();
            }));
        ShowPopup(BtnDelay, list);
    }

    void New_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).NewFromEditor(this, videoMode);

    // =================================================================== tools

    void Tool_Click(object sender, RoutedEventArgs e)
    {
        var t = sender == ToolPen ? InkTool.Pen : sender == ToolHighlighter ? InkTool.Highlighter
            : sender == ToolEraser ? InkTool.Eraser : InkTool.Shapes;
        var btn = (FrameworkElement)sender;
        bool wasActive = t == tool;
        SetTool(t);
        // first click selects the tool, a second click on the active tool opens its options (like Snipping Tool);
        // shapes always opens its options so a shape can be picked.
        if (!wasActive && t != InkTool.Shapes) return;
        switch (t)
        {
            case InkTool.Pen or InkTool.Highlighter: ShowInkOptions(t, btn); break;
            case InkTool.Eraser: ShowEraserOptions(btn); break;
            default: ShowShapeOptions(btn); break;
        }
    }

    void SetTool(InkTool t)
    {
        tool = t;
        ToolPen.IsChecked = t == InkTool.Pen;
        ToolHighlighter.IsChecked = t == InkTool.Highlighter;
        ToolEraser.IsChecked = t == InkTool.Eraser;
        ToolShapes.IsChecked = t == InkTool.Shapes;
        ApplyInk();
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
                Ink.DefaultDrawingAttributes = new DrawingAttributes { Color = penColor, Width = s.PenSize, Height = s.PenSize, FitToCurve = true, IgnorePressure = true };
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
        }
    }

    FrameworkElement Swatch(string hex, bool selected, Action onPick)
    {
        var swatch = new Grid { Width = 40, Height = 40, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = hex };
        swatch.Children.Add(new Ellipse { Width = 36, Height = 36, Stroke = selected ? Brushes.White : Brushes.Transparent, StrokeThickness = 2 });
        swatch.Children.Add(new Ellipse
        {
            Width = selected ? 26 : 30, Height = selected ? 26 : 30, Fill = new SolidColorBrush(ParseColor(hex)),
            Stroke = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1
        });
        swatch.MouseLeftButtonUp += (_, ev) => { onPick(); ev.Handled = true; };
        return swatch;
    }

    static TextBlock Heading(string text, double top = 0) => new()
    {
        Text = text, Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 8)
    };

    Slider SizeSlider(double max, double value, Action<double> changed)
    {
        var sl = new Slider { Minimum = 1, Maximum = max, Value = value, IsMoveToPointEnabled = true, Margin = new Thickness(0, 2, 0, 0) };
        sl.ValueChanged += (_, e) => changed(Math.Round(e.NewValue));
        return sl;
    }

    void ShowInkOptions(InkTool t, FrameworkElement anchor)
    {
        var s = Settings.Current;
        bool pen = t == InkTool.Pen;
        var panel = new StackPanel { Width = 252, Margin = new Thickness(14, 10, 14, 14) };
        panel.Children.Add(Heading("Colours"));
        var grid = new UniformGrid { Columns = 6 };
        var preview = new WPath
        {
            Height = 40, Stretch = Stretch.None, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Data = Geometry.Parse("M14,28 C64,4 110,10 136,20 C168,32 204,36 238,10")
        };
        void Refresh()
        {
            var c = ParseColor(pen ? s.PenColor : s.HighlighterColor);
            if (!pen) c.A = 0x90;
            preview.Stroke = new SolidColorBrush(c);
            preview.StrokeThickness = pen ? s.PenSize : s.HighlighterSize;
            grid.Children.Clear();
            foreach (var hex in Palette)
                grid.Children.Add(Swatch(hex, string.Equals(hex, pen ? s.PenColor : s.HighlighterColor, StringComparison.OrdinalIgnoreCase), () =>
                {
                    if (pen) s.PenColor = hex; else s.HighlighterColor = hex;
                    s.Save(); ApplyInk(); Refresh();
                }));
        }
        Refresh();
        panel.Children.Add(grid);
        panel.Children.Add(Heading("Size", 14));
        panel.Children.Add(preview);
        panel.Children.Add(SizeSlider(pen ? 24 : 40, pen ? s.PenSize : s.HighlighterSize, v =>
        {
            if (pen) s.PenSize = v; else s.HighlighterSize = v;
            s.Save(); ApplyInk(); Refresh();
        }));
        ShowPopup(anchor, panel);
    }

    void ShowEraserOptions(FrameworkElement anchor)
    {
        var list = new StackPanel();
        list.Children.Add(MenuRow(Glyph(""), "Erase all mark-ups", false, EraseAll));
        ShowPopup(anchor, list);
    }

    void ShowShapeOptions(FrameworkElement anchor)
    {
        var s = Settings.Current;
        var panel = new StackPanel { Width = 252, Margin = new Thickness(14, 10, 14, 14) };
        var kinds = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };
        foreach (var (k, data, tip) in new[]
                 {
                     (ShapeKind.Rectangle, "M3.5,4.5 H16.5 V15.5 H3.5 Z", "Rectangle"), (ShapeKind.Ellipse, "M10,3.5 A6.5,6.5 0 1 1 9.99,3.5 Z", "Ellipse"),
                     (ShapeKind.Line, "M4,4 L16,16", "Line"), (ShapeKind.Arrow, "M16,16 L4.5,4.5 M4.5,11 V4.5 H11", "Arrow"),
                 })
        {
            var tb = new ToggleButton
            {
                Style = (Style)FindResource("Pick"), ToolTip = tip, IsChecked = shapeKind == k,
                Content = new WPath { Data = Geometry.Parse(data), Stroke = Brushes.White, StrokeThickness = 1.6, Width = 20, Height = 20, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round }
            };
            tb.Click += (_, _) =>
            {
                shapeKind = k;
                foreach (ToggleButton o in kinds.Children) o.IsChecked = o == tb;
                SetTool(InkTool.Shapes);
            };
            kinds.Children.Add(tb);
        }
        panel.Children.Add(kinds);

        panel.Children.Add(Heading("Outline", 6));
        var outline = new UniformGrid { Columns = 6 };
        panel.Children.Add(outline);
        panel.Children.Add(SizeSlider(24, s.ShapeSize, v => { s.ShapeSize = v; s.Save(); }));

        panel.Children.Add(Heading("Fill", 14));
        var fill = new UniformGrid { Columns = 6 };
        void Refresh()
        {
            outline.Children.Clear();
            foreach (var hex in Palette)
                outline.Children.Add(Swatch(hex, string.Equals(hex, s.ShapeOutline, StringComparison.OrdinalIgnoreCase), () => { s.ShapeOutline = hex; s.Save(); Refresh(); }));
            fill.Children.Clear();
            var none = new Grid { Width = 40, Height = 40, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = "No fill" };
            none.Children.Add(new Ellipse { Width = 36, Height = 36, Stroke = string.IsNullOrEmpty(s.ShapeFill) ? Brushes.White : Brushes.Transparent, StrokeThickness = 2 });
            none.Children.Add(new Ellipse { Width = 28, Height = 28, Stroke = Brushes.Gray, StrokeThickness = 1.5 });
            none.Children.Add(new System.Windows.Shapes.Line { X1 = 12, Y1 = 28, X2 = 28, Y2 = 12, Stroke = Brushes.Gray, StrokeThickness = 1.5 });
            none.MouseLeftButtonUp += (_, ev) => { s.ShapeFill = ""; s.Save(); Refresh(); ev.Handled = true; };
            fill.Children.Add(none);
            foreach (var hex in Palette.Take(Palette.Length - 1))
                fill.Children.Add(Swatch(hex, string.Equals(hex, s.ShapeFill, StringComparison.OrdinalIgnoreCase), () => { s.ShapeFill = hex; s.Save(); Refresh(); }));
        }
        Refresh();
        panel.Children.Add(fill);
        ShowPopup(anchor, panel);
    }

    // ---- shapes and element erasing on the ink layer

    WPath? shapePath;
    Point shapeStart;

    void Ink_Down(object sender, MouseButtonEventArgs e)
    {
        if (cropping) return;
        if (tool == InkTool.Eraser)
        {
            if (e.OriginalSource is UIElement hit && hit != Ink && Ink.Children.Contains(hit))
            {
                Ink.Children.Remove(hit);
                Push(new Edit(new StrokeCollection(), new StrokeCollection(), NoEls, new[] { hit }));
                e.Handled = true;
            }
            return;
        }
        if (tool != InkTool.Shapes) return;
        var s = Settings.Current;
        var pos = e.GetPosition(Ink);
        var outline = new SolidColorBrush(ParseColor(s.ShapeOutline));
        Brush? fill = shapeKind switch
        {
            ShapeKind.Arrow => outline,
            ShapeKind.Line => null,
            _ => string.IsNullOrEmpty(s.ShapeFill) ? null : new SolidColorBrush(ParseColor(s.ShapeFill)),
        };
        shapeStart = pos;
        shapePath = new WPath
        {
            Stroke = outline, StrokeThickness = s.ShapeSize, Fill = fill, StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Data = ShapeGeometry(pos, pos)
        };
        Ink.Children.Add(shapePath);
        Ink.CaptureMouse();
        e.Handled = true;
    }

    void Ink_Move(object sender, MouseEventArgs e)
    {
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

    void Ink_Up(object sender, MouseButtonEventArgs e)
    {
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

    void EraseAll()
    {
        if (Ink.Strokes.Count == 0 && Ink.Children.Count == 0) return;
        var strokes = new StrokeCollection(Ink.Strokes);
        var els = Ink.Children.Cast<UIElement>().ToArray();
        applyingHistory = true;
        Ink.Strokes.Clear();
        Ink.Children.Clear();
        applyingHistory = false;
        Push(new Edit(new StrokeCollection(), strokes, NoEls, els));
    }

    // =================================================================== undo / redo

    sealed record Edit(StrokeCollection AddedStrokes, StrokeCollection RemovedStrokes, UIElement[] AddedEls, UIElement[] RemovedEls,
        BitmapSource? ImgBefore = null, BitmapSource? ImgAfter = null);

    static readonly UIElement[] NoEls = Array.Empty<UIElement>();
    readonly Stack<Edit> undo = new(), redo = new();
    bool applyingHistory;

    void Push(Edit e)
    {
        undo.Push(e);
        redo.Clear();
        UpdateHistoryButtons();
        Changed();
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
        foreach (var el in remE) Ink.Children.Remove(el);
        var img = forward ? e.ImgAfter : e.ImgBefore;
        if (img != null)
        {
            current = img;
            Img.Source = img;
            SizeBoard();
        }
        if (addS.Count > 0) Ink.Strokes.Add(addS);
        foreach (var el in addE) Ink.Children.Add(el);
        applyingHistory = false;
    }

    void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (cropping || undo.Count == 0) return;
        var edit = undo.Pop();
        Apply(edit, false);
        redo.Push(edit);
        UpdateHistoryButtons();
        Changed();
    }

    void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (cropping || redo.Count == 0) return;
        var edit = redo.Pop();
        Apply(edit, true);
        undo.Push(edit);
        UpdateHistoryButtons();
        Changed();
    }

    // =================================================================== crop

    [Flags] enum Edge { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

    bool cropping;
    Rect crop;
    Rect dragStartRect;
    Point dragStartPt;
    Edge dragEdges;
    bool dragMove;
    readonly List<(FrameworkElement Host, Edge Edges)> cropHandles = new();

    void Crop_Click(object sender, RoutedEventArgs e)
    {
        if (cropping) { EndCrop(); return; }
        if (current == null) return;
        ClosePopup();
        cropping = true;
        crop = new Rect(0, 0, Paper.ActualWidth, Paper.ActualHeight);
        BuildCropHandles();
        CropLayer.Width = Paper.ActualWidth;
        CropLayer.Height = Paper.ActualHeight;
        CropLayer.Visibility = Visibility.Visible;
        Ink.IsHitTestVisible = false;
        ToolsPanel.Visibility = Visibility.Collapsed;
        CropPanel.Visibility = Visibility.Visible;
        ActionsPanel.IsEnabled = false;
        BtnNew.IsEnabled = false;
        UpdateCropVisuals();
        Flash("Drag the corners or edges to crop. Enter to apply, Esc to cancel.");
    }

    void EndCrop()
    {
        cropping = false;
        BtnCrop.IsChecked = false;
        CropLayer.Visibility = Visibility.Collapsed;
        Ink.IsHitTestVisible = true;
        ToolsPanel.Visibility = Visibility.Visible;
        CropPanel.Visibility = Visibility.Collapsed;
        ActionsPanel.IsEnabled = true;
        BtnNew.IsEnabled = true;
    }

    void CropCancel_Click(object sender, RoutedEventArgs e) => EndCrop();

    void CropApply_Click(object sender, RoutedEventArgs e)
    {
        var img = current!;
        double kx = img.PixelWidth / Paper.ActualWidth, ky = img.PixelHeight / Paper.ActualHeight;
        int x = Math.Clamp((int)Math.Round(crop.X * kx), 0, img.PixelWidth - 1);
        int y = Math.Clamp((int)Math.Round(crop.Y * ky), 0, img.PixelHeight - 1);
        int w = Math.Clamp((int)Math.Round(crop.Width * kx), 1, img.PixelWidth - x);
        int h = Math.Clamp((int)Math.Round(crop.Height * ky), 1, img.PixelHeight - y);
        EndCrop();
        if (x == 0 && y == 0 && w == img.PixelWidth && h == img.PixelHeight) return;

        var flat = Flatten();
        var cropped = new CroppedBitmap(flat, new Int32Rect(x, y, w, h));
        var bmp = new WriteableBitmap(cropped); // detach from the source so the old snip can be released
        bmp.Freeze();

        var strokes = new StrokeCollection(Ink.Strokes);
        var els = Ink.Children.Cast<UIElement>().ToArray();
        var edit = new Edit(new StrokeCollection(), strokes, NoEls, els, img, bmp);
        Apply(edit, true);
        Push(edit);
        Flash($"Cropped to {w} × {h} px");
    }

    double ViewScale()
    {
        if (Paper.ActualWidth <= 0) return 1;
        var r = Paper.TransformToAncestor(Stage).TransformBounds(new Rect(0, 0, Paper.ActualWidth, Paper.ActualHeight));
        return Math.Max(0.05, r.Width / Paper.ActualWidth);
    }

    void BuildCropHandles()
    {
        if (cropHandles.Count > 0) return;
        CropMove.MouseLeftButtonDown += (_, e) => BeginCropDrag(e, Edge.None, true, CropMove);
        CropMove.MouseMove += (_, e) => MoveCropDrag(e);
        CropMove.MouseLeftButtonUp += (_, e) => EndCropDrag(e, CropMove);
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
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new ScaleTransform(edges.HasFlag(Edge.Right) ? -1 : 1, edges.HasFlag(Edge.Bottom) ? -1 : 1)
                }
                : new Border
                {
                    Width = edges is Edge.Top or Edge.Bottom ? 34 : 5, Height = edges is Edge.Top or Edge.Bottom ? 5 : 34,
                    CornerRadius = new CornerRadius(2.5), Background = Brushes.White
                };
            visual.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 0.6 };
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
            host.MouseLeftButtonDown += (_, e) => BeginCropDrag(e, e2, false, host);
            host.MouseMove += (_, e) => MoveCropDrag(e);
            host.MouseLeftButtonUp += (_, e) => EndCropDrag(e, host);
            CropLayer.Children.Add(host);
            cropHandles.Add((host, edges));
        }
    }

    void BeginCropDrag(MouseButtonEventArgs e, Edge edges, bool move, UIElement capture)
    {
        dragEdges = edges;
        dragMove = move;
        dragStartRect = crop;
        dragStartPt = e.GetPosition(CropLayer);
        capture.CaptureMouse();
        e.Handled = true;
    }

    void MoveCropDrag(MouseEventArgs e)
    {
        if (Mouse.Captured == null || !cropping) return;
        var p = e.GetPosition(CropLayer);
        double W = CropLayer.Width, H = CropLayer.Height, dx = p.X - dragStartPt.X, dy = p.Y - dragStartPt.Y;
        if (dragMove)
        {
            double nx = Math.Clamp(dragStartRect.X + dx, 0, W - dragStartRect.Width);
            double ny = Math.Clamp(dragStartRect.Y + dy, 0, H - dragStartRect.Height);
            crop = new Rect(nx, ny, dragStartRect.Width, dragStartRect.Height);
        }
        else
        {
            double l = dragStartRect.Left, t = dragStartRect.Top, r = dragStartRect.Right, b = dragStartRect.Bottom;
            double min = 8 / ViewScale();
            if (dragEdges.HasFlag(Edge.Left)) l = Math.Clamp(l + dx, 0, r - min);
            if (dragEdges.HasFlag(Edge.Right)) r = Math.Clamp(r + dx, l + min, W);
            if (dragEdges.HasFlag(Edge.Top)) t = Math.Clamp(t + dy, 0, b - min);
            if (dragEdges.HasFlag(Edge.Bottom)) b = Math.Clamp(b + dy, t + min, H);
            crop = new Rect(new Point(l, t), new Point(r, b));
        }
        UpdateCropVisuals();
    }

    void EndCropDrag(MouseButtonEventArgs e, UIElement capture)
    {
        capture.ReleaseMouseCapture();
        e.Handled = true;
    }

    void UpdateCropVisuals()
    {
        double k = 1 / ViewScale();
        var full = new RectangleGeometry(new Rect(0, 0, CropLayer.Width, CropLayer.Height));
        var inner = new RectangleGeometry(crop);
        CropDim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, inner);
        CropOutline.Data = inner;
        CropOutline.StrokeThickness = 1.2 * k;
        Canvas.SetLeft(CropMove, crop.X); Canvas.SetTop(CropMove, crop.Y);
        CropMove.Width = crop.Width; CropMove.Height = crop.Height;
        foreach (var (host, edges) in cropHandles)
        {
            host.LayoutTransform = new ScaleTransform(k, k);
            double size = 30 * k;
            double x = edges.HasFlag(Edge.Left) ? crop.Left : edges.HasFlag(Edge.Right) ? crop.Right : crop.Left + crop.Width / 2;
            double y = edges.HasFlag(Edge.Top) ? crop.Top : edges.HasFlag(Edge.Bottom) ? crop.Bottom : crop.Top + crop.Height / 2;
            // the corner of an L bracket sits 10px inside its 30px box
            double ox = edges.HasFlag(Edge.Left) && edges != Edge.Left ? 10 * k : edges.HasFlag(Edge.Right) && edges != Edge.Right ? size - 10 * k : size / 2;
            double oy = edges.HasFlag(Edge.Top) && edges != Edge.Top ? 10 * k : edges.HasFlag(Edge.Bottom) && edges != Edge.Bottom ? size - 10 * k : size / 2;
            if (edges == Edge.Left) ox = size / 2 - 2 * k;
            if (edges == Edge.Right) ox = size / 2 + 2 * k;
            if (edges == Edge.Top) oy = size / 2 - 2 * k;
            if (edges == Edge.Bottom) oy = size / 2 + 2 * k;
            Canvas.SetLeft(host, x - ox);
            Canvas.SetTop(host, y - oy);
        }
    }

    // =================================================================== right: actions

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        ClipboardUtil.SetImage(Flatten());
        Flash("Copied to clipboard");
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG (*.png)|*.png|JPEG (*.jpg)|*.jpg|BMP (*.bmp)|*.bmp",
            FileName = savedPath != null ? Path.GetFileName(savedPath) : $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss}.png",
            InitialDirectory = savedPath != null ? Path.GetDirectoryName(savedPath) : Settings.Current.ScreenshotFolder
        };
        Directory.CreateDirectory(dlg.InitialDirectory!);
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            ImageUtil.Save(Flatten(), dlg.FileName);
            savedPath = dlg.FileName;
            dirty = false;
            Flash("Saved to " + dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "screenX", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    async void Share_Click(object sender, RoutedEventArgs e)
    {
        try { await ShareHelper.ShareAsync(Flatten()); }
        catch (Exception ex) { Flash(ex.Message); }
    }

    // ---- settings-driven behaviour: auto copy, ask to save

    bool dirty;
    DispatcherTimer? autoCopyTimer;

    /// <summary>Called after every edit: remember there are unsaved changes and (optionally) refresh the clipboard copy.</summary>
    void Changed()
    {
        dirty = true;
        if (!Settings.Current.AutoCopyChanges || current == null) return;
        autoCopyTimer ??= NewAutoCopyTimer();
        autoCopyTimer.Stop();
        autoCopyTimer.Start();
    }

    DispatcherTimer NewAutoCopyTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (current != null && IsLoaded && !cropping) try { ClipboardUtil.SetImage(Flatten()); } catch { }
        };
        return t;
    }

    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!dirty || current == null || !Settings.Current.AskToSaveEdited) return;
        var r = MessageBox.Show(this, "Do you want to save your changes to this snip?", "screenX", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
        if (r == MessageBoxResult.Yes)
        {
            Save_Click(this, new RoutedEventArgs());
            if (dirty) e.Cancel = true; // save dialog was cancelled
        }
    }

    void More_Click(object sender, RoutedEventArgs e)
    {
        var list = new StackPanel();
        list.Children.Add(MenuRow(Glyph(""), "Settings", false, () => SettingsWindow.Open(this)));
        if (current != null) list.Children.Add(MenuRow(Glyph(""), "Search with Google Lens", false, async () =>
        {
            try { await GoogleLens.SearchAsync(Flatten()); } catch (Exception ex) { Flash(ex.Message); }
        }));
        list.Children.Add(MenuRow(Glyph(""), savedPath != null ? "Open file location" : "Open screenshots folder", false, () =>
        {
            if (savedPath != null && File.Exists(savedPath)) WinUtil.RevealFile(savedPath);
            else
            {
                Directory.CreateDirectory(Settings.Current.ScreenshotFolder);
                WinUtil.OpenPath(Settings.Current.ScreenshotFolder);
            }
        }));
        list.Children.Add(MenuRow(Glyph(""), "Open recordings folder", false, () =>
        {
            Directory.CreateDirectory(Settings.Current.VideoFolder);
            WinUtil.OpenPath(Settings.Current.VideoFolder);
        }));
        ShowPopup(BtnMore, list, alignRight: true);
    }

    async void Ocr_Click(object sender, RoutedEventArgs e)
    {
        if (current == null) return;
        BtnOcr.IsEnabled = false;
        Flash("Extracting text…");
        try
        {
            string text = await OcrService.RecognizeAsync(current);
            if (string.IsNullOrWhiteSpace(text)) { Flash("No text detected"); return; }
            ClipboardUtil.SetText(text);
            Flash("Text copied to clipboard");
            new TextResultWindow(text) { Owner = this }.Show();
        }
        catch (Exception ex) { Flash(ex.Message); }
        finally { BtnOcr.IsEnabled = true; }
    }
}

public sealed class RelayCommand : ICommand
{
    readonly Action run;
    public RelayCommand(Action run) => this.run = run;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => run();
}
