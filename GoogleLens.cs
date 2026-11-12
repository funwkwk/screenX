using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace ScreenX;

/// <summary>
/// Reverse image search with Google Lens, hands-free.
/// screenX copies the snip, opens Google's own "Search any image with Google Lens" dialog in the user's browser and,
/// once that dialog is the active tab, pastes the snip into it. The upload therefore happens inside the browser's
/// session, so the results page shows the image (a background upload by screenX leaves it broken).
/// The paste is only sent when the foreground window is a browser showing the Google page; otherwise the user gets
/// a notification to paste or drag the image themselves.
/// </summary>
public static class GoogleLens
{
    public const string LensDialogUrl = "https://www.google.com/?olud";

    static readonly string[] Browsers = { "brave", "chrome", "msedge", "firefox", "opera", "opera_gx", "vivaldi", "arc", "chromium" };

    public static async Task SearchAsync(BitmapSource img)
    {
        ClipboardUtil.SetImage(img);
        string file = Path.Combine(Path.GetTempPath(), "screenX", "Google Lens snip.png");
        try { ImageUtil.Save(img, file); } catch { file = ""; }

        // screenX just received the user's click, so it may hand foreground rights to the browser it launches
        Native.AllowSetForegroundWindow(-1);
        Log("---- Google Lens search: opening " + LensDialogUrl);
        WinUtil.OpenPath(LensDialogUrl);
        Toast.Notify("Searching with Google Lens…", "Opening Google Lens in your browser.");

        if (await PasteIntoLensAsync())
        {
            Toast.CloseCurrent();
            return;
        }

        if (file.Length > 0)
            Toast.Notify("Google Lens is open in your browser",
                "The snip could not be pasted automatically. Press Ctrl+V in the Google Lens window, or drag the image file into it.",
                null, null, ("Show image file", (Action)(() => WinUtil.RevealFile(file))));
        else
            Toast.Notify("Google Lens is open in your browser",
                "The snip could not be pasted automatically. Press Ctrl+V in the Google Lens window.");
    }

    /// <summary>
    /// Waits for the Lens tab to be in front, pastes the clipboard image into it and checks that Google really
    /// started the search (the tab leaves the "Google" page). The Lens dialog is opened by script a moment after
    /// the page loads, so an early paste can be ignored; in that case it is pasted again.
    /// </summary>
    static async Task<bool> PasteIntoLensAsync()
    {
        var sw = Stopwatch.StartNew();
        int attempts = 0;
        while (sw.Elapsed < TimeSpan.FromSeconds(25) && attempts < 4)
        {
            await Task.Delay(250);
            var hwnd = Native.GetForegroundWindow();
            if (!IsLensTab(hwnd))
            {
                // the browser may have opened the tab behind other windows: bring that window forward
                var lens = FindLensWindow();
                if (lens == IntPtr.Zero) continue;
                Log($"bringing Lens window forward (foreground was \"{TitleOf(hwnd)}\")");
                Native.ShowWindow(lens, 9 /* SW_RESTORE if minimised */);
                Native.SetForegroundWindow(lens);
                continue; // re-check on the next tick that it really is in front
            }

            // give the page time to open its Lens dialog, then make sure it is still the active tab
            await Task.Delay(attempts == 0 ? 1500 : 1200);
            if (Native.GetForegroundWindow() != hwnd || !IsLensTab(hwnd)) continue;
            attempts++;
            Log($"paste attempt {attempts} into \"{TitleOf(hwnd)}\"");
            SendCtrlV();

            // success = the tab navigates away from the plain "Google" page (to the Lens results)
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(6))
            {
                await Task.Delay(250);
                if (!IsLensTab(hwnd))
                {
                    Log($"search started: \"{TitleOf(hwnd)}\"");
                    return true;
                }
            }
            Log("page did not react to the paste, retrying");
        }
        Log($"gave up after {attempts} paste attempt(s), {sw.Elapsed.TotalSeconds:F1}s; foreground \"{TitleOf(Native.GetForegroundWindow())}\"");
        return false;
    }

    static string TitleOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        if (hwnd != IntPtr.Zero) Native.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "screenX", "lens.log");

    /// <summary>Small diagnostic log of the last Lens searches (window titles only, no image data).</summary>
    static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 64_000) File.Delete(LogPath);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {line}{Environment.NewLine}");
        }
        catch { }
    }

    static IntPtr FindLensWindow()
    {
        IntPtr found = IntPtr.Zero;
        Native.EnumWindows((h, _) =>
        {
            if (Native.IsWindowVisible(h) && IsLensTab(h)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static bool IsLensTab(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var sb = new StringBuilder(512);
        Native.GetWindowText(hwnd, sb, sb.Capacity);
        string title = sb.ToString();
        // the Lens dialog page is titled just "Google"; the window adds the browser (and maybe a profile):
        // "Google - Brave", "Google - Google Chrome", "Google - Personal - Microsoft Edge", "Google — Mozilla Firefox"
        string t = title.Trim();
        bool isGooglePage = t == "Google" || t.StartsWith("Google - ", StringComparison.Ordinal)
                            || t.StartsWith("Google \u2014 ", StringComparison.Ordinal);
        if (!isGooglePage) return false;

        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return Browsers.Contains(p.ProcessName.ToLowerInvariant());
        }
        catch { return false; }
    }

    static void SendCtrlV()
    {
        const ushort VK_CONTROL = 0x11, VK_V = 0x56;
        var inputs = new Native.INPUT[4];
        for (int i = 0; i < 4; i++) inputs[i].type = Native.INPUT_KEYBOARD;
        inputs[0].U.ki.wVk = VK_CONTROL;
        inputs[1].U.ki.wVk = VK_V;
        inputs[2].U.ki.wVk = VK_V; inputs[2].U.ki.dwFlags = Native.KEYEVENTF_KEYUP;
        inputs[3].U.ki.wVk = VK_CONTROL; inputs[3].U.ki.dwFlags = Native.KEYEVENTF_KEYUP;
        Native.SendInput(4, inputs, Marshal.SizeOf<Native.INPUT>());
    }
}
