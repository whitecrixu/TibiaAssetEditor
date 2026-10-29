using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Google.Protobuf.Collections;
using Tibia.Protobuf.Appearances;

namespace Assets_Editor;

/// <summary>A list row; the thumbnail is rendered the first time the row is shown.</summary>
public sealed class ThingRow : INotifyPropertyChanged
{
    // a few renders at a time keep scrolling smooth on a phone
    private static readonly SemaphoreSlim RenderSlots = new(3);

    public Appearance Appearance { get; }
    public string Title { get; private set; }
    public string Detail { get; private set; }

    private Bitmap? _image;
    private bool _requested;

    public ThingRow(Appearance appearance)
    {
        Appearance = appearance;
        Title = Detail = "";
        UpdateText();
    }

    public void UpdateText()
    {
        uint id = Appearance.Id;
        bool item = Appearance.AppearanceType == APPEARANCE_TYPE.AppearanceObject;
        string? name = item ? Workspace.ItemName(id) : null;
        Title = name != null ? $"{id} · {name}" : id.ToString();

        var info = Appearance.FrameGroup.Count > 0 ? Appearance.FrameGroup[0].SpriteInfo : null;
        string size = info != null ? $"{info.PatternWidth}×{info.PatternHeight}" : "empty";
        string frames = info != null && info.PatternFrames > 1 ? $" · {info.PatternFrames} frames" : "";
        string server = item && Workspace.ServerItemFor(id) is { } s ? $" · {s.Type}" + (s.ServerId != id ? $" sid {s.ServerId}" : "") : "";
        Detail = size + frames + server;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Detail)));
    }

    public Bitmap? Image
    {
        get
        {
            if (!_requested)
            {
                _requested = true;
                _ = Render();
            }
            return _image;
        }
    }

    public void Invalidate()
    {
        _requested = false;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image)));
    }

    private async Task Render()
    {
        await RenderSlots.WaitAsync();
        try
        {
            Bitmap? bitmap = await Task.Run(() => RenderBitmap(Appearance));
            Dispatcher.UIThread.Post(() =>
            {
                _image = bitmap;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image)));
            });
        }
        finally
        {
            RenderSlots.Release();
        }
    }

    public static Bitmap? RenderBitmap(Appearance appearance, int frame = 0)
    {
        if (Workspace.Sprites == null)
            return null;
        try
        {
            using MemoryStream png = LegacyAppearance.GetObjectImage(appearance, Workspace.Sprites, frame);
            return png.Length == 0 ? null : new Bitmap(png);
        }
        catch
        {
            return null;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class ThingsPage : UserControl, IPage
{
    private readonly Dictionary<APPEARANCE_TYPE, List<ThingRow>> _rows = new();
    private APPEARANCE_TYPE _kind = APPEARANCE_TYPE.AppearanceObject;
    private CancellationTokenSource? _searchDelay;
    private ThingRow? _opened;

    public ThingsPage()
    {
        InitializeComponent();
        SearchBox.TextChanged += (_, _) => ScheduleFilter();
        ApplyFilter();
    }

    public void OnReturn()
    {
        // the item page may have changed names, flags or the picture
        _opened?.UpdateText();
        _opened?.Invalidate();
        List.SelectedItem = null;
    }

    private List<ThingRow> Rows(APPEARANCE_TYPE kind)
    {
        if (_rows.TryGetValue(kind, out var rows))
            return rows;
        Appearances things = Workspace.Things!;
        RepeatedField<Appearance> source = kind switch
        {
            APPEARANCE_TYPE.AppearanceOutfit => things.Outfit,
            APPEARANCE_TYPE.AppearanceEffect => things.Effect,
            APPEARANCE_TYPE.AppearanceMissile => things.Missile,
            _ => things.Object,
        };
        // the dat does not store the category; the list a thing lives in decides it
        foreach (Appearance a in source)
            a.AppearanceType = kind;
        rows = source.Select(a => new ThingRow(a)).ToList();
        _rows[kind] = rows;
        return rows;
    }

    private void Kind_Click(object? sender, RoutedEventArgs e)
    {
        _kind = sender == OutfitsTab ? APPEARANCE_TYPE.AppearanceOutfit
              : sender == EffectsTab ? APPEARANCE_TYPE.AppearanceEffect
              : sender == MissilesTab ? APPEARANCE_TYPE.AppearanceMissile
              : APPEARANCE_TYPE.AppearanceObject;
        ApplyFilter();
    }

    private async void ScheduleFilter()
    {
        _searchDelay?.Cancel();
        var cts = _searchDelay = new CancellationTokenSource();
        try
        {
            await Task.Delay(250, cts.Token);
            ApplyFilter();
        }
        catch (TaskCanceledException)
        {
        }
    }

    private void ApplyFilter()
    {
        if (Workspace.Things == null)
            return;
        List<ThingRow> all = Rows(_kind);
        string query = (SearchBox.Text ?? "").Trim();
        IEnumerable<ThingRow> shown = all;

        if (query.Length > 0)
        {
            string[] range = query.Split('-', 2, StringSplitOptions.TrimEntries);
            if (range.Length == 2 && uint.TryParse(range[0], out uint from) && uint.TryParse(range[1], out uint to))
                shown = all.Where(r => r.Appearance.Id >= from && r.Appearance.Id <= to);
            else if (uint.TryParse(query, out uint id))
                shown = all.Where(r => r.Appearance.Id == id || r.Appearance.Id.ToString().StartsWith(query, StringComparison.Ordinal));
            else
                shown = all.Where(r => r.Title.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var list = shown.ToList();
        List.ItemsSource = list;
        CountText.Text = list.Count == all.Count ? $"{all.Count:N0} entries" : $"{list.Count:N0} of {all.Count:N0} entries";
    }

    private void List_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is not ThingRow row)
            return;
        _opened = row;
        var page = new ThingPage(row.Appearance);
        MainView.Current.Push(page, page.PageTitle, page.PageSubtitle);
    }
}
