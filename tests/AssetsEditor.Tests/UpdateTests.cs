using System;
using System.IO;
using Xunit;

namespace Assets_Editor.Tests;

// The self-update install step: files next to the executable are swapped, never lost.
public class UpdateTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tae-tests-").FullName;

    public UpdateTests()
    {
        AppPaths.ConfigDirectoryOverride = Path.Combine(_dir, "config");
    }

    public void Dispose()
    {
        AppPaths.ConfigDirectoryOverride = null;
        Directory.Delete(_dir, true);
    }

    [Theory]
    [InlineData("v2.2", "2.2.0")]
    [InlineData("v2.3.1", "2.3.1")]
    [InlineData("V3.0-beta", "3.0.0")]
    public void Tags_parse_to_versions(string tag, string expected)
    {
        Assert.Equal(Version.Parse(expected), UpdateService.ParseTag(tag));
    }

    [Fact]
    public void Garbage_tag_is_not_a_version()
    {
        Assert.Null(UpdateService.ParseTag("latest"));
    }

    private (string install, string package) Layout()
    {
        string install = Path.Combine(_dir, "install"), package = Path.Combine(_dir, "package");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(install, "app"), "old app");
        File.WriteAllText(Path.Combine(install, "Appearances.xml"), "old xml");
        File.WriteAllText(Path.Combine(install, "keep.old"), "not ours");
        File.WriteAllText(Path.Combine(package, "app"), "new app");
        File.WriteAllText(Path.Combine(package, "Appearances.xml"), "new xml");
        return (install, package);
    }

    [Fact]
    public void Installer_script_swaps_files_after_the_editor_exits_and_starts_the_new_version()
    {
        if (OperatingSystem.IsWindows()) return; // the PowerShell installer is not exercised here
        var (install, package) = Layout();
        // the "new app" is a script that leaves a marker, standing in for the restarted editor
        File.WriteAllText(Path.Combine(package, "app"), "#!/bin/sh\necho started > \"$(dirname \"$0\")/started.txt\"\n");

        using var editor = System.Diagnostics.Process.Start("sleep", "1")!;
        using var installer = UpdateService.LaunchInstaller(package, install, editor.Id, "app")!;
        Assert.True(File.Exists(Path.Combine(install, "Appearances.xml")));
        Assert.Equal("old xml", File.ReadAllText(Path.Combine(install, "Appearances.xml"))); // still waiting

        Assert.True(installer.WaitForExit(15000));
        Assert.Equal(0, installer.ExitCode);
        Assert.Equal("new xml", File.ReadAllText(Path.Combine(install, "Appearances.xml")));
        Assert.Equal("old app", File.ReadAllText(Path.Combine(install, "app.old")));
        var started = Path.Combine(install, "started.txt");
        for (int i = 0; i < 50 && !File.Exists(started); i++) System.Threading.Thread.Sleep(100);
        Assert.True(File.Exists(started));

        UpdateService.CleanupAfterUpdate();
        Assert.False(File.Exists(Path.Combine(install, "app.old")));
        Assert.False(File.Exists(Path.Combine(install, "Appearances.xml.old")));
        Assert.True(File.Exists(Path.Combine(install, "keep.old")));
    }
}
