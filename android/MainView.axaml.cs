using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Assets_Editor;

/// <summary>Single-view shell: an app bar and a stack of pages (Android has no extra windows).</summary>
public partial class MainView : UserControl
{
    public static MainView Current { get; private set; } = null!;

    private sealed record Page(Control View, string Title, string? Subtitle, Control? Action);
    private readonly Stack<Page> _pages = new();

    public MainView()
    {
        InitializeComponent();
        Current = this;
        Push(new HomePage(), "Tibia MultiTool", "Android alpha · 8.60 things, items.otb, items.xml");
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var top = TopLevel.GetTopLevel(this);
        if (top == null)
            return;

        // the system back gesture walks the page stack before it leaves the app
        top.BackRequested += (_, args) =>
        {
            if (_pages.Count > 1)
            {
                args.Handled = true;
                Pop();
            }
        };

        // Android 15 draws apps edge to edge: keep the content out of the status and navigation bars
        if (top.InsetsManager is { } insets)
        {
            Padding = insets.SafeAreaPadding;
            insets.SafeAreaChanged += (_, args) => Padding = args.SafeAreaPadding;
        }
    }

    public void Push(Control view, string title, string? subtitle = null, Control? action = null)
    {
        _pages.Push(new Page(view, title, subtitle, action));
        Show();
    }

    public void Pop()
    {
        if (_pages.Count <= 1)
            return;
        Page leaving = _pages.Pop();
        (leaving.View as IPage)?.OnLeave();
        Show();
        (_pages.Peek().View as IPage)?.OnReturn();
    }

    public void SetHeader(string title, string? subtitle)
    {
        TitleText.Text = title;
        SubtitleText.Text = subtitle;
        SubtitleText.IsVisible = !string.IsNullOrEmpty(subtitle);
    }

    private void Show()
    {
        Page page = _pages.Peek();
        PageHost.Content = page.View;
        ActionHost.Content = page.Action;
        SetHeader(page.Title, page.Subtitle);
        BackButton.IsVisible = _pages.Count > 1;
    }

    private void Back_Click(object? sender, RoutedEventArgs e) => Pop();

    // ---- busy overlay ----

    public void ShowBusy(string text)
    {
        BusyText.Text = text;
        BusyDetail.Text = "";
        BusyProgress.IsIndeterminate = true;
        BusyOverlay.IsVisible = true;
    }

    public void ReportBusy(int percent, string detail)
    {
        BusyProgress.IsIndeterminate = false;
        BusyProgress.Value = percent;
        BusyDetail.Text = detail;
    }

    public void HideBusy() => BusyOverlay.IsVisible = false;

    /// <summary>Runs work off the UI thread behind the busy overlay; errors go to the log and are returned.</summary>
    public async Task<Exception?> RunBusy(string text, Func<Task> work)
    {
        ShowBusy(text);
        try
        {
            await Task.Run(work);
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Add("Error: " + ex.Message);
            return ex;
        }
        finally
        {
            HideBusy();
        }
    }

    public static void OnUi(Action action) => Dispatcher.UIThread.Post(action);
}

/// <summary>Pages that refresh when they are shown again or clean up when they are closed.</summary>
public interface IPage
{
    void OnReturn() { }
    void OnLeave() { }
}
