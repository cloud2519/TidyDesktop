using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace TidyDesktop.Boxes;

/// <summary>Lists the file rules and lets the user add, edit, reorder, remove and apply them.</summary>
internal sealed class RulesWindow : Window
{
    private readonly List<FileRule> _rules;
    private readonly Func<IReadOnlyList<(string Id, string Title)>> _boxes;
    private readonly Func<FileRule, int> _applyNow;
    private readonly Action _saved;
    private readonly ListBox _list = new() { Height = 260 };

    /// <param name="applyNow">Applies one rule to what is on the desktop now; returns how many items it handled.</param>
    /// <param name="saved">Called after every change, to store the rules.</param>
    public RulesWindow(List<FileRule> rules, Func<IReadOnlyList<(string Id, string Title)>> boxes,
        Func<FileRule, int> applyNow, Action saved)
    {
        _rules = rules;
        _boxes = boxes;
        _applyNow = applyNow;
        _saved = saved;

        Title = Lang.T("rules.fileRules");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = Lang.T("rules.intro"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        _list.MouseDoubleClick += (_, _) => Edit();
        panel.Children.Add(_list);

        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(Button(Lang.T("rules.add"), Add));
        buttons.Children.Add(Button(Lang.T("rules.edit"), Edit));
        buttons.Children.Add(Button(Lang.T("rules.delete"), Remove));
        buttons.Children.Add(Button(Lang.T("rules.up"), () => MoveSelected(-1)));
        buttons.Children.Add(Button(Lang.T("rules.down"), () => MoveSelected(1)));
        buttons.Children.Add(Button(Lang.T("rules.onOff"), ToggleEnabled));
        buttons.Children.Add(Button(Lang.T("rules.applyDesktopNow"), ApplySelected));
        panel.Children.Add(buttons);

        var close = new Button { Content = Lang.T("rules.close"), Width = 80, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        close.Click += (_, _) => Close();
        panel.Children.Add(close);

        Content = panel;
        Refresh();
    }

    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 3, 10, 3) };
        button.Click += (_, _) => action();
        return button;
    }

    private string? BoxTitle(string? id) => _boxes().FirstOrDefault(b => b.Id == id).Title;

    private void Refresh(FileRule? select = null)
    {
        _list.Items.Clear();
        foreach (var rule in _rules)
        {
            var item = new ListBoxItem
            {
                Tag = rule,
                Content = new TextBlock
                {
                    Text = $"{(rule.Enabled ? "●" : "○")}  {rule.Name}  —  {rule.DescribeCondition()}  →  {rule.DescribeAction(BoxTitle)}",
                    Opacity = rule.Enabled ? 1 : 0.5,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            };
            _list.Items.Add(item);
            if (rule == select)
                _list.SelectedItem = item;
        }
        if (_rules.Count == 0)
            _list.Items.Add(new ListBoxItem { Content = Lang.T("rules.noRulesYetMake"), IsEnabled = false });
    }

    private FileRule? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as FileRule;

    private void Add()
    {
        var rule = new FileRule();
        if (new RuleEditWindow(rule, _boxes()) { Owner = this }.ShowDialog() != true)
            return;
        _rules.Add(rule);
        Changed(rule);
    }

    private void Edit()
    {
        if (Selected is not { } rule)
            return;
        var copy = System.Text.Json.JsonSerializer.Deserialize<FileRule>(System.Text.Json.JsonSerializer.Serialize(rule))!;
        if (new RuleEditWindow(copy, _boxes()) { Owner = this }.ShowDialog() != true)
            return;
        _rules[_rules.IndexOf(rule)] = copy;
        Changed(copy);
    }

    private void Remove()
    {
        if (Selected is not { } rule)
            return;
        if (MessageBox.Show(this, Lang.T("rules.deleteRule", rule.Name), "TidyDesktop", MessageBoxButton.OKCancel) != MessageBoxResult.OK)
            return;
        _rules.Remove(rule);
        Changed(null);
    }

    private void MoveSelected(int step)
    {
        if (Selected is not { } rule)
            return;
        var index = _rules.IndexOf(rule);
        var target = index + step;
        if (target < 0 || target >= _rules.Count)
            return;
        (_rules[index], _rules[target]) = (_rules[target], _rules[index]);
        Changed(rule);
    }

    private void ToggleEnabled()
    {
        if (Selected is not { } rule)
            return;
        rule.Enabled = !rule.Enabled;
        Changed(rule);
    }

    private void ApplySelected()
    {
        if (Selected is not { } rule)
            return;
        var what = rule.MovesFiles
            ? Lang.T("rules.matchingFilesOnDesktop")
            : Lang.T("rules.matchingItemsOnDesktop");
        if (MessageBox.Show(this, Lang.T("rules.ruleContinue", rule.Name, what), "TidyDesktop", MessageBoxButton.OKCancel) != MessageBoxResult.OK)
            return;
        var count = _applyNow(rule);
        MessageBox.Show(this, Lang.T("rules.appliedItems", count), "TidyDesktop");
    }

    private void Changed(FileRule? select)
    {
        _saved();
        Refresh(select);
    }
}

