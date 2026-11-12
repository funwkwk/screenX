using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenX;

public static class ImageUtil
{
    public static byte[] EncodePng(BitmapSource bmp)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public static void Save(BitmapSource bmp, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        BitmapEncoder enc = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 92 },
            ".bmp" => new BmpBitmapEncoder(),
            _ => new PngBitmapEncoder(),
        };
        BitmapSource src = bmp;
        if (enc is JpegBitmapEncoder or BmpBitmapEncoder)
            src = new FormatConvertedBitmap(bmp, PixelFormats.Bgr24, null, 0); // drop alpha
        enc.Frames.Add(BitmapFrame.Create(src));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>The snip with the border from the settings drawn along its edges (same pixel size).</summary>
    public static BitmapSource WithBorder(BitmapSource bmp)
    {
        var s = Settings.Current;
        double t = Math.Clamp(s.BorderSize, 1, Math.Max(1, Math.Min(bmp.PixelWidth, bmp.PixelHeight) / 3.0));
        Color c;
        try { c = (Color)ColorConverter.ConvertFromString(s.BorderColor); } catch { c = Colors.Red; }
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(bmp, new Rect(0, 0, bmp.PixelWidth, bmp.PixelHeight));
            dc.DrawRectangle(null, new Pen(new SolidColorBrush(c), t), new Rect(t / 2, t / 2, bmp.PixelWidth - t, bmp.PixelHeight - t));
        }
        var rtb = new RenderTargetBitmap(bmp.PixelWidth, bmp.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    public static string AutoSave(BitmapSource bmp)
    {
        var path = UniquePath(Settings.Current.ScreenshotFolder, "Screenshot", ".png");
        Save(bmp, path);
        return path;
    }

    public static string UniquePath(string folder, string prefix, string ext)
    {
        Directory.CreateDirectory(folder);
        string baseName = $"{prefix} {DateTime.Now:yyyy-MM-dd HHmmss}";
        string path = Path.Combine(folder, baseName + ext);
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{baseName} ({i}){ext}");
        return path;
    }
}

public static class ClipboardUtil
{
    static void Retry(Action a)
    {
        for (int i = 0; i < 10; i++)
        {
            try { a(); return; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(50); }
            catch (System.Runtime.InteropServices.ExternalException) { Thread.Sleep(50); }
        }
    }

    public static void SetImage(BitmapSource bmp)
    {
        var data = new DataObject();
        data.SetImage(bmp);
        data.SetData("PNG", new MemoryStream(ImageUtil.EncodePng(bmp)), false);
        Retry(() => Clipboard.SetDataObject(data, true));
    }

    public static void SetText(string text) => Retry(() => Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), true));

    public static void SetFile(string path)
    {
        var files = new StringCollection { path };
        Retry(() => Clipboard.SetFileDropList(files));
    }
}

public static class ColorUtil
{
    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    public static string Rgb(Color c) => $"rgb({c.R}, {c.G}, {c.B})";

    public static string Hsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double h = 0, s, l = (max + min) / 2, d = max - min;
        if (d == 0) s = 0;
        else
        {
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
        }
        return $"hsl({Math.Round(h)}, {Math.Round(s * 100)}%, {Math.Round(l * 100)}%)";
    }
}

public static class WinUtil
{
    public static IntPtr Hwnd(Window w) => new WindowInteropHelper(w).EnsureHandle();

    /// <summary>Keep the window at exactly the physical rect we give it, even across monitors with different DPI.</summary>
    public static void IgnoreDpiChanges(Window w)
    {
        var src = HwndSource.FromHwnd(Hwnd(w));
        src.AddHook((IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
        {
            if (msg == Native.WM_DPICHANGED) handled = true;
            return IntPtr.Zero;
        });
    }

    public static void AddExStyle(Window w, int flags)
    {
        var h = Hwnd(w);
        long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, new IntPtr(ex | (uint)flags));
    }

    public static void ExcludeFromCapture(Window w)
    {
        // Test hook: SCREENX_SHOW_IN_CAPTURE=1 keeps screenX's own windows visible in screenshots.
        if (Environment.GetEnvironmentVariable("SCREENX_SHOW_IN_CAPTURE") == "1") return;
        Native.SetWindowDisplayAffinity(Hwnd(w), Native.WDA_EXCLUDEFROMCAPTURE);
    }

    public static void DarkTitleBar(Window w)
    {
        int on = 1;
        Native.DwmSetWindowAttribute(Hwnd(w), Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, 4);
    }

    public static double Scale(Visual v) => VisualTreeHelper.GetDpi(v).DpiScaleX;

    public static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    public static void RevealFile(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
    }
}

public static class FfmpegLocator
{
    public static string? Find()
    {
        string local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(local)) return local;
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"));
        foreach (var d in dirs)
        {
            try
            {
                var p = Path.Combine(d.Trim('"'), "ffmpeg.exe");
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }
}
