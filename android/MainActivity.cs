using System.IO;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Avalonia;
using Avalonia.Android;

namespace Assets_Editor;

[Activity(
    Label = "Tibia MultiTool",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize)]
public class MainActivity : AvaloniaMainActivity<App>
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // before Avalonia starts: the shared code reads Appearances.xml through AppPaths
        string files = FilesDir!.AbsolutePath;
        AppPaths.BaseDirectory = files;
        AppPaths.ExtractBundled("Appearances.xml");

        // the workspace sits in the app's external folder, so it can also be filled over USB / adb
        string root = GetExternalFilesDir(null)?.AbsolutePath ?? files;
        Workspace.Root = Path.Combine(root, "workspace");
        Directory.CreateDirectory(Workspace.ClientDir);
        Directory.CreateDirectory(Workspace.ServerDir);
        Workspace.LoadSettings();

        base.OnCreate(savedInstanceState);
    }
}
