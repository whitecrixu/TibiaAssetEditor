using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using Avalonia.Threading;

namespace Assets_Editor;

// Stand-ins for the desktop types the shared file-format code touches (same shape as tests/Stubs.cs).

public class PresetSettings
{
    public int Version { get; set; }
    public bool Extended { get; set; }
    public bool Transparent { get; set; }
    public bool FrameDurations { get; set; }
    public bool FrameGroups { get; set; }
}

public static class MainWindow
{
    public static PresetSettings Preset = new();
    public static PresetSettings? GetCurrentPreset() => Preset;

    private static DatStructure? _datStructure;
    // created on first use: its constructor reads Appearances.xml, which App extracts at startup
    public static DatStructure datStructure => _datStructure ??= new DatStructure();

    public static SpriteStorage MainSprStorage = new();

    public static void Log(string message, string level = "Info") => AppLog.Add($"{level}: {message}");
}

public static class ErrorManager
{
    public static void ShowInfo(string message) => AppLog.Add("Info: " + message);
    public static void ShowWarning(string message) => AppLog.Add("Warning: " + message);
    public static void ShowError(string message) => AppLog.Add("Error: " + message);
}

public static class ObdDecoder
{
    public static void SetDatStructure(VersionInfo versionInfo) { }
}

/// <summary>Messages shown on the home screen; the shared code reports problems through MainWindow/ErrorManager.</summary>
public static class AppLog
{
    public static ObservableCollection<string> Lines { get; } = new();

    public static void Add(string line)
    {
        string stamped = $"{DateTime.Now:HH:mm:ss} {line}";
        try
        {
            File.AppendAllText(AppPaths.UserFile("app.log"), stamped + "\n");
        }
        catch
        {
            // the log file is a convenience
        }
        Dispatcher.UIThread.Post(() =>
        {
            Lines.Insert(0, stamped);
            while (Lines.Count > 200)
                Lines.RemoveAt(Lines.Count - 1);
        });
    }
}

/// <summary>
/// Android version of the desktop AppPaths: bundled files are extracted from the assembly into the app's
/// private folder, user data lives next to them.
/// </summary>
public static class AppPaths
{
    public static string BaseDirectory { get; set; } = AppContext.BaseDirectory;

    public static string Bundled(string fileName) => Path.Combine(BaseDirectory, fileName);

    public static string UserFile(string fileName) => Path.Combine(BaseDirectory, fileName);

    public static void ExtractBundled(string fileName)
    {
        using Stream? source = typeof(AppPaths).Assembly.GetManifestResourceStream(fileName);
        if (source == null)
            return;
        using FileStream target = File.Create(Bundled(fileName));
        source.CopyTo(target);
    }

    public static void WriteAllTextAtomic(string path, string contents)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, contents, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}
