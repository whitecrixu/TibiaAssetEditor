using System;
using System.Text;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Assets_Editor;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Add("Crash: " + e.ExceptionObject);

        if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
            singleView.MainView = new MainView();
        base.OnFrameworkInitializationCompleted();
    }
}
