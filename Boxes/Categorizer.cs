using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using TidyDesktop.Desktop;

namespace TidyDesktop.Boxes;

/// <summary>A purpose that desktop items can be sorted into.</summary>
internal sealed record Category(string Id, string Color)
{
    public string Title => Lang.T("category." + Id);
}

/// <summary>
/// Guesses what a desktop item is for: shortcuts by app name and target, files by extension.
/// </summary>
internal static class Categorizer
{
    public static readonly Category Games = new("games", "#4B2D5E");
    public static readonly Category Media = new("media", "#6B2D2D");
    public static readonly Category Internet = new("internet", "#1F3A5F");
    public static readonly Category Office = new("office", "#1E4D4A");
    public static readonly Category System = new("system", "#3A3F4B");
    public static readonly Category Finance = new("finance", "#6B4A1E");
    public static readonly Category Dev = new("dev", "#203A4B");
    public static readonly Category Apps = new("apps", "#2F3640");
    public static readonly Category Documents = new("documents", "#2F4F2F");
    public static readonly Category Pictures = new("pictures", "#5A4A1E");
    public static readonly Category Videos = new("videos", "#5E2D4B");
    public static readonly Category Music = new("music", "#2D4B5E");
    public static readonly Category Archives = new("archives", "#4A4A3A");
    public static readonly Category Folders = new("folders", "#5A4A2A");
    public static readonly Category OtherFiles = new("files", "#3A3A3A");

    /// <summary>All categories, in the order their boxes are placed.</summary>
    public static readonly Category[] All =
    {
        Internet, Office, Finance, Media, Dev, System, Games, Apps,
        Folders, Documents, Pictures, Videos, Music, Archives, OtherFiles,
    };

