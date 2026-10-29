using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Assets_Editor
{
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();
            Version? version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (version != null)
                VersionText.Text = $"Version {version.ToString(3)}";
        }

        private async void Releases_Click(object? sender, RoutedEventArgs e)
        {
            await Launcher.LaunchUriAsync(new Uri("https://github.com/whitecrixu/TibiaAssetEditor/releases"));
        }
    }
}
