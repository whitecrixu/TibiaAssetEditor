using System;
using Avalonia.Controls;

namespace Assets_Editor
{
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();
            Version? version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (version != null)
                VersionText.Text = $"V{version.Major}.{version.Minor}";
        }
    }
}
