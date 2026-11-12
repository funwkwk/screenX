using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace ScreenX;

/// <summary>
/// Settings page laid out like Snipping Tool's: Snipping, Screen recording, Shortcuts and About cards
/// with toggle switches. Every change is saved and applied immediately.
/// </summary>
public sealed class SettingsWindow : Window
{
    static SettingsWindow? open;
    static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    static readonly Brush CardBg = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B));
    static readonly Brush CardBorder = new SolidColorBrush(Color.FromRgb(0x1D, 0x1D, 0x1D));
    static readonly Brush Sub = new SolidColorBrush(Color.FromRgb(0xC5, 0xC5, 0xC5));
    static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF));

    static readonly string[] BorderColors =
    {
        "#000000", "#FFFFFF", "#7A7A7A", "#E81123", "#F7630C", "#FFB900",
        "#16C60C", "#00B7C3", "#0078D4", "#4F4BD9", "#B146C2", "#E3008C",
    };

    readonly StackPanel page = new() { Margin = new Thickness(36, 8, 36, 32), MaxWidth = 860 };

    /// <summary>Show the (single) settings window, in front of <paramref name="owner"/> when given.</summary>
    public static void Open(Window? owner = null)
    {
        if (open == null)
        {
            open = new SettingsWindow();
            open.Closed += (_, _) => open = null;
            if (owner != null && owner.IsVisible)
            {
                open.Owner = owner;
                open.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            open.Show();
        }
        if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
        open.Activate();
    }

    SettingsWindow()
    {
        Title = "screenX Settings";
        Width = 880; Height = 760; MinWidth = 560; MinHeight = 420;
        Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseLayoutRounding = true;
        SourceInitialized += (_, _) => WinUtil.DarkTitleBar(this);

        var s = Settings.Current;
        page.Children.Add(new TextBlock { Text = "Settings", FontSize = 28, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 6) });

        // ---- Snipping
        Section("Snipping");
        Card("", "Automatically copy changes", "Update your clipboard automatically when making edits",
            Switch(s.AutoCopyChanges, v => s.AutoCopyChanges = v));
        Card("", "Automatically save original screenshots", "Every snip is also saved to the screenshots folder",
            Switch(s.AutoSave, v => s.AutoSave = v));
        Card("", "Ask to save edited screenshots", "Ask before closing a snip that has unsaved mark-ups",
            Switch(s.AskToSaveEdited, v => s.AskToSaveEdited = v));
        Card("", "Multiple windows", "Open each new snip from the New button in its own window",
            Switch(s.MultipleWindows, v => s.MultipleWindows = v));
        Card("", "Quick markup after snipping", "Stay on screen with the markup bar after selecting a rectangle",
            Switch(s.MarkupAfterSnip, v => s.MarkupAfterSnip = v));

        var borderOptions = BorderOptions();
        borderOptions.Visibility = s.AddBorder ? Visibility.Visible : Visibility.Collapsed;
        Card("", "Add border to each screenshot", "Draw a coloured outline around every snip",
            Switch(s.AddBorder, v => { s.AddBorder = v; borderOptions.Visibility = v ? Visibility.Visible : Visibility.Collapsed; }), borderOptions);

        FolderCard("", "Screenshots are saved to", () => s.ScreenshotFolder, v => s.ScreenshotFolder = v);

        // ---- Screen recording
        Section("Screen recording");
        Card("", "Include microphone input by default when a screen recording starts", null,
            Switch(s.RecordMic, v => s.RecordMic = v));
        Card("", "Include system audio by default when a screen recording starts", null,
            Switch(s.RecordSystemAudio, v => s.RecordSystemAudio = v));
        Card("", "Show the mouse pointer in recordings", null,
            Switch(s.RecordCursor, v => s.RecordCursor = v));
        Card("", "Frame rate", "Higher is smoother but makes larger files",
            Choice(new[] { 15, 24, 30, 60 }, s.RecordFps, f => $"{f} fps", v => s.RecordFps = v));
        FolderCard("", "Screen recordings are saved to", () => s.VideoFolder, v => s.VideoFolder = v);

        // ---- Shortcuts
        Section("Shortcuts");
        Card("", "Ctrl + Shift + S", "Opens screenX from anywhere", null);
        Card("", "Use the Print Screen key to open screenX", "Turn off \"Use the Print screen key to open screen capture\" in Windows settings if the key is already taken",
            Switch(s.UsePrintScreen, v =>
            {
                s.UsePrintScreen = v;
                s.Save();
                if (Application.Current is App app && !app.ApplyHotkeys() && v)
                    MessageBox.Show(this, "The Print Screen key is used by Windows or another app.\n\nOpen Windows Settings > Accessibility > Keyboard and turn off \"Use the Print screen key to open screen capture\", then switch this on again.",
                        "screenX", MessageBoxButton.OK, MessageBoxImage.Information);
            }));
        Card("", "Start screenX when Windows starts", "Runs quietly in the notification area",
            Switch(Tray.IsAutoStart(), Tray.SetAutoStart));

        // ---- About
        Section("About this app");
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        var about = new StackPanel { Orientation = Orientation.Horizontal };
        about.Children.Add(Link("funwkwk.my.id", "https://funwkwk.my.id"));
        about.Children.Add(Link("GitHub", "https://github.com/funwkwk/screenX"));
        Card("", $"screenX {v?.Major}.{v?.Minor}.{v?.Build}", "Open-source screen capture, recording, colour picker, text extractor and markup tool. MIT licence.", about);

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = page, Focusable = false };
    }

    // =================================================================== building blocks

    void Section(string title) => page.Children.Add(new TextBlock
    {
        Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 22, 0, 8)
    });

    /// <summary>One settings row: icon, title + description on the left, a control on the right, optional extra content below.</summary>
    void Card(string glyph, string title, string? description, UIElement? control, UIElement? below = null)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontSize = 14, TextWrapping = TextWrapping.Wrap });
        if (description != null)
            text.Children.Add(new TextBlock { Text = description, FontSize = 12, Foreground = Sub, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });

        var row = new DockPanel { LastChildFill = true };
        if (control is FrameworkElement fe)
        {
            fe.VerticalAlignment = VerticalAlignment.Center;
            fe.Margin = new Thickness(16, 0, 0, 0);
            DockPanel.SetDock(fe, Dock.Right);
            row.Children.Add(fe);
        }
        var icon = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 18, Width = 22, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 16, 0) };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(text);

        var body = new StackPanel();
        body.Children.Add(row);
        if (below != null) body.Children.Add(below);
        page.Children.Add(new Border
        {
            Background = CardBg, BorderBrush = CardBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
            Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 0, 0, 3), MinHeight = 66, Child = body
        });
    }

    void FolderCard(string glyph, string title, Func<string> get, Action<string> set)
    {
        var path = new TextBlock { Text = get(), FontSize = 12, Foreground = Sub, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 330, VerticalAlignment = VerticalAlignment.Center, ToolTip = get() };
        var open = Button("Open", () => { Directory.CreateDirectory(get()); WinUtil.OpenPath(get()); });
        var change = Button("Change", () =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title, InitialDirectory = Directory.Exists(get()) ? get() : null };
            if (dlg.ShowDialog(this) != true) return;
            set(dlg.FolderName);
            Settings.Current.Save();
            path.Text = dlg.FolderName;
            path.ToolTip = dlg.FolderName;
        });
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(path);
        right.Children.Add(open);
        right.Children.Add(change);
        Card(glyph, title, null, right);
    }

    static ControlTemplate? switchTemplate, buttonTemplate;

    /// <summary>Windows 11 style toggle switch with an On / Off label.</summary>
    FrameworkElement Switch(bool value, Action<bool> changed)
    {
        switchTemplate ??= (ControlTemplate)XamlReader.Parse(
            """
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ToggleButton">
              <Grid Background="Transparent">
                <Border x:Name="Track" Width="40" Height="20" CornerRadius="10" Background="Transparent" BorderBrush="#C5C5C5" BorderThickness="1"/>
                <Ellipse x:Name="Knob" Width="12" Height="12" Fill="#C5C5C5" HorizontalAlignment="Left" Margin="4,0,0,0"/>
              </Grid>
              <ControlTemplate.Triggers>
                <Trigger Property="IsChecked" Value="True">
                  <Setter TargetName="Track" Property="Background" Value="#4CC2FF"/>
                  <Setter TargetName="Track" Property="BorderBrush" Value="#4CC2FF"/>
                  <Setter TargetName="Knob" Property="Fill" Value="#0B2532"/>
                  <Setter TargetName="Knob" Property="HorizontalAlignment" Value="Right"/>
                  <Setter TargetName="Knob" Property="Margin" Value="0,0,4,0"/>
                </Trigger>
                <Trigger Property="IsMouseOver" Value="True">
                  <Setter TargetName="Knob" Property="Width" Value="14"/>
                  <Setter TargetName="Knob" Property="Height" Value="14"/>
                </Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """);
        var label = new TextBlock { Text = value ? "On" : "Off", FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var toggle = new ToggleButton { Template = switchTemplate, IsChecked = value, Width = 40, Height = 24, Cursor = Cursors.Hand, FocusVisualStyle = null };
        toggle.Click += (_, _) =>
        {
            bool on = toggle.IsChecked == true;
            label.Text = on ? "On" : "Off";
            changed(on);
            Settings.Current.Save();
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(label);
        panel.Children.Add(toggle);
        return panel;
    }

    Button Button(string text, Action click)
    {
        buttonTemplate ??= (ControlTemplate)XamlReader.Parse(
            """
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ButtonBase">
              <Border x:Name="Bd" Background="#12FFFFFF" BorderBrush="#1FFFFFFF" BorderThickness="1" CornerRadius="4" Padding="14,6">
                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True">
                  <Setter TargetName="Bd" Property="Background" Value="#20FFFFFF"/>
                </Trigger>
                <Trigger Property="ToggleButton.IsChecked" Value="True">
                  <Setter TargetName="Bd" Property="Background" Value="#4CC2FF"/>
                  <Setter Property="Foreground" Value="#0B2532"/>
                </Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """);
        var b = new Button { Content = text, Template = buttonTemplate, Foreground = Brushes.White, FontSize = 13, Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand, FocusVisualStyle = null };
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>A row of mutually exclusive buttons (used for the frame rate).</summary>
    FrameworkElement Choice<T>(IEnumerable<T> values, T current, Func<T, string> label, Action<T> changed)
    {
        Button("", () => { }); // make sure the shared template exists
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var v in values)
        {
            var tb = new ToggleButton
            {
                Content = label(v), Template = buttonTemplate, Foreground = Brushes.White, FontSize = 13, Margin = new Thickness(6, 0, 0, 0),
                Cursor = Cursors.Hand, FocusVisualStyle = null, IsChecked = EqualityComparer<T>.Default.Equals(v, current)
            };
            tb.Click += (_, _) =>
            {
                foreach (ToggleButton o in panel.Children) o.IsChecked = o == tb;
                changed(v);
                Settings.Current.Save();
            };
            panel.Children.Add(tb);
        }
        return panel;
    }

    FrameworkElement Link(string text, string url)
    {
        var t = new TextBlock { Text = text, Foreground = Accent, FontSize = 13, Cursor = Cursors.Hand, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        t.MouseLeftButtonUp += (_, _) => WinUtil.OpenPath(url);
        return t;
    }

    /// <summary>Colour swatches + thickness slider shown under "Add border to each screenshot".</summary>
    FrameworkElement BorderOptions()
    {
        var s = Settings.Current;
        var swatches = new StackPanel { Orientation = Orientation.Horizontal };
        void Refresh()
        {
            swatches.Children.Clear();
            foreach (var hex in BorderColors)
            {
                bool selected = string.Equals(hex, s.BorderColor, StringComparison.OrdinalIgnoreCase);
                var g = new Grid { Width = 32, Height = 32, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = hex };
                g.Children.Add(new Ellipse { Width = 30, Height = 30, Stroke = selected ? Brushes.White : Brushes.Transparent, StrokeThickness = 2 });
                g.Children.Add(new Ellipse
                {
                    Width = 22, Height = 22, Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                    Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1
                });
                g.MouseLeftButtonUp += (_, _) => { s.BorderColor = hex; s.Save(); Refresh(); };
                swatches.Children.Add(g);
            }
        }
        Refresh();

        var value = new TextBlock { Text = $"{s.BorderSize:0} px", FontSize = 13, Width = 44, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        var slider = new Slider { Minimum = 1, Maximum = 20, Value = s.BorderSize, Width = 220, IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, e) => { s.BorderSize = Math.Round(e.NewValue); value.Text = $"{s.BorderSize:0} px"; s.Save(); };

        var grid = new Grid { Margin = new Thickness(40, 14, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        void Put(UIElement el, int row, int col) { Grid.SetRow(el, row); Grid.SetColumn(el, col); grid.Children.Add(el); }
        Put(new TextBlock { Text = "Colour", FontSize = 13, Foreground = Sub, VerticalAlignment = VerticalAlignment.Center }, 0, 0);
        Put(swatches, 0, 1);
        Put(new TextBlock { Text = "Thickness", FontSize = 13, Foreground = Sub, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) }, 1, 0);
        var thick = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 10, 0, 0) };
        thick.Children.Add(slider);
        thick.Children.Add(value);
        Put(thick, 1, 1);
        return grid;
    }
}
