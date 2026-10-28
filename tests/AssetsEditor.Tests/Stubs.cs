using System;
using System.Collections.Generic;

namespace Assets_Editor;

// Minimal stand-ins for the UI types the file-format code touches.
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
    public static readonly DatStructure datStructure = new();
    public static SpriteStorage MainSprStorage = new();
    public static readonly List<string> LogLines = new();
    public static void Log(string message, string level = "Info") => LogLines.Add($"{level}: {message}");
}

public static class ErrorManager
{
    public static readonly List<string> Messages = new();
    public static void ShowInfo(string m) => Messages.Add("INFO " + m);
    public static void ShowWarning(string m) => Messages.Add("WARN " + m);
    public static void ShowError(string m) => Messages.Add("ERROR " + m);
}

public static class ObdDecoder
{
    public static void SetDatStructure(VersionInfo v) { }
}
