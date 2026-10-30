using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Assets_Editor;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // an exception in an event handler (most handlers are async void) used to close the whole app
        // and lose unsaved edits; log it, tell the user and keep running
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Program.WriteCrashLog("UI thread", e.Exception);
            MainWindow.Log($"Unexpected error: {e.Exception.GetType().Name}: {e.Exception.Message}", "Error");
            ErrorManager.ShowError($"Unexpected error: {e.Exception.Message}\n\nDetails were written to {Program.CrashLogPath}");
            e.Handled = true;
        };

        AddHalloweenAccents();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    // dark based; Resources/Theme.axaml overrides the Semi colors for it
    public static readonly ThemeVariant Halloween = new("Halloween", ThemeVariant.Dark);

    // Semi copies its primary blue and its backgrounds into ~120 control brushes when it loads (check boxes,
    // selected rows, outline buttons, window background, ...), so overriding the SemiColor* keys alone leaves
    // them as they were. Every dark brush that carries one of these shades gets a Halloween twin.
    private static readonly Dictionary<Color, Color> HalloweenShades = new()
    {
        [Color.Parse("#54A9FF")] = Color.Parse("#FF7A1A"),
        [Color.Parse("#7FC1FF")] = Color.Parse("#FF9A4D"),
        [Color.Parse("#A9D7FF")] = Color.Parse("#FFB877"),
        [Color.Parse("#135CB8")] = Color.Parse("#8A3F0D"),
        [Color.Parse("#16161A")] = Color.Parse("#140C18"),
        [Color.Parse("#232429")] = Color.Parse("#1F1326"),
        [Color.Parse("#35363C")] = Color.Parse("#2C1C35"),
        [Color.Parse("#43444A")] = Color.Parse("#382443"),
        [Color.Parse("#4F5159")] = Color.Parse("#462D53"),
    };

    private void AddHalloweenAccents()
    {
        var keys = new HashSet<string>();
        void Collect(object? provider, int depth)
        {
            if (provider == null || depth > 16) return;
            switch (provider)
            {
                case ResourceInclude include: Collect(include.Loaded, depth + 1); break;
                case ResourceDictionary dictionary:
                    foreach (object key in dictionary.Keys)
                        if (key is string name) keys.Add(name);
                    foreach (var theme in dictionary.ThemeDictionaries)
                        if (theme.Key == ThemeVariant.Dark) Collect(theme.Value, depth + 1);
                    foreach (var merged in dictionary.MergedDictionaries) Collect(merged, depth + 1);
                    break;
                case StyleInclude style: Collect(style.Loaded, depth + 1); break;
                case Styles styles:
                    Collect(styles.Resources, depth + 1);
                    foreach (var child in styles) Collect(child, depth + 1);
                    break;
            }
        }
        Collect(Styles, 0);

        var halloween = new ResourceDictionary();
        foreach (string key in keys)
        {
            if (key.StartsWith("SemiColor") || !TryGetResource(key, ThemeVariant.Dark, out object? value))
                continue;
            if (value is ISolidColorBrush brush && HalloweenShades.TryGetValue(Color.FromRgb(brush.Color.R, brush.Color.G, brush.Color.B), out Color shade))
                halloween[key] = new SolidColorBrush(Color.FromArgb(brush.Color.A, shade.R, shade.G, shade.B), brush.Opacity);
            else if (value is Color color && HalloweenShades.TryGetValue(Color.FromRgb(color.R, color.G, color.B), out shade))
                halloween[key] = Color.FromArgb(color.A, shade.R, shade.G, shade.B);
        }
        Resources.ThemeDictionaries[Halloween] = halloween;
    }

    public static void SetTheme(MainWindow.UiTheme theme)
    {
        if (Current != null)
        {
            Current.RequestedThemeVariant = theme switch
            {
                MainWindow.UiTheme.Light => ThemeVariant.Light,
                MainWindow.UiTheme.Halloween => Halloween,
                _ => ThemeVariant.Dark,
            };
        }
    }

    // Density switch: overrides the four main spacing tokens from Theme.axaml.
    // Each option keeps proportions consistent so borders/padding still "feel"
    // right — only the overall tightness changes.
    public static void SetDensity(MainWindow.UiDensity d)
    {
        if (Current == null) return;
        double xs, sm, md, lg;
        switch (d)
        {
            case MainWindow.UiDensity.Compact:     xs = 2; sm = 6;  md = 8;  lg = 12; break;
            case MainWindow.UiDensity.Comfortable: xs = 6; sm = 10; md = 16; lg = 22; break;
            default: /* Cozy */                    xs = 4; sm = 8;  md = 12; lg = 16; break;
        }
        Current.Resources["Spacing.Xs"] = xs;
        Current.Resources["Spacing.Sm"] = sm;
        Current.Resources["Spacing.Md"] = md;
        Current.Resources["Spacing.Lg"] = lg;
        Current.Resources["Inset.Sm"]   = new Avalonia.Thickness(sm);
        Current.Resources["Inset.Md"]   = new Avalonia.Thickness(md);
        Current.Resources["Inset.Lg"]   = new Avalonia.Thickness(lg);
    }
}
