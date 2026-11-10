using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using global::Windows.ApplicationModel.DataTransfer;
using global::Windows.Storage;
using global::Windows.Storage.Streams;

namespace ScreenX;

/// <summary>Opens the Windows share sheet for an image (same sheet Snipping Tool uses).</summary>
public static class ShareHelper
{
    public static async Task ShareAsync(BitmapSource img)
    {
        string dir = Path.Combine(Path.GetTempPath(), "screenX");
        string path = Path.Combine(dir, $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss}.png");
        ImageUtil.Save(img, path);
        var file = await StorageFile.GetFileFromPathAsync(path);

        // The share sheet needs an owner window; use a tiny invisible one in the middle of the work area.
        var wa = SystemParameters.WorkArea;
        var host = new Window
        {
            Width = 1, Height = 1, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, Topmost = true, ResizeMode = ResizeMode.NoResize,
            Left = wa.Left + wa.Width / 2, Top = wa.Top + wa.Height / 2
        };
        host.Show();
        var hwnd = WinUtil.Hwnd(host);

        var dtm = DataTransferManagerInterop.GetForWindow(hwnd);
        dtm.DataRequested += (_, e) =>
        {
            var data = e.Request.Data;
            data.Properties.Title = "screenX";
            data.Properties.Description = Path.GetFileName(path);
            data.SetStorageItems(new IStorageItem[] { file });
            data.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
        };
        DataTransferManagerInterop.ShowShareUIForWindow(hwnd);

        var t = new DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
        t.Tick += (_, _) => { t.Stop(); host.Close(); };
        t.Start();
    }
}
