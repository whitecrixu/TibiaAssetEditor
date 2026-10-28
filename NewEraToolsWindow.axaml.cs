using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Tibia.Protobuf.Appearances;

namespace Assets_Editor
{
    // NewEra / WolfOTS tools for the legacy 8.60 client (client-mehah) and the TFS 1.5 server:
    //   Consistency        dat <-> items.otb (items_otb.py check --dat), items.xml, monsters, outfits.xml
    //   Things guard       SHA-256 of the loaded things, frozen-tree warning, update channel manifest compare
    //   Things diff        edited things vs the original dat + the regeneration checklist
    //   Export for approval  staging folder in the newera-files inbox layout + client things + SHA256SUMS + README
    // Open with `new NewEraToolsWindow().Show(owner)` after a client is loaded.
    public partial class NewEraToolsWindow : Window
    {
        private static readonly Dictionary<Guid, string> LastFolders = [];

        private List<Finding> _findings = [];
        private CancellationTokenSource? _checkCts;
        private ThingsDiffResult? _diff;
        private string? _hashedDir;
        private Dictionary<string, string>? _hashes;

        public NewEraToolsWindow()
        {
            InitializeComponent();

            string dat = MainWindow._datPath ?? "";
            ClientText.Text = MainWindow.appearances == null
                ? "no client loaded - load the 8.60 client first"
                : $"{dat}  ({MainWindow.appearances.Object.Count} items, {MainWindow.appearances.Outfit.Count} outfits)";

            string? thingsDir = dat.Length > 0 ? Path.GetDirectoryName(dat) : null;
            string? mehahThings = thingsDir != null && NewEraTools.IsFrozenClientTree(dat) ? NewEraTools.MehahThingsFor(thingsDir) : null;
            ThingsDirBox.Text = mehahThings ?? thingsDir ?? "";

            string? clientRoot = thingsDir == null ? null : ThingsDiffResult.ClientRootOf(mehahThings ?? thingsDir);
            PresetSettings? preset = MainWindow.GetCurrentPreset();
            string? server = !string.IsNullOrEmpty(preset?.ServerPath) && Directory.Exists(Path.Combine(preset.ServerPath, "data"))
                ? preset.ServerPath
                : ThingsDiffResult.GuessServerRoot(clientRoot);
            ServerRootBox.Text = server ?? "";

            if (clientRoot != null)
            {
                string manifest = Path.Combine(clientRoot, "dist", "wolfupdate", "manifest.json");
                if (File.Exists(manifest)) ManifestBox.Text = manifest;
            }
            if (dat.Length > 0 && File.Exists(dat + "-bak"))
                OriginalDatBox.Text = dat + "-bak";

            CategoryFilter.ItemsSource = new[] { "All" };
            CategoryFilter.SelectedIndex = 0;
            Closing += (_, _) => _checkCts?.Cancel();
        }

        private void SetStatus(string text) => StatusText.Text = text;

        // ---- pickers (start in the folder last used for the same purpose, keyed by a Globals GUID) ------------

        private async Task<IStorageFolder?> StartFolder(Guid key, string? fallback)
        {
            string? path = LastFolders.TryGetValue(key, out string? last) ? last : fallback;
            if (string.IsNullOrEmpty(path)) return null;
            if (File.Exists(path)) path = Path.GetDirectoryName(path);
            if (path == null || !Directory.Exists(path)) return null;
            try { return await StorageProvider.TryGetFolderFromPathAsync(path); }
            catch (Exception) { return null; }
        }

        private static void Remember(Guid key, string path)
        {
            string? dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (dir != null) LastFolders[key] = dir;
        }

