using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ScreenX;

public sealed record UpdateOutcome(
    bool Checked,            // false = skipped or offline (see Message)
    bool UpdateAvailable,
    string? Latest,
    string? Notes,
    string? InstallerPath,   // verified, ready to run; null when not downloadable / not verifiable
    string Message);

/// <summary>
/// Update check against POST /api/update (docs/UPDATE-API.md). Runs shortly after start and then every ~6 hours.
/// The signed asset URL lives only ~2 minutes, so a found update is downloaded at once; the installer is run only
/// after the SHA-256 matches and the user agreed. Offline or server errors are silent: the app keeps running.
/// </summary>
public static class Updater
{
    static readonly HttpClient Download = new() { Timeout = TimeSpan.FromMinutes(10) };
    static readonly SemaphoreSlim Busy = new(1, 1);
    static DispatcherTimer? timer;
    static string? promptedVersion;

    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    /// <summary>Start the automatic checks (call once from App startup, on the UI thread).</summary>
    public static void Start()
    {
        timer = new DispatcherTimer { Interval = Interval };
        timer.Tick += async (_, _) => await RunAutoAsync();
        timer.Start();
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        first.Tick += async (_, _) => { first.Stop(); await RunAutoAsync(); };
        first.Start();
    }

    static async Task RunAutoAsync()
    {
        try
        {
            var o = await CheckAsync();
            if (o is { UpdateAvailable: true } && o.Latest != promptedVersion)
            {
                promptedVersion = o.Latest;
                ShowPrompt(o);
            }
        }
        catch { }
    }

    /// <summary>One check. Safe to call from any thread; concurrent calls are serialised.</summary>
    public static async Task<UpdateOutcome> CheckAsync()
    {
        await Busy.WaitAsync();
        try
        {
            var key = Licence.Key;
            if (key == null) return new(false, false, null, null, null, "Enter your licence in Settings to get updates.");
            if (Licence.State is LicenceState.Revoked or LicenceState.Invalid)
                return new(false, false, null, null, null, Licence.StatusText + ".");

            var r = await Request(key);
            if (r is { Status: 403, Code: "not_activated" })
            {
                // e.g. the slot was freed from the website or another PC: try to claim it again, then ask once more
                var a = await Licence.ActivateAsync(key);
                if (!a.Ok) return new(false, false, null, null, null, a.Error ?? "This PC is not activated.");
                r = await Request(key);
            }
            Licence.ApplyServerVerdict(r);
            if (r.Offline) return new(false, false, null, null, null, r.Error ?? "Offline.");
            if (!r.Ok) return new(false, false, null, null, null, r.Error ?? $"Update check failed ({r.Status}).");

            var b = r.Body;
            string? latest = b.TryGetProperty("latest", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
            bool avail = b.TryGetProperty("updateAvailable", out var u) && u.ValueKind == JsonValueKind.True;
            if (!avail) return new(true, false, latest, null, null, $"screenX {Installer.Version} is up to date.");

            string? notes = b.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            if (!b.TryGetProperty("asset", out var asset) || asset.ValueKind != JsonValueKind.Object)
                return new(true, true, latest, notes, null, "Update " + latest + " has no installer for Windows yet.");

            string? url = Str(asset, "url"), name = Str(asset, "name"), sha = Str(asset, "sha256");

            // Only ever run a file called screenX-Setup-*.exe, whatever the server says.
            string file = Path.GetFileName(name ?? "");
            if (url == null || !Regex.IsMatch(file, @"^screenX-Setup-[\w.\-]+\.exe$", RegexOptions.IgnoreCase))
                return new(true, true, latest, notes, null, "The server sent an installer name this version does not trust.");
            if (string.IsNullOrWhiteSpace(sha))
                return new(true, true, latest, notes, null, "This release has no checksum (SHA256SUMS.txt), so screenX will not install it automatically. Download it from the website instead.");

            // download immediately: the signed URL expires in about two minutes
            string dir = Path.Combine(Path.GetTempPath(), "screenX-update");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, file);
            try
            {
                using (var resp = await Download.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    await using var src = await resp.Content.ReadAsStreamAsync();
                    await using var dst = File.Create(path);
                    await src.CopyToAsync(dst);
                }
                string actual;
                await using (var fs = File.OpenRead(path)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs));
                if (!string.Equals(actual, sha.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(path); } catch { }
                    return new(true, true, latest, notes, null, "The downloaded installer failed its checksum and was deleted. Not installing.");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                try { File.Delete(path); } catch { }
                return new(false, true, latest, notes, null, "Could not download the update: " + ex.Message);
            }
            return new(true, true, latest, notes, path, $"screenX {latest} is ready to install.");
        }
        finally { Busy.Release(); }
    }

    static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static Task<ApiResult> Request(string key) => Licence.PostAsync("/api/update", new
    {
        licence = key, deviceId = Licence.DeviceId, os = Licence.OsName, version = Installer.Version,
    });

    /// <summary>Run the verified installer and close this copy (the setup also stops running copies).</summary>
    public static void Install(string installerPath)
    {
        Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
        if (Application.Current is App app) app.Quit();
    }

    /// <summary>Release notes window with Install now / Later.</summary>
    public static void ShowPrompt(UpdateOutcome o)
    {
        var w = new Window
        {
            Title = "screenX update", Width = 520, Height = 420, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), Foreground = Brushes.White,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"), Topmost = true,
        };
        w.SourceInitialized += (_, _) => WinUtil.DarkTitleBar(w);
        var grid = new Grid { Margin = new Thickness(24, 20, 24, 20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = $"screenX {o.Latest} is available", FontSize = 20, FontWeight = FontWeights.SemiBold });
        var notes = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(o.Notes) ? "No release notes." : o.Notes, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)), Foreground = Brushes.White, BorderThickness = new Thickness(0),
            Padding = new Thickness(10), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 12, 0, 12),
        };
        Grid.SetRow(notes, 1);
        grid.Children.Add(notes);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(bar, 2);
        bar.Children.Add(new Button { Content = "Later", MinWidth = 90, Padding = new Thickness(14, 6, 14, 6), IsCancel = true });
        if (o.InstallerPath != null)
        {
            var go = new Button { Content = "Install now", MinWidth = 110, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
            go.Click += (_, _) => { w.Close(); Install(o.InstallerPath); };
            bar.Children.Add(go);
        }
        else
        {
            var why = new TextBlock
            {
                Text = o.Message, Foreground = new SolidColorBrush(Color.FromRgb(0xFC, 0xB9, 0x00)), TextWrapping = TextWrapping.Wrap,
                MaxWidth = 300, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, FontSize = 12,
            };
            Grid.SetRow(why, 2);
            grid.Children.Add(why);
        }
        grid.Children.Add(bar);
        w.Content = grid;
        w.Show();
    }
}
