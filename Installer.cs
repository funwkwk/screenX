using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace ScreenX;

/// <summary>
/// Built-in installer. The published single-file screenX.exe doubles as its own setup program: when it is
/// started as "screenX-Setup*.exe" (or with --install) it shows the setup window, copies itself to
/// %LocalAppData%\Programs\screenX, creates shortcuts and registers an entry in Windows "Installed apps".
/// "screenX.exe --uninstall" removes all of that again. Everything is per-user; no administrator rights needed.
/// </summary>
public static class Installer
{
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\screenX";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "screenX");
    public static string InstalledExe => Path.Combine(InstallDir, "screenX.exe");
    static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "screenX.lnk");
    static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "screenX.lnk");

    public static string Version
    {
        get { var v = Assembly.GetExecutingAssembly().GetName().Version; return $"{v?.Major}.{v?.Minor}.{v?.Build}"; }
    }

    public static bool IsSetupRun(string[] args) =>
        args.Contains("--install") || Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").Contains("setup", StringComparison.OrdinalIgnoreCase);

    static void StopRunningCopies()
    {
        int me = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName("screenX"))
        {
            try { if (p.Id != me) { p.Kill(); p.WaitForExit(4000); } } catch { }
        }
    }

    static void CreateShortcut(string linkPath, string target)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Cannot create shortcuts on this PC.");
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic link = shell.CreateShortcut(linkPath);
        link.TargetPath = target;
        link.WorkingDirectory = Path.GetDirectoryName(target);
        link.Description = "screenX - screen capture and recording";
        link.IconLocation = target + ",0";
        link.Save();
    }

    public static void Install(bool desktopShortcut, bool startWithWindows)
    {
        string source = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find the setup file.");
        StopRunningCopies();
        Directory.CreateDirectory(InstallDir);
        if (!string.Equals(Path.GetFullPath(source), InstalledExe, StringComparison.OrdinalIgnoreCase))
            File.Copy(source, InstalledExe, true);

        // ffmpeg next to the setup file is installed too, so recording works out of the box
        string ff = Path.Combine(Path.GetDirectoryName(source)!, "ffmpeg.exe");
        if (File.Exists(ff)) try { File.Copy(ff, Path.Combine(InstallDir, "ffmpeg.exe"), true); } catch { }

        CreateShortcut(StartMenuLink, InstalledExe);
        if (desktopShortcut) CreateShortcut(DesktopLink, InstalledExe);
        else try { File.Delete(DesktopLink); } catch { }

        using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", "screenX");
            k.SetValue("DisplayVersion", Version);
            k.SetValue("Publisher", "funwkwk");
            k.SetValue("URLInfoAbout", "https://funwkwk.my.id");
            k.SetValue("DisplayIcon", InstalledExe);
            k.SetValue("InstallLocation", InstallDir);
            k.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
        }
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (startWithWindows) run.SetValue("screenX", $"\"{InstalledExe}\" --background");
            else run.DeleteValue("screenX", false);
        }
    }

    /// <summary>screenX.exe --uninstall (started by Windows "Installed apps").</summary>
    public static void Uninstall()
    {
        if (MessageBox.Show("Remove screenX from this PC?\n\nYour screenshots, recordings and settings are kept.", "Uninstall screenX",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        StopRunningCopies();
        foreach (var f in new[] { StartMenuLink, DesktopLink }) try { File.Delete(f); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        try { using var run = Registry.CurrentUser.CreateSubKey(RunKey); run.DeleteValue("screenX", false); } catch { }

        // The running exe cannot delete itself: let cmd remove the folder a moment after we exit.
        string dir = InstallDir;
        if (Directory.Exists(dir) && Path.GetFullPath(Environment.ProcessPath ?? "").StartsWith(dir, StringComparison.OrdinalIgnoreCase))
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{dir}\"") { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        else
            try { Directory.Delete(dir, true); } catch { }
        MessageBox.Show("screenX was removed.", "Uninstall screenX", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}

/// <summary>Small dark setup window: options, Install button, then Launch.</summary>
public sealed class InstallerWindow : Window
{
    public InstallerWindow()
    {
        Title = "screenX Setup";
        Width = 520; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        SourceInitialized += (_, _) => WinUtil.DarkTitleBar(this);

        bool update = File.Exists(Installer.InstalledExe);
        var sub = new SolidColorBrush(Color.FromRgb(0xC5, 0xC5, 0xC5));
        var root = new StackPanel { Margin = new Thickness(28, 22, 28, 24) };
        root.Children.Add(new TextBlock { Text = (update ? "Update screenX to " : "Install screenX ") + Installer.Version, FontSize = 22, FontWeight = FontWeights.SemiBold });
        root.Children.Add(new TextBlock
        {
            Text = "Screen capture, screen recording, colour picker, text extractor and markup.\nInstalls for your user account only; no administrator rights needed.",
            Foreground = sub, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 14)
        });
        root.Children.Add(new TextBlock { Text = "Location: " + Installer.InstallDir, Foreground = sub, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });

        CheckBox Check(string text, bool on)
        {
            var c = new CheckBox { Content = text, IsChecked = on, Foreground = Brushes.White, FontSize = 14, Margin = new Thickness(0, 5, 0, 5) };
            root.Children.Add(c);
            return c;
        }
        var desktop = Check("Create a desktop shortcut", true);
        var autostart = Check("Start screenX when Windows starts", true);
        var launch = Check("Open screenX when setup finishes", true);

        var status = new TextBlock { Foreground = sub, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
        root.Children.Add(status);

        var install = new Button { Content = update ? "Update" : "Install", MinWidth = 110, Padding = new Thickness(18, 8, 18, 8), FontSize = 14, IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, Padding = new Thickness(18, 8, 18, 8), FontSize = 14, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(install);
        root.Children.Add(buttons);
        Content = root;

        cancel.Click += (_, _) => Close();
        bool done = false;
        install.Click += async (_, _) =>
        {
            if (done)
            {
                if (launch.IsChecked == true) Process.Start(new ProcessStartInfo(Installer.InstalledExe) { UseShellExecute = true });
                Close();
                return;
            }
            install.IsEnabled = cancel.IsEnabled = desktop.IsEnabled = autostart.IsEnabled = false;
            status.Text = "Installing…";
            status.Visibility = Visibility.Visible;
            bool d = desktop.IsChecked == true, a = autostart.IsChecked == true;
            try
            {
                await Task.Run(() => Installer.Install(d, a));
                done = true;
                status.Text = "screenX is installed. Find it in the Start menu, or press Ctrl+Shift+S after opening it.\n" +
                              "Screen recording needs ffmpeg: run  winget install Gyan.FFmpeg  once if you do not have it.";
                install.Content = "Finish";
                install.IsEnabled = true;
            }
            catch (Exception ex)
            {
                status.Text = "Setup failed: " + ex.Message;
                install.IsEnabled = cancel.IsEnabled = desktop.IsEnabled = autostart.IsEnabled = true;
            }
        };
    }
}