    // Shortcuts whose whole name is one of these; too short to match safely as substrings.
    private static readonly Dictionary<string, Category> ExactNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EA"] = Games,
    };

    // Custom URL schemes that open a game launcher.
    private static readonly string[] GameSchemes =
    {
        "steam:", "com.epicgames.launcher:", "uplay:", "ubisoft:", "origin:", "origin2:", "eadm:",
        "battlenet:", "riotclient:", "rockstar:", "goggalaxy:",
    };

    // Checked in this order, so e.g. "NVIDIA Broadcast" lands in media before "nvidia" matches system.
    private static readonly (Category Category, string[] Keywords)[] AppKeywords =
    {
        (Games, new[] { "steam", "nintendo", "epic games", "battle.net", "riot", "league of legends",
            "wallpaper engine", "ubisoft", "ea app", "ea sports", "xbox", "minecraft", "gog galaxy", "game",
            "roblox", "fifa", "battlefront", "star wars", "witcher", "nexon", "넥슨", "nkstarter",
            "lossless scaling", "red dead", "rockstar" }),
        (Finance, new[] { "영웅문", "creon", "크레온", "efriend", "챔피언", "키움", "kiwoom", "증권", "hts",
            "미래에셋", "삼성증권", "나무증권", "유안타", "이베스트", "업비트", "upbit", "빗썸", "bithumb",
            "tradingview", "트레이딩뷰" }),
        (Dev, new[] { "cursor", "antigravity", "dbeaver", "heidisql", "visual studio", "vscode", "code.exe",
            "github", "git bash", "postman", "docker", "jetbrains", "intellij", "pycharm", "android studio",
            "comfy", "sql", "python", "node.js" }),
        (Media, new[] { "팟플레이어", "potplayer", "vlc", "obs studio", "obs64", "내 obs", "ocam", "샤나인코더",
            "nero", "4k video", "elgato", "stream deck", "camera hub", "broadcast", "픽픽", "picpick", "fxsound",
            "spotify", "melon", "premiere", "davinci", "capcut", "vrew", "submaker", "audacity", "handbrake",
            "kmplayer", "곰플레이어" }),
        (Internet, new[] { "chrome", "firefox", "opera", "whale", "웨일", "msedge", "microsoft edge",
            "tor browser", "comet", "brave", "zoom", "카카오톡", "kakaotalk", "discord", "telegram", "slack",
            "teams", "gemini", "chatgpt", "claude", "naver", "네이버", "vpn" }),
        (Office, new[] { "한글", "한셀", "한쇼", "한워드", "한pdf", "hwp", "hancom", "winword", "microsoft word",
            "excel", "powerpnt", "powerpoint", "outlook", "onenote", "foxit", "acrobat", "pdf", "notion",
            "ebook", "yes24", "evernote", "obsidian" }),
        (System, new[] { "nvidia", "intel", "gpu-z", "techpowerup", "afterburner", "msi", "seatools",
            "aomei", "backup", "백업", "everything", "synology", "zoomit", "7-zip", "bandizip",
            "ccleaner", "groupy", "stardock", "goodbyedpi", "rufus", "cpu-z", "hwmonitor", "control panel",
            "제어판", "terminal", "powershell", "logitech", "g hub", "uninstall", "제거", "autohotkey" }),
    };

    private static readonly Dictionary<string, Category> Extensions = BuildExtensions();

    private static Dictionary<string, Category> BuildExtensions()
    {
        var map = new Dictionary<string, Category>(StringComparer.OrdinalIgnoreCase);
        void Add(Category category, params string[] extensions)
        {
            foreach (var ext in extensions)
                map[ext] = category;
        }

        Add(Documents, ".doc", ".docx", ".ppt", ".pptx", ".xls", ".xlsx", ".pdf", ".txt", ".hwp", ".hwpx",
            ".md", ".csv", ".rtf", ".odt", ".show", ".cell");
        Add(Pictures, ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".svg", ".tif", ".tiff", ".ico");
        Add(Videos, ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".m4v", ".ts");
        Add(Music, ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg");
        Add(Archives, ".zip", ".7z", ".rar", ".alz", ".egg", ".tar", ".gz");
        Add(Apps, ".exe", ".bat", ".cmd", ".ps1", ".msi", ".appref-ms");
        return map;
    }

    public static Category? Find(string? id) => All.FirstOrDefault(c => c.Id == id);

    /// <summary>The category of a file or folder given only its path.</summary>
    public static Category? ClassifyPath(string path) =>
        Classify(new DesktopIcon
        {
            Key = path,
            DisplayName = Path.GetFileNameWithoutExtension(path),
            Position = default,
        });

    /// <summary>Returns the item's category, or null for system items such as the Recycle Bin.</summary>
    public static Category? Classify(DesktopIcon icon)
    {
        var path = icon.Key;
        if (path.StartsWith("::", StringComparison.Ordinal) || !Path.IsPathRooted(path))
            return null;
        if (Directory.Exists(path))
            return Folders;

        var extension = Path.GetExtension(path);
        if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            return ClassifyShortcut(icon.DisplayName, ResolveShortcut(path));
        if (extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
            return ClassifyShortcut(icon.DisplayName, ReadUrl(path), isUrl: true);

        return Extensions.TryGetValue(extension, out var category) ? category : OtherFiles;
    }

    private static Category ClassifyShortcut(string name, string? target, bool isUrl = false)
    {
        if (ExactNames.TryGetValue(name.Trim(), out var exact))
            return exact;

        // Known app names win over where the shortcut points, e.g. a Steam-installed tool is still a tool.
        var targetName = target == null || target.Contains("://") ? "" : Path.GetFileName(target);
        var haystack = $"{name} {targetName}".ToLowerInvariant();
        foreach (var (category, keywords) in AppKeywords)
        {
            if (keywords.Any(haystack.Contains))
                return category;
        }

        if (target == null)
            return isUrl ? Internet : Apps;
        if (GameSchemes.Any(scheme => target.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)))
            return Games;
        if (isUrl)
        {
            // Web addresses are internet; any other scheme opens some installed app.
            return target.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? Internet : Apps;
        }
        if (Directory.Exists(target))
            return Folders;

        // A shortcut to a document or picture belongs with those files.
        if (Extensions.TryGetValue(Path.GetExtension(target), out var byTarget) && byTarget != Apps)
            return byTarget;
        return Apps;
    }

    private static string? ReadUrl(string path)
    {
        try
        {
            return File.ReadLines(path)
                .FirstOrDefault(line => line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))?[4..];
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string? ResolveShortcut(string path)
    {
        try
        {
            var link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(path, 0);
            var target = new StringBuilder(260);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            return target.Length > 0 ? target.ToString() : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    // Only the first method is used, so the rest of the vtable is omitted.
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
    }
}
