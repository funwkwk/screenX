using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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


/// <summary>Borderless, rounded dark setup window: logo header, option switches, Install button, then Launch.</summary>
public sealed class InstallerWindow : Window
{
    const string Ns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

    // A card with a soft shadow; the outer margin leaves room for the shadow inside the transparent window.
    static readonly string Layout = $$"""
        <Border {{Ns}} Margin="24" CornerRadius="16" Background="#17171B" BorderBrush="#2C2C33" BorderThickness="1">
          <Border.Effect><DropShadowEffect BlurRadius="28" ShadowDepth="6" Opacity="0.5" Color="#000000"/></Border.Effect>
          <Border.Resources>
            <ControlTemplate x:Key="Switch" TargetType="ToggleButton">
              <Grid Background="Transparent">
                <Border x:Name="Track" Width="40" Height="22" CornerRadius="11" Background="#2A2A31" BorderBrush="#3A3A43" BorderThickness="1"/>
                <Ellipse x:Name="Knob" Width="14" Height="14" Fill="#9A9AA5" HorizontalAlignment="Left" Margin="5,0,0,0"/>
              </Grid>
              <ControlTemplate.Triggers>
                <Trigger Property="IsChecked" Value="True">
                  <Setter TargetName="Track" Property="Background" Value="#4CC2FF"/>
                  <Setter TargetName="Track" Property="BorderBrush" Value="#4CC2FF"/>
                  <Setter TargetName="Knob" Property="Fill" Value="#0B2532"/>
                  <Setter TargetName="Knob" Property="HorizontalAlignment" Value="Right"/>
                  <Setter TargetName="Knob" Property="Margin" Value="0,0,5,0"/>
                </Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            <Style x:Key="Btn" TargetType="Button">
              <Setter Property="Foreground" Value="#E8E8EE"/>
              <Setter Property="Background" Value="#24242A"/>
              <Setter Property="BorderBrush" Value="#33333B"/>
              <Setter Property="FontSize" Value="14"/>
              <Setter Property="Cursor" Value="Hand"/>
              <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
              <Setter Property="Template">
                <Setter.Value>
                  <ControlTemplate TargetType="Button">
                    <Border x:Name="Bd" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="10" Padding="22,10">
                      <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Bd" Property="Opacity" Value="0.88"/></Trigger>
                      <Trigger Property="IsPressed" Value="True"><Setter TargetName="Bd" Property="Opacity" Value="0.72"/></Trigger>
                      <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Bd" Property="Opacity" Value="0.4"/></Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value>
              </Setter>
            </Style>
            <Style x:Key="Primary" TargetType="Button" BasedOn="{StaticResource Btn}">
              <Setter Property="Background" Value="#4CC2FF"/>
              <Setter Property="BorderBrush" Value="#4CC2FF"/>
              <Setter Property="Foreground" Value="#06202D"/>
              <Setter Property="FontWeight" Value="SemiBold"/>
            </Style>
            <Style x:Key="Close" TargetType="Button">
              <Setter Property="Cursor" Value="Hand"/>
              <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
              <Setter Property="Template">
                <Setter.Value>
                  <ControlTemplate TargetType="Button">
                    <Border x:Name="Bd" Width="32" Height="32" CornerRadius="8" Background="Transparent">
                      <TextBlock Text="&#xE8BB;" FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets" FontSize="10" Foreground="#9A9AA5" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Bd" Property="Background" Value="#2A2A31"/></Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value>
              </Setter>
            </Style>
          </Border.Resources>

          <Grid Margin="28,22,22,26">
            <Grid.RowDefinitions>
              <RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/>
              <RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/>
            </Grid.RowDefinitions>

            <StackPanel Orientation="Horizontal" Margin="0,8,0,0">
              <Border Width="52" Height="52" CornerRadius="14" Background="#202027" BorderBrush="#2E2E36" BorderThickness="1">
                <Image x:Name="Logo" Width="32" Height="32" RenderOptions.BitmapScalingMode="HighQuality"/>
              </Border>
              <StackPanel Margin="16,0,0,0" VerticalAlignment="Center">
                <StackPanel Orientation="Horizontal">
                  <TextBlock Text="screenX" FontSize="22" FontWeight="SemiBold" Foreground="#F4F4F8"/>
                  <Border CornerRadius="6" Background="#1A3A4C" Padding="7,2" Margin="10,2,0,0" VerticalAlignment="Center">
                    <TextBlock x:Name="VersionText" FontSize="11.5" FontWeight="SemiBold" Foreground="#7FD3FF"/>
                  </Border>
                </StackPanel>
                <TextBlock x:Name="Heading" FontSize="13" Foreground="#9A9AA5" Margin="0,2,0,0"/>
              </StackPanel>
            </StackPanel>
            <Button x:Name="CloseBtn" Style="{StaticResource Close}" HorizontalAlignment="Right" VerticalAlignment="Top" Margin="0,-6,-6,0"/>

            <TextBlock Grid.Row="1" Margin="0,20,6,0" TextWrapping="Wrap" FontSize="13.5" LineHeight="20" Foreground="#B9B9C3"
                       Text="Capture, record, pick colours, extract text and mark up. Installs for your account only, no admin rights needed."/>

            <Border Grid.Row="2" Margin="0,20,6,0" CornerRadius="12" Background="#1E1E24" BorderBrush="#2A2A31" BorderThickness="1">
              <StackPanel x:Name="OptionList"/>
            </Border>

            <Grid Grid.Row="3" Margin="2,14,6,0">
              <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition/></Grid.ColumnDefinitions>
              <TextBlock Text="&#xE8B7;" FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets" FontSize="12" Foreground="#6E6E79" VerticalAlignment="Center"/>
              <TextBlock Grid.Column="1" x:Name="Location" Margin="8,0,0,0" FontSize="12" Foreground="#6E6E79" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"/>
            </Grid>

            <StackPanel Grid.Row="4" x:Name="StatusPanel" Margin="0,16,6,0" Visibility="Collapsed">
              <ProgressBar x:Name="Progress" Height="4" IsIndeterminate="True" Foreground="#4CC2FF" Background="#26262D" BorderThickness="0" Margin="0,0,0,10"/>
              <TextBlock x:Name="Status" FontSize="13" LineHeight="19" TextWrapping="Wrap" Foreground="#B9B9C3"/>
            </StackPanel>

            <StackPanel Grid.Row="5" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,24,6,0">
              <Button x:Name="CancelBtn" Content="Cancel" Style="{StaticResource Btn}" MinWidth="100" IsCancel="True"/>
              <Button x:Name="InstallBtn" Style="{StaticResource Primary}" MinWidth="120" Margin="10,0,0,0" IsDefault="True"/>
            </StackPanel>
          </Grid>
        </Border>
        """;

