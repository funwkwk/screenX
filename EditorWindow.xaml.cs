using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenX;

/// <summary>Markup window: pen / highlighter / eraser over the snip, then copy or save.</summary>
public partial class EditorWindow : Window
{
    readonly BitmapSource original;
    string? savedPath;
    Color penColor = Color.FromRgb(0xFF, 0x3B, 0x30);

    public EditorWindow(BitmapSource image, string? savedPath)
    {
        InitializeComponent();
        original = image;
        this.savedPath = savedPath;
        Img.Source = image;
        SourceInitialized += (_, _) => WinUtil.DarkTitleBar(this);
        Loaded += (_, _) => FitToImage();
        DpiChanged += (_, _) => SizeBoard();
        ApplyTool();
        Status.Text = $"{image.PixelWidth} × {image.PixelHeight} px" + (savedPath != null ? $"   ·   {savedPath}" : "");

        InputBindings.Add(new KeyBinding(new RelayCommand(() => Copy_Click(this, new RoutedEventArgs())), Key.C, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => Save_Click(this, new RoutedEventArgs())), Key.S, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => Undo_Click(this, new RoutedEventArgs())), Key.Z, ModifierKeys.Control));
    }

    void SizeBoard()
    {
        double s = WinUtil.Scale(this);
        Paper.Width = original.PixelWidth / s;
        Paper.Height = original.PixelHeight / s;
    }

    void FitToImage()
    {
        SizeBoard();
        var wa = SystemParameters.WorkArea;
        Width = Math.Clamp(Paper.Width + 80, MinWidth, wa.Width * 0.9);
        Height = Math.Clamp(Paper.Height + 150, MinHeight, wa.Height * 0.9);
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top + (wa.Height - Height) / 2;
        Activate();
    }

    void ApplyTool()
    {
        if (Ink == null) return;
        double size = SizeSlider?.Value ?? 3;
        if (ToolEraser.IsChecked == true)
        {
            Ink.EditingMode = InkCanvasEditingMode.EraseByStroke;
            return;
        }
        Ink.EditingMode = InkCanvasEditingMode.Ink;
        bool hl = ToolHighlighter.IsChecked == true;
        Ink.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = penColor,
            IsHighlighter = hl,
            Width = hl ? Math.Max(size * 4, 12) : size,
            Height = hl ? Math.Max(size * 4, 12) : size,
            StylusTip = hl ? StylusTip.Rectangle : StylusTip.Ellipse,
            FitToCurve = true
        };
    }

    void Tool_Changed(object sender, RoutedEventArgs e) => ApplyTool();
    void Size_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyTool();

    void Color_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Background is SolidColorBrush b)
        {
            penColor = b.Color;
            if (ToolEraser != null && ToolEraser.IsChecked == true) ToolPen.IsChecked = true;
            ApplyTool();
        }
    }

    void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (Ink.Strokes.Count > 0) Ink.Strokes.RemoveAt(Ink.Strokes.Count - 1);
    }

    void Clear_Click(object sender, RoutedEventArgs e) => Ink.Strokes.Clear();

    /// <summary>The snip with ink flattened onto it, at the original pixel size.</summary>
    BitmapSource Flatten()
    {
        if (Ink.Strokes.Count == 0) return original;
        double s = WinUtil.Scale(this);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var r = new Rect(0, 0, Paper.ActualWidth, Paper.ActualHeight);
            dc.DrawImage(original, r);
            dc.DrawRectangle(new VisualBrush(Ink) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = r }, null, r);
        }
        var rtb = new RenderTargetBitmap(original.PixelWidth, original.PixelHeight, 96 * s, 96 * s, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        ClipboardUtil.SetImage(Flatten());
        Status.Text = "Disalin ke clipboard.";
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
            Status.Text = $"Disimpan: {dlg.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "screenX", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (savedPath != null && File.Exists(savedPath)) WinUtil.RevealFile(savedPath);
        else
        {
            Directory.CreateDirectory(Settings.Current.ScreenshotFolder);
            WinUtil.OpenPath(Settings.Current.ScreenshotFolder);
        }
    }

    async void Ocr_Click(object sender, RoutedEventArgs e)
    {
        BtnOcr.IsEnabled = false;
        Status.Text = "Mengekstrak teks…";
        try
        {
            string text = await OcrService.RecognizeAsync(original);
            if (string.IsNullOrWhiteSpace(text)) { Status.Text = "Tidak ada teks yang terdeteksi."; return; }
            ClipboardUtil.SetText(text);
            Status.Text = "Teks disalin ke clipboard.";
            new TextResultWindow(text) { Owner = this }.Show();
        }
        catch (Exception ex) { Status.Text = ex.Message; }
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
