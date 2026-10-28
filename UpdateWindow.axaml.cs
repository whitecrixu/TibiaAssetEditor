using System;
using System.IO;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Assets_Editor
{
    /// <summary>Shows a newer GitHub release and installs it (see <see cref="UpdateService"/>).</summary>
    public partial class UpdateWindow : Window
    {
        private readonly UpdateInfo? _info;
        private CancellationTokenSource? _download;

        public UpdateWindow()
        {
            InitializeComponent();
        }

        public UpdateWindow(UpdateInfo info) : this()
        {
            _info = info;
            Title = $"Update available - {info.Tag}";
            HeadlineText.Text = $"{info.Title}";
            VersionText.Text = $"Installed: {UpdateService.CurrentVersion}   Available: {info.Version}";
            NotesBox.Text = string.IsNullOrWhiteSpace(info.Notes) ? "(no release notes)" : info.Notes.Replace("\r\n", "\n");

            if (!info.CanInstall)
            {
                UpdateButton.IsEnabled = false;
                StatusText.Text = !UpdateService.CanSelfUpdate
                    ? "This is a development build - download the new version from GitHub."
                    : info.DownloadUrl == null
                        ? "There is no package for this system in the release - download it from GitHub."
                        : "GitHub published no checksum for the package, so it is not installed automatically - download it from GitHub.";
            }
            else if (!UpdateService.IsInstallDirWritable(AppPaths.BaseDirectory))
            {
                UpdateButton.IsEnabled = false;
                StatusText.Text = $"The program folder {AppPaths.BaseDirectory} is not writable - download the new version from GitHub.";
            }
            else
            {
                StatusText.Text = $"Update now downloads {info.AssetName} ({info.Size / (1024 * 1024)} MB), checks it and restarts the editor. Save your work first.";
            }
        }

        private void GitHub_Click(object? sender, RoutedEventArgs e)
        {
            UpdateService.OpenInBrowser(_info?.PageUrl ?? UpdateService.ReleasesPage);
        }

        private void Skip_Click(object? sender, RoutedEventArgs e)
        {
            if (_info != null)
            {
                MainWindow.GetSettings().SkippedUpdateVersion = _info.Version.ToString();
                MainWindow.PersistSettings();
            }
            Close();
        }

        private void Later_Click(object? sender, RoutedEventArgs e)
        {
            _download?.Cancel();
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            _download?.Cancel();
            base.OnClosed(e);
        }

        private async void Update_Click(object? sender, RoutedEventArgs e)
        {
            if (_info == null || !_info.CanInstall) return;

            UpdateButton.IsEnabled = SkipButton.IsEnabled = false;
            DownloadProgress.IsVisible = true;
            DownloadProgress.Value = 0;
            StatusText.Text = "Downloading...";
            _download = new CancellationTokenSource();
            try
            {
                var progress = new Progress<double>(p => DownloadProgress.Value = p * 100);
                string archive = await UpdateService.DownloadAsync(_info, progress, _download.Token);

                StatusText.Text = "Checksum OK - preparing...";
                string exeName = Path.GetFileName(Environment.ProcessPath!);
                await System.Threading.Tasks.Task.Run(() =>
                {
                    string files = UpdateService.Extract(archive, exeName);
                    UpdateService.LaunchInstaller(files, AppPaths.BaseDirectory);
                });

                // the installer waits for this process to end, then swaps the files and starts the new version
                MainWindow.Log($"Installing {_info.Tag}; the editor restarts");
                StatusText.Text = $"Closing the editor to install {_info.Tag}...";
                if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                    && !desktop.TryShutdown())
                {
                    // an editor asked about unsaved changes and stayed open
                    StatusText.Text = $"{_info.Tag} installs as soon as the editor is closed.";
                    DownloadProgress.IsVisible = false;
                }
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Download cancelled.";
                UpdateButton.IsEnabled = SkipButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"Update to {_info.Tag} failed: {ex}", "Error");
                StatusText.Text = $"Update failed: {ex.Message}\nNothing was changed - you can download the new version from GitHub.";
                DownloadProgress.IsVisible = false;
                UpdateButton.IsEnabled = SkipButton.IsEnabled = true;
            }
        }
    }
}
