using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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

                var ed = new EditorWindow(textImg, null) { Topmost = true };
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
                Call(ed, "Crop_Click", ed, new RoutedEventArgs());
                await Task.Delay(400);
                SaveWindow(ed, Path.Combine(outDir, "editor-crop.png"));
                Call(ed, "CropCancel_Click", ed, new RoutedEventArgs());
                Call(ed, "Tool_Click", ed.FindName("ToolShapes"), new RoutedEventArgs());
                await Task.Delay(500);
                SaveWindow(ed, Path.Combine(outDir, "editor-shapes.png"), pad: 420);
                ed.Close();
                L("editor render: OK");

                SettingsWindow.Open();
                foreach (Window w in Application.Current.Windows)
                    if (w is SettingsWindow sw)
                    {
                        sw.Topmost = true;
                        await Task.Delay(700);
                        SaveWindow(sw, Path.Combine(outDir, "settings.png"));
                        sw.Close();
                        L("settings render: OK");
                    }

                // Recording UI: region layer + bar, then a short recording with audio
                var ffm = FfmpegLocator.Find();
                if (ffm != null)
                {
                    bool micBefore = Settings.Current.RecordMic, sysBefore = Settings.Current.RecordSystemAudio;
                    Settings.Current.RecordMic = true; Settings.Current.RecordSystemAudio = true; // not saved
                    var rc = new RecordingController(new Int32Rect(vs.X + 200, vs.Y + 160, 800, 450));
                    var layer = (Window)Field(rc, "layer");
                    var bar = (Window)Field(rc, "bar");
                    rc.Show();
                    await Task.Delay(800);
                    SaveScreen((FrameworkElement)layer.Content, Path.Combine(outDir, "record-setup.png"), shot);                    SaveVisual((FrameworkElement)bar.Content, Path.Combine(outDir, "record-bar-idle.png"));
                    _ = (Task)Call(rc, "StartAsync")!;
                    await Task.Delay(5500);
                    SaveScreen((FrameworkElement)layer.Content, Path.Combine(outDir, "record-recording.png"), shot);
                    SaveVisual((FrameworkElement)bar.Content, Path.Combine(outDir, "record-bar-recording.png"));
                    string final = (string)Field(rc, "finalPath");
                    await (Task)Call(rc, "StopAsync", true)!;
                    Settings.Current.RecordMic = micBefore; Settings.Current.RecordSystemAudio = sysBefore;
                    var probe = new System.Diagnostics.ProcessStartInfo(ffm) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                    foreach (var a in new[] { "-hide_banner", "-i", final }) probe.ArgumentList.Add(a);
                    using (var p = System.Diagnostics.Process.Start(probe)!)
                    {
                        string info = await p.StandardError.ReadToEndAsync();
                        foreach (var line in info.Split('\n'))
                            if (line.Contains("Stream #") || line.Contains("Duration")) L("recorded: " + line.Trim());
                    }
                    L($"recorded file: {final} exists={File.Exists(final)}");
                    try { File.Delete(final); } catch { }
                }

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

    static object? Call(object o, string name, params object?[] args) =>
        o.GetType().GetMethod(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!.Invoke(o, args);

    static object Field(object o, string name) =>
        o.GetType().GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(o)!;

    static void SaveWindow(Window w, string path, int pad = 0)
    {
        Native.GetWindowRect(WinUtil.Hwnd(w), out var r);
        var vs = ScreenCapture.VirtualScreen;
        int b = Math.Min(r.Bottom + pad, vs.Y + vs.Height);
        ImageUtil.Save(ScreenCapture.Capture(new Int32Rect(r.Left, r.Top, r.Right - r.Left, b - r.Top)), path);
    }

    /// <summary>Real screen grab when screenX windows are allowed in captures (SCREENX_SHOW_IN_CAPTURE=1), else an offscreen render.</summary>
    static void SaveScreen(FrameworkElement el, string path, ImageSource background)
    {
        if (Environment.GetEnvironmentVariable("SCREENX_SHOW_IN_CAPTURE") == "1") ImageUtil.Save(ScreenCapture.Capture(ScreenCapture.VirtualScreen), path);
        else SaveVisual(el, path, background);
    }

    static void SaveVisual(FrameworkElement el, string path, ImageSource? background = null)
    {
        int w = (int)Math.Ceiling(el.ActualWidth), h = (int)Math.Ceiling(el.ActualHeight);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            if (background != null) dc.DrawImage(background, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(el), null, new Rect(0, 0, w, h));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        ImageUtil.Save(rtb, path);
    }
}
