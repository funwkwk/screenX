using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Vortice.WIC;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;

namespace ScreenX;

/// <summary>
/// Renders colour emoji (Segoe UI Emoji) to a bitmap with Direct2D/DirectWrite, because WPF text only
/// draws emoji in monochrome.
/// </summary>
public static class EmojiRenderer
{
    static readonly Dictionary<(string, int), BitmapSource> cache = new();

    public static BitmapSource Render(string emoji, int sizePx)
    {
        if (cache.TryGetValue((emoji, sizePx), out var hit)) return hit;

        using var wic = new IWICImagingFactory();
        using var wicBmp = wic.CreateBitmap((uint)sizePx, (uint)sizePx, Vortice.WIC.PixelFormat.Format32bppPBGRA, BitmapCreateCacheOption.CacheOnLoad);
        using var d2d = D2D1.D2D1CreateFactory<ID2D1Factory>();
        var props = new RenderTargetProperties(new D2DPixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied));
        using var rt = d2d.CreateWicBitmapRenderTarget(wicBmp, props);
        using var dw = DWrite.DWriteCreateFactory<IDWriteFactory>();
        using var fmt = dw.CreateTextFormat("Segoe UI Emoji", sizePx * 0.72f);
        fmt.TextAlignment = TextAlignment.Center;
        fmt.ParagraphAlignment = ParagraphAlignment.Center;
        using var brush = rt.CreateSolidColorBrush(new Color4(0, 0, 0, 1));

        rt.BeginDraw();
        rt.Clear(new Color4(0, 0, 0, 0));
        rt.DrawText(emoji, fmt, new Rect(0, 0, sizePx, sizePx), brush, DrawTextOptions.EnableColorFont);
        rt.EndDraw();

        int stride = sizePx * 4;
        var buf = new byte[stride * sizePx];
        wicBmp.CopyPixels((uint)stride, buf);
        var bmp = BitmapSource.Create(sizePx, sizePx, 96, 96, PixelFormats.Pbgra32, null, buf, stride);
        bmp.Freeze();
        cache[(emoji, sizePx)] = bmp;
        return bmp;
    }
}