/// <summary>Edits one rule: name, conditions and what to do.</summary>
internal sealed class RuleEditWindow : Window
{
    public RuleEditWindow(FileRule rule, IReadOnlyList<(string Id, string Title)> boxes)
    {
        Title = Lang.T("rules.editRule");
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(16) };

        panel.Children.Add(Label(Lang.T("rules.ruleName")));
        var name = new TextBox { Text = rule.Name, Padding = new Thickness(4, 3, 4, 3) };
        panel.Children.Add(name);

        panel.Children.Add(Label(Lang.T("rules.conditionsAllThatAre")));
        panel.Children.Add(Hint(Lang.T("rules.extensionsCommaSeparatedE")));
        var extensions = new TextBox { Text = string.Join(", ", rule.Extensions), Padding = new Thickness(4, 3, 4, 3) };
        panel.Children.Add(extensions);
        panel.Children.Add(Hint(Lang.T("rules.textInFileName")));
        var contains = new TextBox { Text = rule.NameContains ?? "", Padding = new Thickness(4, 3, 4, 3) };
        panel.Children.Add(contains);
        panel.Children.Add(Hint(Lang.T("rules.kind")));
        var category = new ComboBox();
        category.Items.Add(new ComboBoxItem { Content = Lang.T("rules.any"), Tag = null });
        foreach (var c in Categorizer.All)
            category.Items.Add(new ComboBoxItem { Content = c.Title, Tag = c.Id });
        category.SelectedItem = category.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == rule.CategoryId) ?? category.Items[0];
        panel.Children.Add(category);
        panel.Children.Add(Hint(Lang.T("rules.minimumSizeInMb")));
        var size = new TextBox { Text = rule.MinSizeMB > 0 ? $"{rule.MinSizeMB:0.#}" : "", Padding = new Thickness(4, 3, 4, 3), Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(size);

        panel.Children.Add(Label(Lang.T("rules.action")));
        var action = new ComboBox();
        action.Items.Add(new ComboBoxItem { Content = Lang.T("rules.putInBoxFile"), Tag = RuleAction.PutInBox });
        action.Items.Add(new ComboBoxItem { Content = Lang.T("rules.moveFolder"), Tag = RuleAction.MoveToFolder });
        action.Items.Add(new ComboBoxItem { Content = Lang.T("rules.moveFolderAndLeave"), Tag = RuleAction.MoveAndLeaveShortcut });
        action.SelectedItem = action.Items.Cast<ComboBoxItem>().First(i => (RuleAction)i.Tag == rule.Action);
        panel.Children.Add(action);

        var box = new ComboBox { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var (id, title) in boxes)
            box.Items.Add(new ComboBoxItem { Content = title, Tag = id });
        box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == rule.BoxId)
                           ?? (box.Items.Count > 0 ? box.Items[0] : null);
        panel.Children.Add(box);

        var folderRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var browse = new Button { Content = Lang.T("rules.browse"), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
        var folder = new TextBox { Text = rule.Folder ?? "", Padding = new Thickness(4, 3, 4, 3) };
        DockPanel.SetDock(browse, Dock.Right);
        folderRow.Children.Add(browse);
        folderRow.Children.Add(folder);
        panel.Children.Add(folderRow);
        browse.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Lang.T("rules.chooseFolderMove") };
            if (Directory.Exists(folder.Text))
                dialog.InitialDirectory = folder.Text;
            if (dialog.ShowDialog(this) == true)
                folder.Text = dialog.FolderName;
        };

        void UpdateTarget()
        {
            var moves = (RuleAction)((ComboBoxItem)action.SelectedItem).Tag != RuleAction.PutInBox;
            box.Visibility = moves ? Visibility.Collapsed : Visibility.Visible;
            folderRow.Visibility = moves ? Visibility.Visible : Visibility.Collapsed;
        }
        action.SelectionChanged += (_, _) => UpdateTarget();
        UpdateTarget();

        var ok = new Button { Content = Lang.T("common.ok"), Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = Lang.T("common.cancel"), Width = 80, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        ok.Click += (_, _) =>
        {
            var candidate = new FileRule
            {
                Extensions = FileRule.ParseExtensions(extensions.Text),
                NameContains = string.IsNullOrWhiteSpace(contains.Text) ? null : contains.Text.Trim(),
                CategoryId = (string?)((ComboBoxItem)category.SelectedItem).Tag,
                MinSizeMB = double.TryParse(size.Text, out var mb) && mb > 0 ? mb : null,
                Action = (RuleAction)((ComboBoxItem)action.SelectedItem).Tag,
                BoxId = (string?)(box.SelectedItem as ComboBoxItem)?.Tag,
                Folder = folder.Text.Trim(),
            };
            var problem = !candidate.HasCondition ? Lang.T("rules.setAtLeastOne")
                : candidate.Action == RuleAction.PutInBox && candidate.BoxId == null ? Lang.T("rules.chooseBoxPutItems")
                : candidate.MovesFiles && !Path.IsPathRooted(candidate.Folder) ? Lang.T("rules.chooseFolderMoveFiles")
                : candidate.MovesFiles && DesktopFolders().Any(d => string.Equals(Path.TrimEndingDirectorySeparator(candidate.Folder!), d, StringComparison.OrdinalIgnoreCase))
                    ? Lang.T("rules.chooseFolderOtherThan")
                : null;
            if (problem != null)
            {
                MessageBox.Show(this, problem, "TidyDesktop");
                return;
            }

            rule.Name = string.IsNullOrWhiteSpace(name.Text) ? Lang.T("rules.rule") : name.Text.Trim();
            rule.Extensions = candidate.Extensions;
            rule.NameContains = candidate.NameContains;
            rule.CategoryId = candidate.CategoryId;
            rule.MinSizeMB = candidate.MinSizeMB;
            rule.Action = candidate.Action;
            rule.BoxId = candidate.Action == RuleAction.PutInBox ? candidate.BoxId : null;
            rule.Folder = candidate.MovesFiles ? candidate.Folder : null;
            DialogResult = true;
        };

        Content = panel;
    }

    private static IEnumerable<string> DesktopFolders() =>
        Desktop.DesktopWatcher.Folders.Select(Path.TrimEndingDirectorySeparator);

    private static TextBlock Label(string text) =>
        new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) };

    private static TextBlock Hint(string text) =>
        new() { Text = text, Opacity = 0.75, Margin = new Thickness(0, 6, 0, 2) };
}
