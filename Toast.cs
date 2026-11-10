using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace ScreenX;

/// <summary>Small notification card shown in the bottom-right corner (like Snipping Tool's "Snip copied" toast).</summary>
public sealed class Toast : Window
{
    static Toast? current;
    readonly DispatcherTimer timer;

    public static bool IsShowing => current != null;

    public static void CloseCurrent()
    {
        current?.Close();
        current = null;
    }

    public static void Notify(string title, string? body = null, UIElement? visual = null, Action? onClick = null,
        params (string Label, Action Action)[] buttons)
    {
        CloseCurrent();
        current = new Toast(title, body, visual, onClick, buttons);
        current.Show();
    }

    Toast(string title, string? body, UIElement? visual, Action? onClick, IEnumerable<(string Label, Action Action)> buttons)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = new FontFamily("Segoe UI");
        UseLayoutRounding = true;

        var stack = new StackPanel();

        // header
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var close = new Button
        {
            Content = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)), Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Width = 24, Height = 22, Cursor = Cursors.Hand,
            Template = FlatButtonTemplate()
        };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(new TextBlock
        {
            Text = "screenX", Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0)), FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        });
        stack.Children.Add(header);

        if (visual != null)
        {
            var holder = new Border { Margin = new Thickness(0, 0, 0, 10), Child = visual, HorizontalAlignment = HorizontalAlignment.Left };
            stack.Children.Add(holder);
        }

        stack.Children.Add(new TextBlock
        {
            Text = title, Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrEmpty(body))
            stack.Children.Add(new TextBlock
            {
                Text = body, Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0)), FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), MaxHeight = 120,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

        var btnRow = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var (label, action) in buttons)
        {
            var b = new Button
            {
                Content = label, Foreground = Brushes.White, FontSize = 12.5, Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand, Template = PillButtonTemplate()
            };
            b.Click += (_, e) => { e.Handled = true; action(); Close(); };
            btnRow.Children.Add(b);
        }
        if (btnRow.Children.Count > 0) stack.Children.Add(btnRow);

        var card = new Border
        {
            Width = 360, Padding = new Thickness(14, 10, 14, 14), CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0xF7, 0x2B, 0x2B, 0x2B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            Margin = new Thickness(16), Child = stack,
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.4 },
            Cursor = onClick != null ? Cursors.Hand : Cursors.Arrow
        };
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (e.Handled) return;
            if (onClick != null) { onClick(); Close(); }
        };
        Content = card;

        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        timer.Tick += (_, _) => Close();
        MouseEnter += (_, _) => timer.Stop();
        MouseLeave += (_, _) => timer.Start();

        Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - ActualWidth;
            Top = wa.Bottom - ActualHeight;
            var slide = new DoubleAnimation(Top + 30, Top, TimeSpan.FromMilliseconds(220))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            BeginAnimation(TopProperty, slide);
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            timer.Start();
        };
        Closed += (_, _) =>
        {
            timer.Stop();
            if (current == this) current = null;
        };
    }

    public static ControlTemplate FlatButtonTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border), "Bd");
        bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        t.VisualTree = bd;
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), "Bd"));
        t.Triggers.Add(hover);
        return t;
    }

    public static ControlTemplate PillButtonTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border), "Bd");
        bd.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        t.VisualTree = bd;
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), "Bd"));
        t.Triggers.Add(hover);
        return t;
    }
}
