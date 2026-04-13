using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;

namespace Assets_Editor;

public class ClipboardManager
{
    public static async void CopyText(string text, string thingName, Action<string>? notifyCallback = null, long seconds = 2)
    {
        try
        {
            var clipboard = GetClipboard();
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(text);
                notifyCallback?.Invoke($"{thingName} copied to clipboard.");
            }
            else
            {
                notifyCallback?.Invoke("Clipboard unavailable.");
            }
        }
        catch (Exception e)
        {
            notifyCallback?.Invoke(e.Message);
        }
    }

    private static IClipboard? GetClipboard()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow?.Clipboard;
        }
        return null;
    }
}
