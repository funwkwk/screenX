using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenX;

/// <summary>Reverse image search: uploads the snip to Google Lens and opens the results in the browser.</summary>
public static class GoogleLens
{
    const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    public static async Task SearchAsync(BitmapSource img)
    {
        Toast.Notify("Mencari dengan Google Lens…", "Mengunggah snip ke Google.");
        try
        {
            string? url = await UploadAsync(Downscale(img, 1600));
            if (url != null)
            {
                Toast.CloseCurrent();
                WinUtil.OpenPath(url);
                return;
            }
        }
        catch { /* fall through to manual paste */ }

        // Fallback: put the image on the clipboard and open Lens so the user can paste it.
        ClipboardUtil.SetImage(img);
        WinUtil.OpenPath("https://lens.google.com/");
        Toast.Notify("Google Lens dibuka di browser", "Gagal mengunggah otomatis. Gambar sudah disalin, tempel dengan Ctrl+V di halaman Google Lens.");
    }

    /// <summary>Returns the Lens results URL, or null if Google did not hand one back.</summary>
    public static async Task<string?> UploadAsync(BitmapSource img)
    {
        byte[] png = ImageUtil.EncodePng(img);
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "encoded_image", "screenshot.png");
        form.Add(new StringContent($"{img.PixelHeight},{img.PixelWidth}"), "processed_image_dimensions");

        long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var resp = await http.PostAsync($"https://lens.google.com/v3/upload?hl=id&re=df&stcs={ms}&ep=subb", form);

        if (resp.Headers.Location is { } loc)
            return loc.IsAbsoluteUri ? loc.ToString() : new Uri(new Uri("https://lens.google.com"), loc).ToString();

        string body = await resp.Content.ReadAsStringAsync();
        var m = Regex.Match(body, @"https://lens\.google\.com/search\?[^""'<>\s]+");
        return m.Success ? WebUtility.HtmlDecode(m.Value) : null;
    }

    static BitmapSource Downscale(BitmapSource src, int maxSide)
    {
        int longest = Math.Max(src.PixelWidth, src.PixelHeight);
        if (longest <= maxSide) return src;
        double k = (double)maxSide / longest;
        var t = new TransformedBitmap(src, new ScaleTransform(k, k));
        t.Freeze();
        return t;
    }
}