    static readonly Brush Muted = Freeze(new SolidColorBrush(Color.FromRgb(0xB9, 0xB9, 0xC3)));
    static readonly Brush Good = Freeze(new SolidColorBrush(Color.FromRgb(0x8E, 0xE0, 0xA8)));
    static readonly Brush Bad = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x99, 0x99)));
    static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    readonly FrameworkElement root;
    T Get<T>(string name) where T : class => (T)root.FindName(name);

    public InstallerWindow()
    {
        Title = "screenX Setup";
        Width = 520; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        root = (FrameworkElement)XamlReader.Parse(Layout);
        Content = root;
        root.MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };

        bool update = File.Exists(Installer.InstalledExe);
        Get<Image>("Logo").Source = LoadLogo();
        Get<TextBlock>("VersionText").Text = "v" + Installer.Version;
        var heading = Get<TextBlock>("Heading");
        heading.Text = update ? "Update to the latest version" : "Ready to install";
        var location = Get<TextBlock>("Location");
        location.Text = Installer.InstallDir;
        location.ToolTip = Installer.InstallDir;

        var list = Get<StackPanel>("OptionList");
        var switchTemplate = (ControlTemplate)root.FindResource("Switch");
        ToggleButton Option(string title, string hint, bool on)
        {
            if (list.Children.Count > 0)
                list.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x31)), Margin = new Thickness(16, 0, 16, 0) });
            var row = new Grid { Margin = new Thickness(16, 12, 14, 12), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = title, FontSize = 14, Foreground = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xF1)) });
            text.Children.Add(new TextBlock { Text = hint, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x8B)), Margin = new Thickness(0, 2, 0, 0) });
            row.Children.Add(text);
            var t = new ToggleButton { Template = switchTemplate, IsChecked = on, Width = 40, Height = 24, FocusVisualStyle = null, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(t, 1);
            row.Children.Add(t);
            // the whole row toggles; clicks on it must not start a window drag
            row.MouseLeftButtonDown += (_, e) => e.Handled = true;
            row.MouseLeftButtonUp += (_, _) => { if (t.IsEnabled && !t.IsMouseOver) t.IsChecked = t.IsChecked != true; };
            list.Children.Add(row);
            return t;
        }
        var desktop = Option("Desktop shortcut", "Add screenX to your desktop", true);
        var autostart = Option("Start with Windows", "Ready in the tray when you sign in", true);
        var launch = Option("Open when finished", "Launch screenX right after setup", true);

        var install = Get<Button>("InstallBtn");
        var cancel = Get<Button>("CancelBtn");
        var statusPanel = Get<StackPanel>("StatusPanel");
        var progress = Get<ProgressBar>("Progress");
        var status = Get<TextBlock>("Status");
        install.Content = update ? "Update" : "Install";

        Get<Button>("CloseBtn").Click += (_, _) => Close();
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
            statusPanel.Visibility = progress.Visibility = Visibility.Visible;
            status.Foreground = Muted;
            status.Text = update ? "Updating screenX…" : "Installing screenX…";
            bool d = desktop.IsChecked == true, a = autostart.IsChecked == true;
            try
            {
                await Task.Run(() => Installer.Install(d, a));
                done = true;
                progress.Visibility = Visibility.Collapsed;
                heading.Text = "You're all set";
                status.Foreground = Good;
                status.Text = "screenX is installed. Find it in the Start menu" +
                              Settings.Current.SnipHotkeyHint(", or press {0} after opening it.", ".");
                install.Content = "Finish";
                install.IsEnabled = true;
                cancel.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                progress.Visibility = Visibility.Collapsed;
                status.Foreground = Bad;
                status.Text = "Setup failed: " + ex.Message;
                install.IsEnabled = cancel.IsEnabled = desktop.IsEnabled = autostart.IsEnabled = true;
            }
        };
    }

    /// <summary>Largest frame of the embedded app icon.</summary>
    static ImageSource? LoadLogo()
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("screenX.ico");
            if (s == null) return null;
            var dec = new IconBitmapDecoder(s, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return dec.Frames.OrderByDescending(f => f.PixelWidth).First();
        }
        catch { return null; }
    }
}
