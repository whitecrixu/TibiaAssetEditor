using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Projektanker.Icons.Avalonia;
using Projektanker.Icons.Avalonia.FontAwesome;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Assets_Editor;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Icon font registration — must happen before the first AXAML resolves
        // an attached i:Attached.Icon="fa-*" value, so we do it before the
        // AppBuilder starts the desktop lifetime.
        IconProvider.Current.Register<FontAwesomeIconProvider>();

        // last-resort logging: anything that escapes the UI-thread handler (App) ends up in crash.log
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrashLog("Unhandled", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog("Unobserved task", e.Exception);
            e.SetObserved();
        };

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static string CrashLogPath => Path.Combine(AppPaths.ConfigDirectory, "crash.log");

    public static void WriteCrashLog(string kind, Exception? ex)
    {
        try
        {
            File.AppendAllText(CrashLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {kind}: {ex}\n\n");
        }
        catch
        {
            // nothing left to report to
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            // the toolbars use emoji glyphs; Linux usually has no emoji font, so they rendered as boxes
            .ConfigureFonts(fonts => fonts.AddFontCollection(new EmbeddedFontCollection(EmojiFontKey, new Uri("avares://AssetsEditor.Fonts"))))
            .With(new FontManagerOptions { FontFallbacks = new[] { new FontFallback { FontFamily = new FontFamily($"{EmojiFontKey}#Noto Emoji") } } })
            .LogToTrace();

    private static readonly Uri EmojiFontKey = new("fonts:Emoji");
}
