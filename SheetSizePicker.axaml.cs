using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Assets_Editor;

public partial class SheetSizePicker : Window
{
    public int SelectedSpriteType { get; private set; } = 0;

    public SheetSizePicker()
    {
        InitializeComponent();
        BuildItems();
    }

    private void BuildItems()
    {
        var sizes = DatEditor.SpriteSizes;
        for (int i = 0; i < sizes.Count; i++)
        {
            int w = sizes[i].Width;
            int h = sizes[i].Height;
            int tx = w / 32;
            int ty = h / 32;
            // The first 4 entries (1x1, 1x2, 2x1, 2x2) are native client sizes;
            // everything else is OTClient-only.
            string suffix = i <= 3 ? $"({tx}x{ty})" : $"({tx}x{ty} , OTClient)";
            SizeBox.Items.Add($"Width: {w} | Height: {h}  {suffix}");
        }
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        SelectedSpriteType = SizeBox.SelectedIndex < 0 ? 0 : SizeBox.SelectedIndex;
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
