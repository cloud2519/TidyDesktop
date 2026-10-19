using System.IO;
using TidyDesktop.Desktop;

namespace TidyDesktop.Boxes;

public enum RuleAction
{
    /// <summary>Put the item in a box; the file stays on the desktop.</summary>
    PutInBox,

    /// <summary>Move the file into a folder.</summary>
    MoveToFolder,

    /// <summary>Move the file into a folder and leave a shortcut to it on the desktop.</summary>
    MoveAndLeaveShortcut,
}

/// <summary>
/// What to do with items that newly appear on the desktop. Every condition that is set must hold;
/// a rule with no condition matches nothing.
/// </summary>
public sealed class FileRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = Lang.T("rule.newRule");
    public bool Enabled { get; set; } = true;

    /// <summary>File extensions without the dot, for example "mp4".</summary>
    public List<string> Extensions { get; set; } = new();

    /// <summary>Text the file name must contain (ignoring case).</summary>
    public string? NameContains { get; set; }

    /// <summary>Id of a <see cref="Category"/> the item must belong to.</summary>
    public string? CategoryId { get; set; }

    /// <summary>Smallest size in megabytes, for files.</summary>
    public double? MinSizeMB { get; set; }

    public RuleAction Action { get; set; } = RuleAction.PutInBox;

    /// <summary>Target box for <see cref="RuleAction.PutInBox"/>.</summary>
    public string? BoxId { get; set; }

    /// <summary>Target folder for the move actions.</summary>
    public string? Folder { get; set; }

    public bool HasCondition =>
        Extensions.Count > 0 || !string.IsNullOrWhiteSpace(NameContains) || CategoryId != null || MinSizeMB > 0;

    public bool MovesFiles => Action != RuleAction.PutInBox;

    public bool Matches(string path)
    {
        if (!Enabled || !HasCondition || !Path.IsPathRooted(path))
            return false;
        var isFile = File.Exists(path);
        // Only files are moved; a folder can still be put in a box.
        if (MovesFiles && !isFile)
            return false;

        var name = Path.GetFileName(path);
        if (Extensions.Count > 0)
        {
            var extension = Path.GetExtension(path).TrimStart('.');
            if (!isFile || !Extensions.Any(e => e.Equals(extension, StringComparison.OrdinalIgnoreCase)))
                return false;
        }
        if (!string.IsNullOrWhiteSpace(NameContains) &&
            !name.Contains(NameContains.Trim(), StringComparison.CurrentCultureIgnoreCase))
            return false;
        if (CategoryId != null && Categorizer.ClassifyPath(path)?.Id != CategoryId)
            return false;
        if (MinSizeMB > 0 && (!isFile || new FileInfo(path).Length < MinSizeMB * 1024 * 1024))
            return false;
        return true;
    }

    /// <summary>A one-line summary such as "extension mp4, mkv · name has 'invoice'".</summary>
    public string DescribeCondition()
    {
        var parts = new List<string>();
        if (Extensions.Count > 0)
            parts.Add(Lang.T("rule.extension", string.Join(", ", Extensions)));
        if (!string.IsNullOrWhiteSpace(NameContains))
            parts.Add(Lang.T("rule.nameHas", NameContains.Trim()));
        if (Categorizer.Find(CategoryId) is { } category)
            parts.Add(Lang.T("rule.kind", category.Title));
        if (MinSizeMB > 0)
            parts.Add(Lang.T("rule.mbOrLarger", MinSizeMB));
        return parts.Count > 0 ? string.Join(" · ", parts) : Lang.T("rule.anyItem");
    }

    public string DescribeAction(Func<string?, string?> boxTitle) => Action switch
    {
        RuleAction.PutInBox => Lang.T("rule.putInBox", boxTitle(BoxId) ?? "?"),
        RuleAction.MoveToFolder => Lang.T("rule.move", Folder),
        _ => Lang.T("rule.moveAndLeaveShortcut", Folder),
    };

    /// <summary>Splits user input such as ".mp4, MKV avi" into extensions.</summary>
    public static List<string> ParseExtensions(string text) =>
        text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim().TrimStart('*').TrimStart('.').ToLowerInvariant())
            .Where(e => e.Length > 0)
            .Distinct()
            .ToList();
}
