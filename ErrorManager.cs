using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;

namespace Assets_Editor;

public static class ErrorManager
{
    private static Window? GetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow;
        return null;
    }

    public static void ShowInfo(string message)
    {
        ShowMessageAsync("Information", message, Icon.Info).ConfigureAwait(false);
    }

    public static void ShowWarning(string message)
    {
        ShowMessageAsync("Warning", message, Icon.Warning).ConfigureAwait(false);
    }

    public static void ShowError(string message)
    {
        ShowMessageAsync("Error", message, Icon.Error).ConfigureAwait(false);
    }

    // Shows a Yes/No modal and returns true only if the user confirmed.
    public static async Task<bool> ConfirmAsync(string title, string message)
    {
        try
        {
            var box = MessageBoxManager
                .GetMessageBoxStandard(title, message, ButtonEnum.YesNo, Icon.Warning);
            var window = GetMainWindow();
            var result = window != null
                ? await box.ShowWindowDialogAsync(window)
                : await box.ShowAsync();
            return result == ButtonResult.Yes;
        }
        catch
        {
            return false;
        }
    }

    private static async Task ShowMessageAsync(string title, string message, Icon icon)
    {
        try
        {
            var box = MessageBoxManager
                .GetMessageBoxStandard(title, message, ButtonEnum.Ok, icon);

            var window = GetMainWindow();
            if (window != null)
                await box.ShowWindowDialogAsync(window);
            else
                await box.ShowAsync();
        }
        catch
        {
            // Fallback: just write to console if UI is not available
            Console.WriteLine($"[{title}] {message}");
        }
    }
}
