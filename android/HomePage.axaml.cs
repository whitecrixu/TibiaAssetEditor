using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Assets_Editor;

public partial class HomePage : UserControl, IPage
{
    private IStorageFolder? _clientFolder;
    private IStorageFolder? _serverFolder;
    private UpdateCheck.Release? _update;
    private bool _started;

    public HomePage()
    {
        InitializeComponent();
        LogList.ItemsSource = AppLog.Lines;
        VersionText.Text = "v" + UpdateCheck.CurrentVersionText;
        Refresh();
    }

    protected override async void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_started)
            return;
        _started = true;

        _clientFolder = await OpenBookmark(Workspace.Settings.ClientBookmark);
        _serverFolder = await OpenBookmark(Workspace.Settings.ServerBookmark);

        // files from the last session (or copied into the workspace over USB / adb)
        if (Workspace.HasClientFiles)
            await LoadClient();
        if (Workspace.HasServerFiles)
            await LoadServer();
        Refresh();

        await CheckUpdates(manual: false);
    }

    public void OnReturn() => Refresh();

    private IStorageProvider? Storage => TopLevel.GetTopLevel(this)?.StorageProvider;

    private async Task<IStorageFolder?> OpenBookmark(string? bookmark)
    {
        if (string.IsNullOrEmpty(bookmark) || Storage == null)
            return null;
        try
        {
            return await Storage.OpenFolderBookmarkAsync(bookmark);
        }
        catch (Exception ex)
        {
            AppLog.Add("The folder chosen last time is no longer available: " + ex.Message);
            return null;
        }
    }

    private void Refresh()
    {
        if (Workspace.ClientLoaded)
        {
            var t = Workspace.Things!;
            ClientStatus.Text = $"{t.Object.Count:N0} items · {t.Outfit.Count:N0} outfits · {t.Effect.Count:N0} effects · {t.Missile.Count:N0} missiles · {Workspace.Sprites!.SpriteCount:N0} sprites" +
                                $"\nSignature 0x{Workspace.DatSignature:X8} · {Workspace.Version?.Name}" +
                                $"{(MainWindow.Preset.Extended ? " · extended" : "")}{(MainWindow.Preset.FrameDurations ? " · frame durations" : "")}{(MainWindow.Preset.FrameGroups ? " · frame groups" : "")}";
        }
        else
            ClientStatus.Text = "Not loaded. Choose the folder with Tibia.dat, Tibia.spr and Tibia.otfi (or the client folder that contains data/things/860).";

        ClientFolder.Text = "Folder: " + Workspace.Settings.ClientFolderName + (_clientFolder == null && Workspace.Settings.ClientFolderName != null ? " (no access, choose it again to save there)" : "");
        ClientFolder.IsVisible = Workspace.Settings.ClientFolderName != null;
        ReloadClientButton.IsEnabled = _clientFolder != null;
        BrowseButton.IsEnabled = Workspace.ClientLoaded;

        if (Workspace.ServerLoaded)
            ServerStatus.Text = $"{Workspace.OtbItems?.Count ?? 0:N0} items.otb entries · {ItemsXml.Items.Count:N0} items.xml entries";
        ServerFolder.Text = "Folder: " + Workspace.Settings.ServerFolderName + (_serverFolder == null && Workspace.Settings.ServerFolderName != null ? " (no access, choose it again to save there)" : "");
        ServerFolder.IsVisible = Workspace.Settings.ServerFolderName != null;
        ReloadServerButton.IsEnabled = _serverFolder != null;

        var changed = new List<string>();
        if (Workspace.DatDirty) changed.Add("Tibia.dat");
        if (Workspace.OtbDirty) changed.Add("items.otb");
        if (Workspace.XmlDirty) changed.Add("items.xml");
        ChangesStatus.Text = changed.Count == 0 ? "No changes." : "Changed: " + string.Join(", ", changed);
        SaveButton.IsEnabled = changed.Count > 0;
    }

    // ---- loading ----

    private async Task LoadClient()
    {
        var progress = new Progress<int>(p => MainView.Current.ReportBusy(p, "Reading sprite table"));
        await MainView.Current.RunBusy("Loading Tibia.dat / Tibia.spr…", () => Task.Run(() => Workspace.LoadClient(progress)));
    }

    private async Task LoadServer()
    {
        await MainView.Current.RunBusy("Loading items.otb / items.xml…", () => Task.Run(Workspace.LoadServer));
    }

    private bool ConfirmDiscard()
    {
        if (!Workspace.HasChanges)
            return true;
        AppLog.Add("Save or discard your changes first: there are unsaved changes.");
        return false;
    }

    private async Task<IStorageFolder?> PickFolder(string title)
    {
        if (Storage == null)
            return null;
        var folders = await Storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.FirstOrDefault();
    }

    private async void ChooseClient_Click(object? sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard())
            return;
        IStorageFolder? picked = await PickFolder("Folder with Tibia.dat and Tibia.spr");
        if (picked == null)
            return;

        MainView.Current.ShowBusy("Looking for Tibia.dat…");
        IStorageFolder? folder = await Workspace.FindFolderWith(picked, "Tibia.dat");
        MainView.Current.HideBusy();
        if (folder == null)
        {
            AppLog.Add($"No Tibia.dat in {picked.Name} or its subfolders.");
            return;
        }

        await ImportClient(folder);
        if (!Workspace.ClientLoaded)
            return;
        _clientFolder = folder;
        Workspace.Settings.ClientBookmark = await folder.SaveBookmarkAsync();
        Workspace.Settings.ClientFolderName = await DisplayName(folder);
        Workspace.SaveSettings();
        Refresh();
    }

    private async void ReloadClient_Click(object? sender, RoutedEventArgs e)
    {
        if (_clientFolder == null || !ConfirmDiscard())
            return;
        await ImportClient(_clientFolder);
        Refresh();
    }

    private async Task ImportClient(IStorageFolder folder)
    {
        var progress = new Progress<(string file, int percent)>(p => MainView.Current.ReportBusy(p.percent, "Copying " + p.file));
        Exception? error = await MainView.Current.RunBusy("Copying the client files…",
            () => Workspace.CopyIn(folder, Workspace.ClientDir, Workspace.ClientFiles, progress));
        if (error != null)
        {
            AppLog.Add("Copy failed: " + error.Message);
            return;
        }
        await LoadClient();
    }

    private async void ChooseServer_Click(object? sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard())
            return;
        IStorageFolder? picked = await PickFolder("Folder with items.otb and items.xml");
        if (picked == null)
            return;

        MainView.Current.ShowBusy("Looking for items.otb…");
        IStorageFolder? folder = await Workspace.FindFolderWith(picked, "items.otb")
                                 ?? await Workspace.FindFolderWith(picked, "items.xml");
        MainView.Current.HideBusy();
        if (folder == null)
        {
            AppLog.Add($"No items.otb or items.xml in {picked.Name} or its subfolders.");
            return;
        }

        await ImportServer(folder);
        _serverFolder = folder;
        Workspace.Settings.ServerBookmark = await folder.SaveBookmarkAsync();
        Workspace.Settings.ServerFolderName = await DisplayName(folder);
        Workspace.SaveSettings();
        Refresh();
    }

    private async void ReloadServer_Click(object? sender, RoutedEventArgs e)
    {
        if (_serverFolder == null || !ConfirmDiscard())
            return;
        await ImportServer(_serverFolder);
        Refresh();
    }

    private async Task ImportServer(IStorageFolder folder)
    {
        Exception? error = await MainView.Current.RunBusy("Copying the server files…",
            () => Workspace.CopyIn(folder, Workspace.ServerDir, Workspace.ServerFiles, null));
        if (error != null)
        {
            AppLog.Add("Copy failed: " + error.Message);
            return;
        }
        await LoadServer();
    }

    private static async Task<string> DisplayName(IStorageFolder folder)
    {
        try
        {
            var parent = await folder.GetParentAsync();
            return parent != null ? $"{parent.Name}/{folder.Name}" : folder.Name;
        }
        catch
        {
            return folder.Name;
        }
    }

    private void Browse_Click(object? sender, RoutedEventArgs e)
    {
        MainView.Current.Push(new ThingsPage(), "Things", Workspace.Version?.Name);
    }

    // ---- save ----

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        List<(string dir, string name)> written = new();
        Exception? error = await MainView.Current.RunBusy("Saving…", () => Task.Run(() => written = Workspace.SaveLocal()));
        if (error != null)
        {
            AppLog.Add("Save failed, nothing was copied to your folders: " + error.Message);
            Refresh();
            return;
        }

        foreach (var group in written.GroupBy(w => w.dir))
        {
            bool client = group.Key == Workspace.ClientDir;
            IStorageFolder? target = client ? _clientFolder : _serverFolder;
            string names = string.Join(", ", group.Select(g => g.name));
            if (target == null)
            {
                AppLog.Add($"Saved {names} in the app workspace only: choose the {(client ? "client" : "server")} folder again to write it there.");
                continue;
            }

            Exception? copyError = await MainView.Current.RunBusy($"Writing {names}…",
                () => Workspace.CopyOut(target, group.Key, group.Select(g => g.name)));
            AppLog.Add(copyError == null
                ? $"Saved {names} to {target.Name} (previous version kept as -bak)."
                : $"Saved {names} in the app workspace, but writing to {target.Name} failed: {copyError.Message}");
        }

        if (written.Any(w => w.name == "Tibia.dat"))
            AppLog.Add("Tibia.dat changed: the game client needs a new update build to pick it up (check_things_manifest.py).");
        Refresh();
    }

    // ---- updates ----

    private async Task CheckUpdates(bool manual)
    {
        UpdateCheck.Release? release = await UpdateCheck.FindNewer();
        if (release == null)
        {
            if (manual)
                AppLog.Add($"No newer Android version than {UpdateCheck.CurrentVersionText} on GitHub.");
            return;
        }
        if (!manual && release.Tag == Workspace.Settings.SkippedUpdate)
            return;

        _update = release;
        UpdateTitle.Text = $"Update available: {release.Name}";
        UpdateNotes.Text = release.Notes;
        UpdateCard.IsVisible = true;
    }

    private async void CheckUpdates_Click(object? sender, RoutedEventArgs e) => await CheckUpdates(manual: true);

    private async void UpdateDownload_Click(object? sender, RoutedEventArgs e)
    {
        if (_update == null)
            return;
        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher != null)
            await launcher.LaunchUriAsync(new Uri(_update.DownloadUrl));
    }

    private void UpdateSkip_Click(object? sender, RoutedEventArgs e)
    {
        if (_update == null)
            return;
        Workspace.Settings.SkippedUpdate = _update.Tag;
        Workspace.SaveSettings();
        UpdateCard.IsVisible = false;
    }
}
