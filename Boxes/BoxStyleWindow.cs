using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace TidyDesktop.Boxes;

/// <summary>
/// Edits a box's name, background color, opacity and auto-collect category.
/// Changes preview live; cancelling puts the original values back.
/// </summary>
public sealed class BoxStyleWindow : Window
{
    private readonly BoxModel _model;
    private readonly Action _preview;
    private readonly (string Title, string Color, double Opacity, string? Category) _original;
    private readonly TextBlock _opacityLabel = new() { Width = 44, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _customSwatch;

    public BoxStyleWindow(BoxModel model, Action preview)
    {
        _model = model;
        _preview = preview;
        _original = (model.Title, model.Color, model.Opacity, model.Category);

        Title = Lang.T("style.customizeBox");
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(16) };

        panel.Children.Add(Label(Lang.T("style.name")));
        var name = new TextBox { Text = model.Title, Padding = new Thickness(4, 3, 4, 3) };
        name.TextChanged += (_, _) =>
        {
            if (name.Text.Trim().Length > 0)
                Update(() => _model.Title = name.Text.Trim());
        };
        panel.Children.Add(name);

        panel.Children.Add(Label(Lang.T("style.backgroundColor")));
        var swatches = new WrapPanel();
        foreach (var hex in BoxStyle.Presets)
            swatches.Children.Add(Swatch(hex));
        _customSwatch = Swatch(model.Color);
        var custom = new Button { Content = Lang.T("style.custom"), Margin = new Thickness(4, 2, 0, 2), Padding = new Thickness(8, 2, 8, 2) };
        custom.Click += (_, _) => PickCustomColor();
        swatches.Children.Add(_customSwatch);
        swatches.Children.Add(custom);
        panel.Children.Add(swatches);

        panel.Children.Add(Label(Lang.T("style.opacityLowerShowsMore")));
        var slider = new Slider
        {
            Minimum = BoxStyle.MinOpacity * 100,
            Maximum = 100,
            Value = BoxStyle.ClampOpacity(model.Opacity) * 100,
            Width = 280,
            IsSnapToTickEnabled = true,
            TickFrequency = 1,
            VerticalAlignment = VerticalAlignment.Center,
        };
        slider.ValueChanged += (_, e) => Update(() => _model.Opacity = e.NewValue / 100);
        var opacityRow = new StackPanel { Orientation = Orientation.Horizontal };
        opacityRow.Children.Add(slider);
        opacityRow.Children.Add(_opacityLabel);
        panel.Children.Add(opacityRow);

        panel.Children.Add(Label(Lang.T("style.addNewDesktopItems")));
        var category = new ComboBox();
        category.Items.Add(new ComboBoxItem { Content = Lang.T("style.off"), Tag = null });
        foreach (var c in Categorizer.All)
            category.Items.Add(new ComboBoxItem { Content = c.Title, Tag = c.Id });
        category.SelectedItem = category.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => (string?)i.Tag == model.Category) ?? category.Items[0];
        category.SelectionChanged += (_, _) =>
            _model.Category = (string?)((ComboBoxItem)category.SelectedItem).Tag;
        panel.Children.Add(category);

        var ok = new Button { Content = Lang.T("common.ok"), Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) =>
        {
            DialogResult = true;
        };
        var cancel = new Button { Content = Lang.T("common.cancel"), Width = 80, IsCancel = true };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        Content = panel;
        UpdateOpacityLabel();
        Closed += (_, _) =>
        {
            if (DialogResult != true)
                Revert();
        };
    }

    private static TextBlock Label(string text) =>
        new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) };

    private Border Swatch(string hex)
    {
        var swatch = new Border
        {
            Width = 26,
            Height = 26,
            Margin = new Thickness(2),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Gray,
            Background = new SolidColorBrush(BoxStyle.ParseColor(hex)),
            Cursor = Cursors.Hand,
            Tag = hex,
            ToolTip = hex,
        };
        swatch.MouseLeftButtonUp += (s, _) => SetColor((string)((Border)s).Tag);
        return swatch;
    }

    private void PickCustomColor()
    {
        var current = BoxStyle.ParseColor(_model.Color);
        using var dialog = new Forms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK)
            return;
        var picked = Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B);
        var hex = BoxStyle.ToHex(picked);
        _customSwatch.Background = new SolidColorBrush(picked);
        _customSwatch.Tag = hex;
        SetColor(hex);
    }

    private void SetColor(string hex) => Update(() => _model.Color = hex);

    private void Update(Action change)
    {
        change();
        UpdateOpacityLabel();
        _preview();
    }

    private void UpdateOpacityLabel() =>
        _opacityLabel.Text = $" {Math.Round(BoxStyle.ClampOpacity(_model.Opacity) * 100)}%";

    private void Revert()
    {
        (_model.Title, _model.Color, _model.Opacity, _model.Category) = _original;
        _preview();
    }
}