        private async Task<string?> PickFolder(Guid key, string title, string? fallback)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                SuggestedStartLocation = await StartFolder(key, fallback),
            });
            if (folders.Count == 0) return null;
            string path = folders[0].Path.LocalPath;
            Remember(key, path);
            return path;
        }

        private async Task<List<string>> PickFiles(Guid key, string title, string? fallback, bool multiple, params FilePickerFileType[] types)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = multiple,
                FileTypeFilter = types,
                SuggestedStartLocation = await StartFolder(key, fallback),
            });
            var paths = files.Select(f => f.Path.LocalPath).ToList();
            if (paths.Count > 0) Remember(key, paths[0]);
            return paths;
        }

        private async Task<string?> PickSave(Guid key, string title, string suggestedName, string extension, string typeName)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggestedName,
                DefaultExtension = extension,
                FileTypeChoices = [new FilePickerFileType(typeName) { Patterns = ["*." + extension] }],
                SuggestedStartLocation = await StartFolder(key, MainWindow._datPath),
            });
            if (file == null) return null;
            string path = file.Path.LocalPath;
            Remember(key, path);
            return path;
        }

        private async Task CopyText(string text, string what)
        {
            try
            {
                if (Clipboard == null) { SetStatus("Clipboard unavailable."); return; }
                await Clipboard.SetTextAsync(text);
                SetStatus($"{what} copied to the clipboard.");
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message);
            }
        }

        private string? ServerRoot()
        {
            string root = (ServerRootBox.Text ?? "").Trim();
            return root.Length > 0 && Directory.Exists(root) ? root : null;
        }

        private async void BrowseServerRoot_Click(object? sender, RoutedEventArgs e)
        {
            string? path = await PickFolder(Globals.GUID_NewEraTools_ServerRoot, "Server root (the folder that contains data/)", ServerRootBox.Text);
            if (path == null) return;
            if (!Directory.Exists(Path.Combine(path, "data")) && Path.GetFileName(path) == "data")
                path = Path.GetDirectoryName(path) ?? path;
            ServerRootBox.Text = path;
        }

        // ---- 1. consistency -------------------------------------------------------------------------------

        private async void RunCheck_Click(object? sender, RoutedEventArgs e)
        {
            if (MainWindow.appearances == null)
            {
                ErrorManager.ShowWarning("Load the client (Tibia.dat/.spr) first.");
                return;
            }
            string? server = ServerRoot();
            if (server == null)
            {
                ErrorManager.ShowWarning("Pick the server root (the folder that contains data/).");
                return;
            }

            // snapshot on the UI thread: the editor may change the appearances while the check runs
            ThingsSnapshot things = ThingsSnapshot.From(MainWindow.appearances);
            var input = new ConsistencyInput { Things = things, ServerRoot = server };
            _checkCts = new CancellationTokenSource();
            var ct = _checkCts.Token;
            var progress = new Progress<(string stage, double fraction)>(p =>
            {
                CheckProgress.Value = p.fraction;
                CheckStage.Text = p.stage;
            });
            RunCheckButton.IsEnabled = false;
            CancelCheckButton.IsEnabled = true;
            var started = DateTime.Now;
            try
            {
                _findings = await Task.Run(() => ConsistencyChecker.Run(input, progress, ct), ct);
                var categories = new List<string> { "All" };
                categories.AddRange(_findings.Select(f => f.Category).Distinct().OrderBy(c => c, StringComparer.Ordinal));
                CategoryFilter.ItemsSource = categories;
                CategoryFilter.SelectedIndex = 0;
                ApplyFilter();
                int errors = _findings.Count(f => f.Severity == Severity.Error), warnings = _findings.Count(f => f.Severity == Severity.Warning);
                string summary = $"Consistency check: {errors} error(s), {warnings} warning(s), {_findings.Count - errors - warnings} info in {(DateTime.Now - started).TotalSeconds:0.0} s";
                SetStatus(summary);
                MainWindow.Log(summary + $" ({server})", errors > 0 ? "Error" : warnings > 0 ? "Warning" : "Info");
            }
            catch (OperationCanceledException)
            {
                SetStatus("Consistency check cancelled.");
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Consistency check failed:\n{ex.Message}");
                MainWindow.Log($"NewEra consistency check failed: {ex}", "Error");
            }
            finally
            {
                RunCheckButton.IsEnabled = true;
                CancelCheckButton.IsEnabled = false;
                CheckStage.Text = "";
                CheckProgress.Value = 0;
                _checkCts?.Dispose();
                _checkCts = null;
            }
        }

        private void CancelCheck_Click(object? sender, RoutedEventArgs e) => _checkCts?.Cancel();

        private void Filter_Changed(object? sender, SelectionChangedEventArgs e) => ApplyFilter();

        private void TextFilter_Changed(object? sender, TextChangedEventArgs e) => ApplyFilter();

        private List<Finding> FilteredFindings()
        {
            IEnumerable<Finding> q = _findings;
            switch (SeverityFilter?.SelectedIndex ?? 0)
            {
                case 1: q = q.Where(f => f.Severity == Severity.Error); break;
                case 2: q = q.Where(f => f.Severity == Severity.Warning); break;
                case 3: q = q.Where(f => f.Severity == Severity.Info); break;
                case 4: q = q.Where(f => f.Severity != Severity.Info); break;
            }
            if (CategoryFilter?.SelectedItem is string cat && cat != "All")
                q = q.Where(f => f.Category == cat);
            string text = (TextFilter?.Text ?? "").Trim();
            if (text.Length > 0)
                q = uint.TryParse(text, out uint id)
                    ? q.Where(f => f.Id == id || f.Message.Contains(text, StringComparison.OrdinalIgnoreCase))
                    : q.Where(f => f.Message.Contains(text, StringComparison.OrdinalIgnoreCase) || f.Category.Contains(text, StringComparison.OrdinalIgnoreCase));
            return q.ToList();
        }

        private void ApplyFilter()
        {
            if (FindingList == null) return;
            var list = FilteredFindings();
            FindingList.ItemsSource = list;
            FindingSummary.Text = $"{list.Count} of {_findings.Count} finding(s) shown";
        }

        private async void CopyFindings_Click(object? sender, RoutedEventArgs e)
        {
            var selected = FindingList.SelectedItems?.OfType<Finding>().ToList() ?? [];
            var rows = selected.Count > 0 ? selected : FilteredFindings();
            await CopyText(NewEraTools.ToTsv(rows), $"{rows.Count} finding(s)");
        }

        private async void SaveFindings_Click(object? sender, RoutedEventArgs e)
        {
            if (_findings.Count == 0) { SetStatus("Run the check first."); return; }
            string? path = await PickSave(Globals.GUID_NewEraTools_SaveReport, "Save consistency report", $"newera-consistency-{DateTime.Now:yyyyMMdd-HHmm}.tsv", "tsv", "TSV");
            if (path == null) return;
            try
            {
                var rows = FilteredFindings();
                await File.WriteAllTextAsync(path, NewEraTools.ToTsv(rows));
                SetStatus($"Saved {rows.Count} finding(s) to {path}");
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Could not save {path}:\n{ex.Message}");
            }
        }

        // ---- 2. things guard ------------------------------------------------------------------------------

        private async void HashThings_Click(object? sender, RoutedEventArgs e)
        {
            string dat = MainWindow._datPath ?? "";
            if (dat.Length == 0 || !File.Exists(dat))
            {
                ErrorManager.ShowWarning("Load the client (Tibia.dat/.spr) first.");
                return;
            }
            string dir = Path.GetDirectoryName(Path.GetFullPath(dat))!;
            HashButton.IsEnabled = false;
            SetStatus("Hashing the things (the spr is ~400 MB)...");
            try
            {
                string spr = string.IsNullOrEmpty(MainWindow._sprPath) ? Path.Combine(dir, "Tibia.spr") : MainWindow._sprPath;
                var files = new List<(string name, string path)> { ("Tibia.dat", dat), ("Tibia.spr", spr), ("Tibia.otfi", Path.Combine(dir, "Tibia.otfi")) };
                var hashes = await Task.Run(() => files.Where(f => File.Exists(f.path)).ToDictionary(f => f.name, f => NewEraTools.Sha256File(f.path)));
                _hashes = hashes;
                _hashedDir = dir;

                var lines = new List<string> { $"Things directory: {dir}", "" };
                foreach (var (name, path) in files)
                {
                    lines.Add(hashes.TryGetValue(name, out string? h)
                        ? $"{h}  {name}  ({new FileInfo(path).Length:N0} bytes, {File.GetLastWriteTime(path):yyyy-MM-dd HH:mm})"
                        : $"{"(missing)",-64}  {name}");
                }
                lines.Add("");
                if (NewEraTools.IsFrozenClientTree(dat))
                {
                    lines.Add("WARNING: these things are in NewEra/client/, the FROZEN OTCv8 tree. The live client is client-mehah;");
                    lines.Add("         edits here never reach the players and the regeneration tools must not read this tree.");
                    string? mehah = NewEraTools.MehahThingsFor(dir);
                    if (mehah != null) lines.Add($"         Load {mehah} instead.");
                }
                else if (dir.Contains("client-mehah", StringComparison.OrdinalIgnoreCase))
                    lines.Add("OK: client-mehah things (the live client).");
                var otfi = NewEraTools.ReadOtfi(Path.Combine(dir, "Tibia.otfi"));
                lines.Add($"Tibia.otfi: extended {otfi["extended"]}, frame-durations {otfi["frame-durations"]}, frame-groups {otfi["frame-groups"]}");
                lines.Add("The hashes are of the files on disk: edits in the editor count only after Compile.");
                GuardText.Text = string.Join("\n", lines);
                SetStatus("Things hashed.");
                if ((ManifestBox.Text ?? "").Trim().Length > 0)
                    await CompareManifest();
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Could not hash the things:\n{ex.Message}");
            }
            finally
            {
                HashButton.IsEnabled = true;
            }
        }

        private async void BrowseManifest_Click(object? sender, RoutedEventArgs e)
        {
            var paths = await PickFiles(Globals.GUID_NewEraTools_Manifest, "Update channel manifest", ManifestBox.Text, false,
                new FilePickerFileType("Manifest") { Patterns = ["*.json", "*.json.gz"] });
            if (paths.Count > 0) ManifestBox.Text = paths[0];
        }

        private async void CompareManifest_Click(object? sender, RoutedEventArgs e) => await CompareManifest();

        private async Task CompareManifest()
        {
            string manifest = (ManifestBox.Text ?? "").Trim();
            if (!File.Exists(manifest)) { ErrorManager.ShowWarning("Pick the channel manifest (dist/wolfupdate/manifest.json or builds/N.json)."); return; }
            string dat = MainWindow._datPath ?? "";
            if (dat.Length == 0) { ErrorManager.ShowWarning("Load the client first."); return; }
            string dir = Path.GetDirectoryName(Path.GetFullPath(dat))!;
            CompareManifestButton.IsEnabled = false;
            try
            {
                var cached = _hashedDir == dir ? _hashes : null;
                var (rows, build) = await Task.Run(() => (NewEraTools.CompareWithManifest(dir, manifest, cached), NewEraTools.ManifestBuild(manifest)));
                var lines = new List<string> { "", $"Manifest {manifest} (build {build?.ToString() ?? "?"}):" };
                foreach (var r in rows)
                    lines.Add($"  {r.Name,-11} {r.Status,-16} manifest {r.Expected ?? "-"}");
                bool bad = rows.Any(r => r.Status is "DIFFERENT" or "missing locally");
                lines.Add(bad
                    ? $"  REFUSING (check_things_manifest.py): the things differ from build {build}; the updater trusts the manifest and would never replace them. Build a new channel build with these things."
                    : $"  sprites in {dir} match build {build}.");
                GuardText.Text = (GuardText.Text ?? "") + string.Join("\n", lines);
                SetStatus(bad ? "Things differ from the channel manifest." : "Things match the channel manifest.");
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Could not read the manifest:\n{ex.Message}");
            }
            finally
            {
                CompareManifestButton.IsEnabled = true;
            }
        }

        // ---- 3. things diff -------------------------------------------------------------------------------

        private async void BrowseOriginalDat_Click(object? sender, RoutedEventArgs e)
        {
            var paths = await PickFiles(Globals.GUID_NewEraTools_OriginalDat, "Original Tibia.dat", OriginalDatBox.Text is { Length: > 0 } t ? t : MainWindow._datPath, false,
                new FilePickerFileType("Tibia.dat") { Patterns = ["*.dat", "*.dat-bak", "*"] });
            if (paths.Count > 0) OriginalDatBox.Text = paths[0];
        }

        private async void ComputeDiff_Click(object? sender, RoutedEventArgs e)
        {
            if (MainWindow.appearances == null || !MainWindow.LegacyClient)
            {
                ErrorManager.ShowWarning("Load a legacy (Tibia.dat/.spr) client first.");
                return;
            }
            string original = (OriginalDatBox.Text ?? "").Trim();
            if (original.Length == 0)
            {
                await PickOriginalDat();
                original = (OriginalDatBox.Text ?? "").Trim();
                if (original.Length == 0) return;
            }
            if (!File.Exists(original)) { ErrorManager.ShowWarning($"{original} not found."); return; }
            PresetSettings preset = MainWindow.GetCurrentPreset() ?? new();
            int structure = preset.Version;
            Appearances current = MainWindow.appearances.Clone(); // the editor may keep editing while we compare
            List<uint> modified;
            lock (MainWindow.MainSprStorage.Modified)
                modified = MainWindow.MainSprStorage.Modified.ToList();
            string thingsDir = (ThingsDirBox.Text ?? "").Trim();
            if (thingsDir.Length == 0) thingsDir = Path.GetDirectoryName(MainWindow._datPath) ?? "";
            string? server = ServerRoot();

            DiffButton.IsEnabled = false;
            SetStatus("Comparing...");
            try
            {
                _diff = await Task.Run(() => ThingsDiff.Compute(current, original, structure, preset, modified));
                ApplyDiffFilter();
                DiffSummary.Text = _diff.Summary().Replace("\n", "   ·   ");
                ChecklistText.Text = ThingsDiffResult.ChecklistText(_diff.Checklist(thingsDir, server));
                SetStatus($"{_diff.Changes.Count} change(s) against {original}");
                MainWindow.Log($"Things diff against {original}: {_diff.Summary().Replace("\n", "; ")}");
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Could not compare with {original}:\n{ex.Message}");
            }
            finally
            {
                DiffButton.IsEnabled = true;
            }
        }

        private async Task PickOriginalDat()
        {
            ErrorManager.ShowInfo("There is no Tibia.dat-bak next to the loaded dat (it is written by the first Compile). " +
                                  "Pick the original dat to compare with; until you compile, the Tibia.dat on disk is the original.");
            var paths = await PickFiles(Globals.GUID_NewEraTools_OriginalDat, "Original Tibia.dat", MainWindow._datPath, false,
                new FilePickerFileType("Tibia.dat") { Patterns = ["*.dat", "*.dat-bak", "*"] });
            if (paths.Count > 0) OriginalDatBox.Text = paths[0];
        }

        private void DiffFilter_Changed(object? sender, SelectionChangedEventArgs e) => ApplyDiffFilter();

        private List<ThingChange> FilteredChanges()
        {
            if (_diff == null) return [];
            IEnumerable<ThingChange> q = _diff.Changes;
            if (DiffCategoryFilter?.SelectedIndex is int c and > 0)
                q = q.Where(x => (int)x.Category == c - 1);
            if (DiffKindFilter?.SelectedItem is ComboBoxItem { Content: string kind } && kind != "All")
                q = q.Where(x => x.Kind == kind);
            return q.ToList();
        }

        private void ApplyDiffFilter()
        {
            if (DiffList == null) return;
            DiffList.ItemsSource = FilteredChanges();
        }

        private async void ExportDiffJson_Click(object? sender, RoutedEventArgs e) => await ExportDiff(json: true);

        private async void ExportDiffTsv_Click(object? sender, RoutedEventArgs e) => await ExportDiff(json: false);

        private async Task ExportDiff(bool json)
        {
            if (_diff == null) { SetStatus("Compare first."); return; }
            string ext = json ? "json" : "tsv";
            string? path = await PickSave(Globals.GUID_NewEraTools_SaveDiff, "Export things diff", $"things-diff-{DateTime.Now:yyyyMMdd-HHmm}.{ext}", ext, ext.ToUpperInvariant());
            if (path == null) return;
            try
            {
                await File.WriteAllTextAsync(path, json ? _diff.ToJson() : _diff.ToTsv());
                SetStatus($"Saved {path}");
            }
            catch (Exception ex)
            {
                ErrorManager.ShowError($"Could not save {path}:\n{ex.Message}");
            }
        }

        private async void CopyChecklist_Click(object? sender, RoutedEventArgs e) => await CopyText(ChecklistText.Text ?? "", "Checklist");

        // ---- 4. export for approval -----------------------------------------------------------------------

        private async void AddServerFiles_Click(object? sender, RoutedEventArgs e)
        {
            string? server = ServerRoot();
            var paths = await PickFiles(Globals.GUID_NewEraTools_ServerFiles, "Changed server files", server == null ? null : Path.Combine(server, "data", "monster"), true,
                new FilePickerFileType("Server files") { Patterns = NewEraTools.AllowedExtensions.Select(x => "*" + x).ToArray() });
            if (paths.Count == 0) return;
            var list = ServerFileList.Items.OfType<string>().ToList();
            foreach (string p in paths)
                if (!list.Contains(p)) list.Add(p);
            ServerFileList.ItemsSource = list;
        }

        private void RemoveServerFiles_Click(object? sender, RoutedEventArgs e)
        {
            var selected = ServerFileList.SelectedItems?.OfType<string>().ToHashSet() ?? [];
            ServerFileList.ItemsSource = ServerFileList.Items.OfType<string>().Where(p => !selected.Contains(p)).ToList();
        }

        private void ClearServerFiles_Click(object? sender, RoutedEventArgs e) => ServerFileList.ItemsSource = new List<string>();

        private async void BrowseThingsDir_Click(object? sender, RoutedEventArgs e)
        {
            string? path = await PickFolder(Globals.GUID_NewEraTools_ThingsDir, "Client things (data/things/860)", ThingsDirBox.Text);
            if (path != null) ThingsDirBox.Text = path;
        }

        private async void BrowseExportDir_Click(object? sender, RoutedEventArgs e)
        {
            string? path = await PickFolder(Globals.GUID_NewEraTools_ExportDir, "Staging folder (new or empty)", ExportDirBox.Text);
            if (path != null) ExportDirBox.Text = path;
        }

        private async void Export_Click(object? sender, RoutedEventArgs e)
        {
            string? server = ServerRoot();
            var files = new List<string>();
            if (server != null)
            {
                if (ExportOtb.IsChecked == true) files.Add(Path.Combine(server, "data", "items", "items.otb"));
                if (ExportItemsXml.IsChecked == true) files.Add(Path.Combine(server, "data", "items", "items.xml"));
                if (ExportOutfitsXml.IsChecked == true) files.Add(Path.Combine(server, "data", "XML", "outfits.xml"));
            }
            files.AddRange(ServerFileList.Items.OfType<string>());
            if (files.Count > 0 && server == null)
            {
                ErrorManager.ShowWarning("Pick the server root (the folder that contains data/).");
                return;
            }
            string? things = ExportClient.IsChecked == true ? (ThingsDirBox.Text ?? "").Trim() : null;
            if (things is { Length: 0 }) { ErrorManager.ShowWarning("Pick the client things folder."); return; }
            string outDir = (ExportDirBox.Text ?? "").Trim();
            if (outDir.Length == 0) { ErrorManager.ShowWarning("Pick a new or empty staging folder."); return; }

            var request = new NewEraTools.ExportRequest { ServerRoot = server ?? "", ServerFiles = files, ThingsDir = things, OutputDir = outDir };
            ExportButton.IsEnabled = false;
            ExportLog.Text = "Exporting...";
            var progress = new Progress<string>(p => SetStatus($"Copied {p}"));
            try
            {
                var result = await Task.Run(() => NewEraTools.ExportForApproval(request, progress));
                if (!result.Ok)
                {
                    ExportLog.Text = "Nothing was written:\n" + string.Join("\n", result.Refused.Select(r => "  " + r));
                    SetStatus("Export refused.");
                    return;
                }
                string readme = await File.ReadAllTextAsync(Path.Combine(outDir, "README.txt"));
                ExportLog.Text = $"{result.Copied} file(s) written to {outDir}\n\nSHA256SUMS:\n{string.Join("\n", result.Lines)}\n\n{readme}";
                SetStatus($"Exported {result.Copied} file(s) to {outDir}");
                MainWindow.Log($"NewEra export for approval: {result.Copied} file(s) in {outDir}");
            }
            catch (Exception ex)
            {
                ExportLog.Text = $"Export failed: {ex.Message}";
                ErrorManager.ShowError($"Export failed:\n{ex.Message}");
            }
            finally
            {
                ExportButton.IsEnabled = true;
            }
        }
    }
}
