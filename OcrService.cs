using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using global::Windows.Graphics.Imaging;
using global::Windows.Media.Ocr;

namespace ScreenX;

/// <summary>Text extraction using the built-in Windows OCR engine (same as Snipping Tool / PowerToys).</summary>
public static class OcrService
{
    public static OcrEngine? CreateEngine()
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine != null) return engine;
        var langs = OcrEngine.AvailableRecognizerLanguages;
        var en = langs.FirstOrDefault(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase)) ?? langs.FirstOrDefault();
        return en == null ? null : OcrEngine.TryCreateFromLanguage(en);
    }

    public static async Task<string> RecognizeAsync(BitmapSource src)
    {
        var engine = CreateEngine() ?? throw new InvalidOperationException(
            "No OCR language is installed. Add a language (e.g. English) in Settings > Time & language > Language.");

        // Small snips recognise much better when upscaled.
        double scale = 1;
        int minSide = Math.Min(src.PixelWidth, src.PixelHeight);
        if (minSide < 120) scale = Math.Min(4, 120.0 / Math.Max(1, minSide));
        if (src.PixelHeight < 40) scale = Math.Max(scale, 2.5);
        double maxDim = OcrEngine.MaxImageDimension;
        scale = Math.Min(scale, maxDim / Math.Max(src.PixelWidth, src.PixelHeight));

        BitmapSource img = Math.Abs(scale - 1) > 0.01
            ? new TransformedBitmap(src, new ScaleTransform(scale, scale))
            : src;

        byte[] png = ImageUtil.EncodePng(img);
        using var ms = new MemoryStream(png);
        using var ras = ms.AsRandomAccessStream();
        var decoder = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ras);
        using var sb = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(sb);

        bool cjk = engine.RecognizerLanguage.LanguageTag.StartsWith("zh") || engine.RecognizerLanguage.LanguageTag.StartsWith("ja");
        var sbText = new StringBuilder();
        foreach (var line in result.Lines)
        {
            if (sbText.Length > 0) sbText.AppendLine();
            sbText.Append(cjk ? string.Concat(line.Words.Select(w => w.Text)) : line.Text);
        }
        return sbText.ToString();
    }
}
