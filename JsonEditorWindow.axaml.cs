using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Google.Protobuf;
using Tibia.Protobuf.Appearances;

namespace Assets_Editor;

public partial class JsonEditorWindow : Window
{
    public Appearance? Result { get; private set; }

    public JsonEditorWindow()
    {
        InitializeComponent();
    }

    /// <summary>Read-only text viewer (for reports, diffs, etc.)</summary>
    public JsonEditorWindow(string text, string header) : this()
    {
        HeaderText.Text = header;
        JsonBox.Text = text;
        JsonBox.IsReadOnly = true;
    }

    public JsonEditorWindow(Appearance appearance, string header) : this()
    {
        HeaderText.Text = header;
        try
        {
            string raw = JsonFormatter.Default.Format(appearance);
            // Google.Protobuf 3.21 can't indent — pretty-print through Newtonsoft for readability.
            var parsed = Newtonsoft.Json.Linq.JToken.Parse(raw);
            JsonBox.Text = parsed.ToString(Newtonsoft.Json.Formatting.Indented);
        }
        catch (Exception ex)
        {
            JsonBox.Text = $"// Failed to serialize: {ex.Message}";
        }
    }

    private void Apply_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var parsed = JsonParser.Default.Parse<Appearance>(JsonBox.Text ?? string.Empty);
            Result = parsed;
            Close(true);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Parse error: {ex.Message}";
            StatusText.Foreground = Avalonia.Media.Brushes.OrangeRed;
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
