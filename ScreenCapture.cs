using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenX;

/// <summary>Screen grabbing in physical pixels (process is Per-Monitor-V2 DPI aware).</summary>
public static class ScreenCapture
{
    public static Int32Rect VirtualScreen => new(
        Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN),
        Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN),
        Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN),
        Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));

    public static BitmapSource Capture(Int32Rect r)
    {
        using var cap = new GdiCapturer(r.Width, r.Height);
        cap.Capture(r.X, r.Y, false);
        var bmp = BitmapSource.Create(r.Width, r.Height, 96, 96, PixelFormats.Bgr32, null,
            cap.Bits, cap.Stride * r.Height, cap.Stride);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Visible top-level windows in z-order (top first), as physical screen rects.</summary>
    public static List<Int32Rect> GetWindowRects()
    {
        var list = new List<Int32Rect>();
        uint myPid = (uint)Environment.ProcessId;
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid == myPid) return true;
            if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true;
            long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && Native.GetWindowTextLength(h) == 0) return true;
            if (Native.DwmGetWindowAttribute(h, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out Native.RECT r,
                    Marshal.SizeOf<Native.RECT>()) != 0)
                Native.GetWindowRect(h, out r);
            int w = r.Right - r.Left, hgt = r.Bottom - r.Top;
            if (w < 8 || hgt < 8) return true;
            list.Add(new Int32Rect(r.Left, r.Top, w, hgt));
            return true;
        }, IntPtr.Zero);
        return list;
    }
}

/// <summary>Reusable BitBlt target backed by a top-down 32bpp DIB.</summary>
public sealed class GdiCapturer : IDisposable
{
    readonly IntPtr screenDc, memDc, hBmp, oldBmp;
    public IntPtr Bits { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride => Width * 4;

    public GdiCapturer(int width, int height)
    {
        Width = width; Height = height;
        screenDc = Native.GetDC(IntPtr.Zero);
        memDc = Native.CreateCompatibleDC(screenDc);
        var bi = new Native.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32, biCompression = 0
        };
        hBmp = Native.CreateDIBSection(screenDc, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (hBmp == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection gagal.");
        Bits = bits;
        oldBmp = Native.SelectObject(memDc, hBmp);
    }

    public void Capture(int x, int y, bool drawCursor)
    {
        Native.BitBlt(memDc, 0, 0, Width, Height, screenDc, x, y, Native.SRCCOPY | Native.CAPTUREBLT);
        if (drawCursor) DrawCursor(x, y);
    }

    void DrawCursor(int x, int y)
    {
        var ci = new Native.CURSORINFO { cbSize = Marshal.SizeOf<Native.CURSORINFO>() };
        if (!Native.GetCursorInfo(ref ci) || (ci.flags & 1) == 0 || ci.hCursor == IntPtr.Zero) return;
        int hx = 0, hy = 0;
        if (Native.GetIconInfo(ci.hCursor, out var ii))
        {
            hx = ii.xHotspot; hy = ii.yHotspot;
            if (ii.hbmMask != IntPtr.Zero) Native.DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) Native.DeleteObject(ii.hbmColor);
        }
        Native.DrawIconEx(memDc, ci.ptScreenPos.X - x - hx, ci.ptScreenPos.Y - y - hy, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3);
    }

    public void Dispose()
    {
        Native.SelectObject(memDc, oldBmp);
        Native.DeleteObject(hBmp);
        Native.DeleteDC(memDc);
        Native.ReleaseDC(IntPtr.Zero, screenDc);
    }
}
