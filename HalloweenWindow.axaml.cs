using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Assets_Editor
{
    public partial class HalloweenWindow : Window
    {
        public HalloweenWindow()
        {
            InitializeComponent();
        }

        public bool HideThisYear => DontShowAgain.IsChecked == true;

        private void Close_Click(object? sender, RoutedEventArgs e) => Close();
    }
}
