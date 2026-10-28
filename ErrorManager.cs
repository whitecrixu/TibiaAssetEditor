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
    private static Window? GetOwnerWindow()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return null;

        // Prefer the window the user is actually looking at. DatEditor/SprEditor open as
        // separate Window instances while MainWindow is hidden, and ShowWindowDialogAsync
        // throws "Cannot show window with non-visible owner" if we pass the hidden root.
        Window? best = null;
        foreach (var w in desktop.Windows)
        {
            if (!w.IsVisible) continue;
            if (w.IsActive) return w;
            best = w;
        }
        return best ?? desktop.MainWindow;
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
            var window = GetOwnerWindow();
            var result = window != null
                ? await box.ShowWindowDialogAsync(window)
                : await box.ShowAsync();
            return result == ButtonResult.Yes;
        }
        catch (Exception ex)
        {
            MainWindow.Log($"Confirmation dialog failed: {ex.GetType().Name}: {ex.Message}", "Error");
            return false;
        }
    }

    private static async Task ShowMessageAsync(string title, string message, Icon icon)
    {
        try
        {
            var box = MessageBoxManager
                .GetMessageBoxStandard(title, message, ButtonEnum.Ok, icon);

            var window = GetOwnerWindow();
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
