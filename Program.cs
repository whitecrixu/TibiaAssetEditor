using Avalonia;
using Projektanker.Icons.Avalonia;
using Projektanker.Icons.Avalonia.FontAwesome;
using System;

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
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
