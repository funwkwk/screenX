using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenX;

/// <summary>
/// Headless smoke test: <c>screenX.exe --selftest &lt;outDir&gt;</c>.
/// Exercises capture, OCR, color math, recording and renders the main UI to PNGs.
/// </summary>
public static class SelfTest
{
    public static async Task RunAsync(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var log = new StringBuilder();
        void L(string s) { log.AppendLine(s); File.WriteAllText(Path.Combine(outDir, "selftest.log"), log.ToString()); }

        await Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var vs = ScreenCapture.VirtualScreen;
                L($"virtual screen: {vs.X},{vs.Y} {vs.Width}x{vs.Height}");
                var shot = ScreenCapture.Capture(vs);
                ImageUtil.Save(shot, Path.Combine(outDir, "capture.png"));
                L("capture: OK");
                L($"windows found: {ScreenCapture.GetWindowRects().Count}");

                var engine = OcrService.CreateEngine();
                L($"ocr language: {engine?.RecognizerLanguage.LanguageTag ?? "(none)"}");
                var textImg = RenderText("screenX Text Extractor 12345\nHello from Windows OCR");
                ImageUtil.Save(textImg, Path.Combine(outDir, "ocr-input.png"));
                L("ocr result: " + (await OcrService.RecognizeAsync(textImg)).Replace(Environment.NewLine, " | "));

                var c = Color.FromRgb(0x33, 0x99, 0xFF);
                L($"color: {ColorUtil.Hex(c)} {ColorUtil.Rgb(c)} {ColorUtil.Hsl(c)}");

                // Render overlay UI
                var ov = new OverlayWindow(shot, vs, ScreenCapture.GetWindowRects(), CaptureMode.Rectangle);
                ov.ShowPinned();
                await Task.Delay(600);
                SaveVisual((FrameworkElement)ov.Content, Path.Combine(outDir, "overlay.png"));
                double wPhoto = ov.TestToolbarWidth(CaptureMode.Rectangle);
                double wVideo = ov.TestToolbarWidth(CaptureMode.Record);
                L($"toolbar width photo={wPhoto} video={wVideo} stable={wPhoto == wVideo}");
                await Task.Delay(300);
                SaveVisual((FrameworkElement)ov.Content, Path.Combine(outDir, "overlay-video.png"));
                ov.TestToolbarWidth(CaptureMode.Rectangle);
                ov.TestOpen("shapemenu");
                await Task.Delay(300);
                SaveVisual((FrameworkElement)ov.Content, Path.Combine(outDir, "overlay-shapemenu.png"));
                ov.TestOpen("none");
                ov.TestEnterMarkup(new Int32Rect(300, 200, 900, 450));
                await Task.Delay(400);
                SaveVisual((FrameworkElement)ov.Content, Path.Combine(outDir, "markup.png"));
                ov.TestAddShapes();
                foreach (var f in new[] { "capture", "colours", "eraser", "shapes", "emoji", "fill", "outline" })
                {
                    ov.TestOpen(f);
                    await Task.Delay(300);
                    SaveVisual((FrameworkElement)ov.Content, Path.Combine(outDir, $"markup-{f}.png"));
                }
                ov.TestOpen("shapes");
                await Task.Delay(200);
                L("button heights: " + ov.TestButtonHeights());
                ov.TestOpen("none");
                ImageUtil.Save(ov.TestFlatten(), Path.Combine(outDir, "markup-flatten-before-resize.png"));
                var resized = ov.TestResize(120, 80);
                await Task.Delay(300);
                SaveVisual((FrameworkElement)ov.Content, Path.Combine(outDir, "markup-resized.png"));
                var flatResized = ov.TestFlatten();
                L($"resize: sel={resized.Width}x{resized.Height} capture={flatResized.PixelWidth}x{flatResized.PixelHeight}");
                ImageUtil.Save(flatResized, Path.Combine(outDir, "markup-flatten-resized.png"));
                ImageUtil.Save(ov.TestFlatten(), Path.Combine(outDir, "markup-flatten.png"));
                ov.Close();
                L("overlay render: OK");

                var ov2 = new OverlayWindow(shot, vs, ScreenCapture.GetWindowRects(), CaptureMode.ColorPicker);
                ov2.ShowPinned();
                await Task.Delay(600);
                SaveVisual((FrameworkElement)ov2.Content, Path.Combine(outDir, "overlay-color.png"));
                ov2.Close();

                var ed = new EditorWindow(textImg, null);
                ed.Show();
                await Task.Delay(600);
                // draw some ink to verify flattening
                var ink = (System.Windows.Controls.InkCanvas)ed.FindName("Ink");
                var sp = new System.Windows.Input.StylusPointCollection { new System.Windows.Input.StylusPoint(20, 90), new System.Windows.Input.StylusPoint(200, 100), new System.Windows.Input.StylusPoint(400, 95) };
                ink.Strokes.Add(new System.Windows.Ink.Stroke(sp, ink.DefaultDrawingAttributes.Clone()));
                await Task.Delay(300);
                SaveWindow(ed, Path.Combine(outDir, "editor.png"));
                var flat = (BitmapSource)typeof(EditorWindow).GetMethod("Flatten", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(ed, null)!;
                ImageUtil.Save(flat, Path.Combine(outDir, "editor-flatten.png"));
                ed.Close();
                L("editor render: OK");

                // Recording: 2 seconds of a 640x360 region
                var ff = FfmpegLocator.Find();
                L($"ffmpeg: {ff ?? "(not found)"}");
                if (ff != null)
                {
                    var path = Path.Combine(outDir, "record-test.mp4");
                    var region = new Int32Rect(vs.X + 100, vs.Y + 100, 640, 360);
                    var rs = new RecordingSession(ff, region, path, 30, true);
                    rs.Start();
                    await Task.Delay(2000);
                    await rs.StopAsync();
                    L($"recording: OK {new FileInfo(path).Length} bytes, elapsed {rs.Elapsed.TotalSeconds:F2}s");
                }
                L("DONE");
            }
            catch (Exception ex) { L("FAIL: " + ex); }
        }).Task.Unwrap();
    }

    static BitmapSource RenderText(string text)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 520, 110));
            var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 26, Brushes.Black, 1.0);
            dc.DrawText(ft, new Point(16, 12));
        }
        var rtb = new RenderTargetBitmap(520, 110, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    static void SaveWindow(Window w, string path)
    {
        Native.GetWindowRect(WinUtil.Hwnd(w), out var r);
        ImageUtil.Save(ScreenCapture.Capture(new Int32Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top)), path);
    }

    static void SaveVisual(FrameworkElement el, string path)
    {
        int w = (int)Math.Ceiling(el.ActualWidth), h = (int)Math.Ceiling(el.ActualHeight);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawRectangle(new VisualBrush(el), null, new Rect(0, 0, w, h));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        ImageUtil.Save(rtb, path);
    }
}
