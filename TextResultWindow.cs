using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ScreenX;

/// <summary>Shows extracted text so it can be reviewed / edited and copied again.</summary>
public sealed class TextResultWindow : Window
{
    public TextResultWindow(string text)
    {
        Title = "screenX — Teks hasil ekstraksi";
        Width = 560; Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
        FontFamily = new FontFamily("Segoe UI");
        SourceInitialized += (_, _) => WinUtil.DarkTitleBar(this);

        var box = new TextBox
        {
            Text = text, AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 14, Padding = new Thickness(8),
            Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)), Foreground = Brushes.White,
            BorderThickness = new Thickness(0), CaretBrush = Brushes.White
        };

        var copy = new Button { Content = "Salin semua", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0), Foreground = Brushes.White, Template = Toast.PillButtonTemplate(), Cursor = System.Windows.Input.Cursors.Hand };
        var close = new Button { Content = "Tutup", Padding = new Thickness(14, 6, 14, 6), Foreground = Brushes.White, Template = Toast.PillButtonTemplate(), Cursor = System.Windows.Input.Cursors.Hand };
        var status = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0)), VerticalAlignment = VerticalAlignment.Center };
        copy.Click += (_, _) => { ClipboardUtil.SetText(box.Text); status.Text = "Disalin ✓"; };
        close.Click += (_, _) => Close();

        var buttons = new DockPanel { Margin = new Thickness(12) };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(copy);
        right.Children.Add(close);
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);
        buttons.Children.Add(status);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new Border { Margin = new Thickness(12, 12, 12, 0), CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = box });
        Content = root;
    }
}
