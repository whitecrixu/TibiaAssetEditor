using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Assets_Editor;

/// <summary>A newer release found on GitHub.</summary>
public sealed record UpdateInfo(
    Version Version,
    string Tag,
    string Title,
    string Notes,
    string PageUrl,
    string? AssetName,
    string? DownloadUrl,
    string? Sha256,
    long Size)
{
    /// <summary>A package for this platform exists and GitHub published its checksum.</summary>
    public bool CanInstall => DownloadUrl != null && Sha256 != null && UpdateService.CanSelfUpdate;
}

/// <summary>
/// Checks GitHub releases of the editor and installs a newer one in place.
///
/// Install: the platform package (TibiaMultiToolEditor-vX-linux-x64.tar.gz / -win-x64.zip) is downloaded
/// into the config folder, checked against the SHA-256 GitHub publishes for the asset, and extracted.
/// The running program cannot swap its own files: a single-file build keeps loading assemblies from its
/// executable while it runs, so changing that file breaks the process before it can restart. Instead a
/// small installer script (sh on Linux, PowerShell on Windows) is started, the editor exits, and the
/// script waits for it, renames the installed files to "*.old", moves the new ones in (putting the old
/// ones back if anything fails) and starts the new version, which removes the "*.old" files.
/// </summary>
public static class UpdateService
{
    public const string Repository = "whitecrixu/TibiaAssetEditor";
    public static string ReleasesPage => $"https://github.com/{Repository}/releases";
    private const string OldSuffix = ".old";

    // the "*.old" files the last install created; only these are removed on the next start
    private static string CleanupListPath => Path.Combine(AppPaths.ConfigDirectory, "update-cleanup.txt");

