using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Assets_Editor;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    public static void SetTheme(bool isDark)
    {
        if (Current != null)
        {
            Current.RequestedThemeVariant = isDark ? ThemeVariant.Dark : ThemeVariant.Light;
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
