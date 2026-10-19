using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TidyDesktop.Boxes;

/// <summary>
/// A unified settings window for all TidyDesktop configurations, box actions, and snapshots.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private readonly BoxManager _manager;
    private readonly Action<string?> _changeLanguage;
    private readonly Action _restoreOriginal;
    private readonly ListBox _snapshotList = new() { Height = 130 };
    private readonly CheckBox _autoStartCheck;
    private readonly CheckBox _doubleClickCheck;
    private readonly CheckBox _historyCheck;
    private readonly ComboBox _langCombo = new() { Width = 220, HorizontalAlignment = HorizontalAlignment.Left };

    public SettingsWindow(BoxManager manager, Action<string?> changeLanguage, Action restoreOriginal)
    {
        _manager = manager;
        _changeLanguage = changeLanguage;
        _restoreOriginal = restoreOriginal;

        Title = Lang.T("settings.title");
        Width = 580;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = true;
        Background = new SolidColorBrush(Color.FromRgb(248, 249, 250));

        var root = new StackPanel { Margin = new Thickness(20) };

        // 1. General Section
        root.Children.Add(SectionHeader(Lang.T("settings.general")));
        var generalCard = CreateCard();

        _autoStartCheck = new CheckBox
        {
            Content = Lang.T("settings.autoStart"),
            IsChecked = AutoStart.IsEnabled,
            Margin = new Thickness(0, 0, 0, 10),
            FontWeight = FontWeights.Medium,
        };
        _autoStartCheck.Click += (_, _) =>
        {
            try
            {
                AutoStart.Set(_autoStartCheck.IsChecked == true);
            }
            catch (Exception ex)
            {
                AppData.Log($"Settings: Could not toggle auto start: {ex.Message}");
                MessageBox.Show(Lang.T("app.couldNotChangeStart", ex.Message), "TidyDesktop");
                _autoStartCheck.IsChecked = AutoStart.IsEnabled;
            }
        };
        generalCard.Inner.Children.Add(_autoStartCheck);

        var langRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        langRow.Children.Add(new TextBlock
        {
            Text = Lang.T("settings.language"),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(0, 0, 16, 0),
        });

        _langCombo.Items.Add(new ComboBoxItem { Content = Lang.T("app.sameAsWindows"), Tag = null });
        foreach (var (code, name) in Lang.Available)
        {
            _langCombo.Items.Add(new ComboBoxItem { Content = name, Tag = code });
        }
        SelectCurrentLanguage();
        _langCombo.SelectionChanged += (_, _) =>
        {
            if (_langCombo.SelectedItem is ComboBoxItem item)
            {
                var code = item.Tag as string;
                if (code != Lang.Choice)
                {
                    _changeLanguage(code);
                }
            }
        };
        langRow.Children.Add(_langCombo);
        generalCard.Inner.Children.Add(langRow);
        root.Children.Add(generalCard.Element);

        // 2. Behavior Section
        root.Children.Add(SectionHeader(Lang.T("settings.behavior")));
        var behaviorCard = CreateCard();

        _doubleClickCheck = new CheckBox
        {
            Content = Lang.T("settings.doubleClickHide"),
            IsChecked = _manager.DoubleClickHides,
            Margin = new Thickness(0, 0, 0, 8),
        };
        _doubleClickCheck.Click += (_, _) => _manager.DoubleClickHides = _doubleClickCheck.IsChecked == true;
        behaviorCard.Inner.Children.Add(_doubleClickCheck);

        _historyCheck = new CheckBox
        {
            Content = Lang.T("settings.historyPanel"),
            IsChecked = _manager.HistoryPanelVisible,
        };
        _historyCheck.Click += (_, _) => _manager.SetHistoryPanelVisible(_historyCheck.IsChecked == true);
        behaviorCard.Inner.Children.Add(_historyCheck);
        root.Children.Add(behaviorCard.Element);

        // 3. Boxes & Layout Section
        root.Children.Add(SectionHeader(Lang.T("settings.boxesAndLayout")));
        var layoutCard = CreateCard();

        var actionButtons = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        actionButtons.Children.Add(Button(Lang.T("settings.autoOrganizeBtn"), () =>
        {
            _manager.AutoOrganize();
            RefreshSnapshots();
        }));
        actionButtons.Children.Add(Button(Lang.T("settings.lineUpLeftBtn"), () => _manager.ArrangeBoxes(fromRight: false)));
        actionButtons.Children.Add(Button(Lang.T("settings.lineUpRightBtn"), () => _manager.ArrangeBoxes(fromRight: true)));
        actionButtons.Children.Add(Button(Lang.T("settings.fileRulesBtn"), () => _manager.OpenRules()));
        actionButtons.Children.Add(Button(Lang.T("settings.restoreOriginalBtn"), () => _restoreOriginal()));
        layoutCard.Inner.Children.Add(actionButtons);

        // Snapshot list
        layoutCard.Inner.Children.Add(new TextBlock
        {
            Text = Lang.T("settings.snapshots"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 4),
        });
        _snapshotList.MouseDoubleClick += (_, _) => RestoreSelectedSnapshot();
        layoutCard.Inner.Children.Add(_snapshotList);

        var snapshotButtons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        snapshotButtons.Children.Add(Button(Lang.T("app.saveLayoutNow"), () =>
        {
            _manager.TakeSnapshot(Lang.T("app.savedByHand"));
            RefreshSnapshots();
        }));
        snapshotButtons.Children.Add(Button(Lang.T("settings.restoreSnapshotBtn"), RestoreSelectedSnapshot));
        layoutCard.Inner.Children.Add(snapshotButtons);

        root.Children.Add(layoutCard.Element);

        // 4. Footer & About
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var folderBtn = Button(Lang.T("settings.openFolder"), () =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(AppData.Directory) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppData.Log($"Settings: Could not open settings folder: {ex.Message}");
            }
        });
        DockPanel.SetDock(folderBtn, Dock.Left);
        footer.Children.Add(folderBtn);

        var closeBtn = new Button
        {
            Content = Lang.T("settings.close"),
            Width = 90,
            Padding = new Thickness(12, 6, 12, 6),
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        closeBtn.Click += (_, _) => Close();
        DockPanel.SetDock(closeBtn, Dock.Right);
        footer.Children.Add(closeBtn);

        root.Children.Add(footer);

        Content = root;
        RefreshSnapshots();
    }

    private void SelectCurrentLanguage()
    {
        foreach (ComboBoxItem item in _langCombo.Items)
        {
            if (item.Tag as string == Lang.Choice)
            {
                _langCombo.SelectedItem = item;
                return;
            }
        }
        _langCombo.SelectedIndex = 0;
    }

    private static TextBlock SectionHeader(string title) => new()
    {
        Text = title,
        FontSize = 14,
        FontWeight = FontWeights.Bold,
        Foreground = new SolidColorBrush(Color.FromRgb(40, 44, 52)),
        Margin = new Thickness(0, 10, 0, 6),
    };

    private static (Border Element, StackPanel Inner) CreateCard()
    {
        var panel = new StackPanel();
        var border = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(220, 224, 230)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 8),
            Child = panel,
        };
        return (border, panel);
    }

    private static Button Button(string text, Action action)
    {
        var button = new Button
        {
            Content = text,
            Margin = new Thickness(0, 0, 6, 6),
            Padding = new Thickness(12, 5, 12, 5),
            Background = new SolidColorBrush(Color.FromRgb(240, 242, 245)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(200, 204, 210)),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        button.Click += (_, _) => action();
        return button;
    }

    private void RefreshSnapshots()
    {
        _snapshotList.Items.Clear();
        var snapshots = AppData.LoadSnapshots();
        if (snapshots.Count == 0)
        {
            _snapshotList.Items.Add(new ListBoxItem
            {
                Content = new TextBlock
                {
                    Text = Lang.T("app.noSavedLayouts"),
                    Foreground = Brushes.Gray,
                },
                IsEnabled = false,
            });
            return;
        }

        foreach (var (path, snapshot) in snapshots)
        {
            _snapshotList.Items.Add(new ListBoxItem
            {
                Tag = path,
                Content = new TextBlock
                {
                    Text = Lang.T("app.snapshotItem",
                        Lang.Date(snapshot.TakenAt, "format.shortDateTime"),
                        snapshot.Reason,
                        snapshot.Boxes.Count),
                },
            });
        }
    }

    private void RestoreSelectedSnapshot()
    {
        if (_snapshotList.SelectedItem is ListBoxItem { Tag: string path })
        {
            var saved = AppData.LoadSnapshot(path);
            if (saved == null)
                return;

            var time = Lang.Date(saved.TakenAt, "format.shortDateTime");
            if (MessageBox.Show(
                Lang.T("app.snapshotConfirm", time),
                "TidyDesktop", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            {
                _manager.RestoreSnapshot(path);
                RefreshSnapshots();
            }
        }
    }
}
