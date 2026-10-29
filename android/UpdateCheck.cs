using System;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace Assets_Editor;

/// <summary>
/// Windows, Linux and Android share one version: a release "vX.Y.Z" carries the desktop archives and the
/// .apk. Releases without an .apk (older desktop-only ones) are skipped. Installing is left to the
/// system: the APK is opened in the browser and Android asks for confirmation.
/// </summary>
public static class UpdateCheck
{
    private const string ReleasesUrl = "https://api.github.com/repos/whitecrixu/TibiaAssetEditor/releases?per_page=30";

    public sealed record Release(string Tag, string Name, string Notes, string DownloadUrl, Version Version);

    public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public static string CurrentVersionText =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? CurrentVersion.ToString(3);

    public static Version? ParseTag(string tag)
    {
        // "v2.6.0", and the "android-v0.1.2-alpha" tags of the first Android builds
        string text = tag.StartsWith("android-", StringComparison.OrdinalIgnoreCase) ? tag["android-".Length..] : tag;
        if (!text.StartsWith('v') && !text.StartsWith('V'))
            return null;
        text = text[1..].Split('-')[0];
        return Version.TryParse(text, out Version? v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
    }

    public static async Task<Release?> FindNewer()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("TibiaMultiTool-Android/" + CurrentVersion.ToString(3));
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using JsonDocument doc = JsonDocument.Parse(await http.GetStringAsync(ReleasesUrl));

            Version current = new(CurrentVersion.Major, CurrentVersion.Minor, Math.Max(0, CurrentVersion.Build));
            Release? best = null;
            foreach (JsonElement r in doc.RootElement.EnumerateArray())
            {
                if (r.GetProperty("draft").GetBoolean())
                    continue;
                string tag = r.GetProperty("tag_name").GetString() ?? "";
                Version? version = ParseTag(tag);
                if (version == null || version <= current || (best != null && version <= best.Version))
                    continue;

                string? apk = r.GetProperty("assets").EnumerateArray()
                    .Select(a => a.GetProperty("browser_download_url").GetString())
                    .FirstOrDefault(u => u != null && u.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));
                if (apk == null)
                    continue;
                best = new Release(tag, r.GetProperty("name").GetString() ?? tag, r.GetProperty("body").GetString() ?? "", apk, version);
            }
            return best;
        }
        catch (Exception ex)
        {
            AppLog.Add("Update check failed: " + ex.Message);
            return null;
        }
    }
}
