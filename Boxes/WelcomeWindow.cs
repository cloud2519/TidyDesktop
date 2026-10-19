using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TidyDesktop.Boxes;

/// <summary>
/// A welcoming first-run setup window that asks the user about auto-start and initial auto-organization.
/// </summary>
internal sealed class WelcomeWindow : Window
{
    public bool ShouldAutoStart => _autoStartCheck.IsChecked == true;
    public bool ShouldAutoOrganize => _autoOrganizeCheck.IsChecked == true;

    private readonly CheckBox _autoStartCheck;
    private readonly CheckBox _autoOrganizeCheck;

    public WelcomeWindow()
    {
        Title = Lang.T("welcome.title");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = true;
        Background = new SolidColorBrush(Color.FromRgb(248, 249, 250));

        var root = new StackPanel { Margin = new Thickness(24) };

        // Header
        root.Children.Add(new TextBlock
        {
            Text = Lang.T("welcome.heading"),
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(30, 35, 45)),
            Margin = new Thickness(0, 0, 0, 8),
        });

        root.Children.Add(new TextBlock
        {
            Text = Lang.T("welcome.intro"),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(100, 105, 115)),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            Margin = new Thickness(0, 0, 0, 20),
        });

        // Option 1: Auto-start
        var card1 = CreateCard();
        _autoStartCheck = new CheckBox
        {
            Content = Lang.T("welcome.autoStart"),
            IsChecked = true,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 4),
        };
        card1.Inner.Children.Add(_autoStartCheck);
        card1.Inner.Children.Add(new TextBlock
        {
            Text = Lang.T("welcome.autoStartHint"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(120, 125, 135)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24, 0, 0, 0),
        });
        root.Children.Add(card1.Element);

        // Option 2: Auto-organize by kind
        var card2 = CreateCard();
        _autoOrganizeCheck = new CheckBox
        {
            Content = Lang.T("welcome.autoOrganize"),
            IsChecked = true,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 4),
        };
        card2.Inner.Children.Add(_autoOrganizeCheck);
        card2.Inner.Children.Add(new TextBlock
        {
            Text = Lang.T("welcome.autoOrganizeHint"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(120, 125, 135)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24, 0, 0, 0),
        });
        root.Children.Add(card2.Element);

        // Action button
        var startBtn = new Button
        {
            Content = Lang.T("welcome.start"),
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(0, 120, 215)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(24, 10, 24, 10),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            IsDefault = true,
        };
        startBtn.Click += (_, _) =>
        {
            DialogResult = true;
            Close();
        };
        root.Children.Add(startBtn);

        Content = root;
    }

    private static (Border Element, StackPanel Inner) CreateCard()
    {
        var panel = new StackPanel();
        var border = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(225, 229, 235)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 12),
            Child = panel,
        };
        return (border, panel);
    }
}