    // overridable for tests (a local server standing in for api.github.com)
    public static string ApiBaseUrl { get; set; } = "https://api.github.com";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub rejects API requests without a User-Agent
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"TibiaMultiToolEditor/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static Version CurrentVersion
    {
        get
        {
            var v = typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    /// <summary>Package name suffix for this OS/CPU, or null when no package is published for it.</summary>
    public static string? PlatformAssetSuffix
    {
        get
        {
            if (RuntimeInformation.OSArchitecture != Architecture.X64) return null;
            if (OperatingSystem.IsLinux()) return "-linux-x64.tar.gz";
            if (OperatingSystem.IsWindows()) return "-win-x64.zip";
            return null;
        }
    }

    /// <summary>
    /// Only the published single-file build replaces itself; a development build (dotnet run, IDE) runs
    /// from bin/ where swapping files would be wrong, so it only reports updates.
    /// </summary>
    public static bool CanSelfUpdate =>
        string.IsNullOrEmpty(typeof(UpdateService).Assembly.Location) && !string.IsNullOrEmpty(Environment.ProcessPath);

    public static Version? ParseTag(string tag)
    {
        string t = tag.Trim().TrimStart('v', 'V');
        int cut = t.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) t = t[..cut];
        if (!Version.TryParse(t, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
    }

    /// <summary>The latest release if it is newer than this build, otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var response = await Http.GetAsync($"{ApiBaseUrl}/repos/{Repository}/releases/latest", ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        string tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = ParseTag(tag);
        if (version == null || version <= CurrentVersion)
            return null;

        string? assetName = null, url = null, sha = null;
        long size = 0;
        string? suffix = PlatformAssetSuffix;
        if (suffix != null && root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                assetName = name;
                url = asset.GetProperty("browser_download_url").GetString();
                size = asset.GetProperty("size").GetInt64();
                if (asset.TryGetProperty("digest", out var digest) && digest.GetString() is string d
                    && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha = d["sha256:".Length..].ToLowerInvariant();
                break;
            }
        }

        // only ever download from this repository's release assets
        if (url != null && !url.StartsWith($"https://github.com/{Repository}/releases/download/", StringComparison.Ordinal))
            url = null;

        return new UpdateInfo(
            version,
            tag,
            root.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag,
            root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
            root.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage,
            assetName, url, sha, size);
    }

    public static string UpdatesDirectory
    {
        get
        {
            string dir = Path.Combine(AppPaths.ConfigDirectory, "updates");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Downloads the package and checks its SHA-256. Returns the archive path.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (info.DownloadUrl == null || info.AssetName == null || info.Sha256 == null)
            throw new InvalidOperationException("This release has no verified package for this platform.");

        string target = Path.Combine(UpdatesDirectory, info.AssetName);
        string partial = target + ".part";
        using (var response = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? info.Size;
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = File.Create(partial);
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        string actual;
        await using (var stream = File.OpenRead(partial))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
        if (actual != info.Sha256)
        {
            File.Delete(partial);
            throw new InvalidDataException($"The downloaded package does not match its published checksum (expected {info.Sha256}, got {actual}).");
        }

        File.Move(partial, target, overwrite: true);
        return target;
    }

    /// <summary>Extracts the package into a fresh folder and checks it contains this executable.</summary>
    public static string Extract(string archive, string exeName)
    {
        string dir = Path.Combine(UpdatesDirectory, Path.GetFileNameWithoutExtension(archive).Replace(".tar", "") + "-files");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);

        // both extractors refuse entries that would land outside dir
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archive, dir);
        }
        else
        {
            using var file = File.OpenRead(archive);
            using var gz = new GZipStream(file, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gz, dir, overwriteFiles: true);
        }

        if (!File.Exists(Path.Combine(dir, exeName)))
            throw new InvalidDataException($"The package does not contain {exeName}.");
        return dir;
    }

    private const string LinuxInstaller = """
        #!/bin/sh
        # Tibia MultiTool Editor update installer: runs after the editor has exited.
        pid="$1"; src="$2"; dst="$3"; exe="$4"
        n=0
        while kill -0 "$pid" 2>/dev/null; do
            sleep 0.2; n=$((n + 1))
            [ "$n" -gt 9000 ] && exit 1  # editor still open after 30 min: give up, nothing changed
        done
        moved=""
        rollback() {
            IFS='|'
            for name in $moved; do [ -n "$name" ] && mv -f "$dst/$name.old" "$dst/$name"; done
            exit 1
        }
        for f in "$src"/*; do
            name=$(basename "$f")
            if [ -e "$dst/$name" ]; then
                mv -f "$dst/$name" "$dst/$name.old" || rollback
                moved="$moved|$name"
            fi
            mv -f "$f" "$dst/$name" || rollback
        done
        chmod +x "$dst/$exe"
        cd "$dst" && nohup "./$exe" >/dev/null 2>&1 &
        """;

    private const string WindowsInstaller = """
        # Tibia MultiTool Editor update installer: runs after the editor has exited.
        param([int]$ProcId, [string]$Src, [string]$Dst, [string]$Exe)
        try { Wait-Process -Id $ProcId -Timeout 1800 -ErrorAction Stop } catch { if (Get-Process -Id $ProcId -ErrorAction SilentlyContinue) { exit 1 } }
        Start-Sleep -Milliseconds 300
        $moved = @()
        try {
            foreach ($f in Get-ChildItem -LiteralPath $Src -File) {
                $t = Join-Path $Dst $f.Name
                if (Test-Path -LiteralPath $t) {
                    Move-Item -LiteralPath $t -Destination "$t.old" -Force -ErrorAction Stop
                    $moved += $t
                }
                Move-Item -LiteralPath $f.FullName -Destination $t -Force -ErrorAction Stop
            }
        } catch {
            foreach ($t in $moved) { Move-Item -LiteralPath "$t.old" -Destination $t -Force }
            exit 1
        }
        Start-Process -FilePath (Join-Path $Dst $Exe) -WorkingDirectory $Dst
        """;

    /// <summary>
    /// Starts the installer script for the extracted package and returns; the caller must then exit the
    /// app. Nothing in the installation is touched until this process has ended.
    /// </summary>
    public static void LaunchInstaller(string extractedDir, string installDir) =>
        LaunchInstaller(extractedDir, installDir, Environment.ProcessId, Path.GetFileName(Environment.ProcessPath!));

    public static Process? LaunchInstaller(string extractedDir, string installDir, int waitForPid, string exe)
    {
        // the files that will become "*.old", for CleanupAfterUpdate in the new version
        File.WriteAllLines(CleanupListPath, Directory.GetFiles(extractedDir)
            .Select(f => Path.Combine(installDir, Path.GetFileName(f)))
            .Where(File.Exists)
            .Select(t => t + OldSuffix));

        ProcessStartInfo start;
        if (OperatingSystem.IsWindows())
        {
            string script = Path.Combine(UpdatesDirectory, "install-update.ps1");
            File.WriteAllText(script, WindowsInstaller);
            start = new ProcessStartInfo("powershell.exe")
            {
                ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script,
                                 waitForPid.ToString(), extractedDir, installDir, exe },
                CreateNoWindow = true,
            };
        }
        else
        {
            string script = Path.Combine(UpdatesDirectory, "install-update.sh");
            File.WriteAllText(script, LinuxInstaller.Replace("\r\n", "\n")); // sh chokes on CRLF
            start = new ProcessStartInfo("/bin/sh")
            {
                ArgumentList = { script, waitForPid.ToString(), extractedDir, installDir, exe },
            };
        }
        start.UseShellExecute = false;
        start.WorkingDirectory = UpdatesDirectory;
        return Process.Start(start);
    }

    /// <summary>Whether files can be created next to the executable (not the case under /opt or Program Files).</summary>
    public static bool IsInstallDirWritable(string installDir)
    {
        try
        {
            string probe = Path.Combine(installDir, $".update-probe-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Removes "*.old" files and downloaded packages left by a previous update.</summary>
    public static void CleanupAfterUpdate()
    {
        try
        {
            if (File.Exists(CleanupListPath))
            {
                var left = new List<string>();
                foreach (string old in File.ReadAllLines(CleanupListPath).Where(l => l.EndsWith(OldSuffix)))
                {
                    try { if (File.Exists(old)) File.Delete(old); }
                    catch { left.Add(old); /* still locked: next start */ }
                }
                if (left.Count > 0) File.WriteAllLines(CleanupListPath, left);
                else File.Delete(CleanupListPath);
            }
            string updates = Path.Combine(AppPaths.ConfigDirectory, "updates");
            if (Directory.Exists(updates))
                Directory.Delete(updates, true);
        }
        catch
        {
            // best effort
        }
    }

    public static void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MainWindow.Log($"Unable to open {url}: {ex.Message}", "Warning");
        }
    }
}
