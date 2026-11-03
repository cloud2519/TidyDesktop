using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using TidyDesktop.Boxes;

namespace TidyDesktop;

/// <summary>
/// Updates the zip build from the latest GitHub release. A running exe cannot be overwritten but can
/// be renamed, so the new exe takes its place and the old one is deleted after the restart. The
/// Microsoft Store build is updated by the Store and never uses this.
/// </summary>
internal static class Updater
{
    private const string LatestReleaseApi = "https://api.github.com/repos/cloud2519/TidyDesktop/releases/latest";
    public const string ReleasesPage = "https://github.com/cloud2519/TidyDesktop/releases/latest";

    // The release asset that is downloaded; the zip holds the same exe plus readme files.
    private const string ExeAsset = "TidyDesktop.exe";

    /// <summary>A newer release. <see cref="DownloadUrl"/> is null when it has no exe to install.</summary>
    public sealed record Release(Version Version, string? DownloadUrl, string? Sha256);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        // GitHub's API refuses requests without a user agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"TidyDesktop/{App.Version}");
        return client;
    }

    private static string ExePath => Environment.ProcessPath!;
    private static string OldExePath => ExePath + ".old";

    /// <summary>The latest release if it is newer than this build, otherwise null.</summary>
    public static async Task<Release?> FindNewerAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var tag = json.RootElement.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest) || Normalize(latest) <= Normalize(Version.Parse(App.Version)))
            return null;

        foreach (var asset in json.RootElement.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(asset.GetProperty("name").GetString(), ExeAsset, StringComparison.OrdinalIgnoreCase))
                continue;
            // GitHub lists a "sha256:..." digest for each asset; older releases may not have one.
            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            var sha256 = digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? digest[7..] : null;
            return new Release(latest, asset.GetProperty("browser_download_url").GetString(), sha256);
        }
        return new Release(latest, null, null);
    }

    // "1.0.7" and "1.0.7.0" are the same version, which Version does not consider equal.
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>Downloads the release and puts it in place of this exe; takes effect on the next start.</summary>
    public static async Task InstallAsync(Release release)
    {
        var download = ExePath + ".download";
        using (var response = await Http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(download);
            await response.Content.CopyToAsync(file);
        }

        if (release.Sha256 != null)
        {
            string hash;
            await using (var file = File.OpenRead(download))
                hash = Convert.ToHexString(await SHA256.HashDataAsync(file));
            if (!hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(download);
                throw new InvalidDataException($"The download does not match the release (sha256 {hash}).");
            }
        }

        File.Delete(OldExePath);
        File.Move(ExePath, OldExePath);
        try
        {
            File.Move(download, ExePath);
        }
        catch
        {
            File.Move(OldExePath, ExePath);
            throw;
        }
        AppData.Log($"Installed {release.Version}; restarting");
    }

    /// <summary>
    /// Deletes the exe the last update replaced. The old process may still be on its way out, so
    /// this tries for a little while in the background.
    /// </summary>
    public static void CleanUp()
    {
        if (!File.Exists(OldExePath))
            return;
        Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 20 && File.Exists(OldExePath); attempt++)
            {
                try
                {
                    File.Delete(OldExePath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(500);
                }
            }
        });
    }
}
