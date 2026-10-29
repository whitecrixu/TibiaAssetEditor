using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Tibia.Protobuf.Appearances;
using static Assets_Editor.OTB;

namespace Assets_Editor;

/// <summary>
/// One thing: preview, the Tibia.dat flags the loaded structure has, and for items the items.otb entry and
/// the items.xml entry. Edits apply to the loaded data at once and mark the file as changed; nothing is
/// written before "Save changes" on the home screen.
/// </summary>
public sealed class ThingPage : UserControl, IPage
{
    private readonly Appearance _appearance;
    private readonly bool _isItem;
    private readonly Image _preview = new() { Stretch = Stretch.Uniform, MaxHeight = 192, MaxWidth = 192 };
    private readonly int _frames;
    private int _frame;

    public string PageTitle { get; }
    public string? PageSubtitle { get; }

    public ThingPage(Appearance appearance)
    {
        _appearance = appearance;
        _isItem = appearance.AppearanceType == APPEARANCE_TYPE.AppearanceObject;
        _appearance.Flags ??= new AppearanceFlags();

        string kind = appearance.AppearanceType switch
        {
            APPEARANCE_TYPE.AppearanceOutfit => "Outfit",
            APPEARANCE_TYPE.AppearanceEffect => "Effect",
            APPEARANCE_TYPE.AppearanceMissile => "Missile",
            _ => "Item",
        };
        PageTitle = $"{kind} {appearance.Id}";
        PageSubtitle = _isItem ? Workspace.ItemName(appearance.Id) : null;

        SpriteInfo? info = appearance.FrameGroup.Count > 0 ? appearance.FrameGroup[0].SpriteInfo : null;
        _frames = (int)Math.Max(1, info?.PatternFrames ?? 1);

        var content = new StackPanel { Margin = new Thickness(16), MaxWidth = 720 };
        content.Children.Add(PreviewCard(info));
        content.Children.Add(DatCard());
        if (_isItem)
        {
            if (Workspace.ServerItemFor(appearance.Id) is { } serverItem)
                content.Children.Add(OtbCard(serverItem));
            ItemsXml.Items.TryGetValue(Workspace.ServerIdFor(appearance.Id), out ItemEntry? xml);
            content.Children.Add(XmlCard(xml));
        }
        Content = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        RenderPreview();
    }

    // ---- preview ----

    private Control PreviewCard(SpriteInfo? info)
    {
        var panel = new StackPanel { Spacing = 8 };
        RenderOptions.SetBitmapInterpolationMode(_preview, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
        panel.Children.Add(new Border
        {
            Background = Brush("SemiColorFill0"),
            CornerRadius = new CornerRadius(10),
            Height = 208,
            Child = _preview,
        });

        if (info != null)
        {
            string text = $"{info.PatternWidth}×{info.PatternHeight} tiles · layers {info.PatternLayers} · patterns {info.PatternX}×{info.PatternY}×{info.PatternZ} · " +
                          $"{info.PatternFrames} frame(s) · {_appearance.FrameGroup.Count} group(s) · {info.SpriteId.Count} sprite ids";
            panel.Children.Add(Muted(text));
        }
        else
            panel.Children.Add(Muted("No sprites."));

        if (_frames > 1)
        {
            var slider = new Slider { Minimum = 0, Maximum = _frames - 1, TickFrequency = 1, IsSnapToTickEnabled = true };
            var label = Muted($"Frame 1 of {_frames}");
            slider.PropertyChanged += (_, e) =>
            {
                if (e.Property != Slider.ValueProperty) return;
                _frame = (int)slider.Value;
                label.Text = $"Frame {_frame + 1} of {_frames}";
                RenderPreview();
            };
            panel.Children.Add(slider);
            panel.Children.Add(label);
        }
        return Card(null, panel);
    }

    private async void RenderPreview()
    {
        int frame = _frame;
        var bitmap = await System.Threading.Tasks.Task.Run(() => ThingRow.RenderBitmap(_appearance, frame));
        if (frame != _frame)
            return;
        _preview.Source = bitmap;
        if (bitmap != null)
        {
            // whole-number zoom keeps the pixels sharp
            double zoom = Math.Max(1, Math.Floor(192.0 / Math.Max(bitmap.PixelSize.Width, bitmap.PixelSize.Height)));
            _preview.Width = bitmap.PixelSize.Width * zoom;
            _preview.Height = bitmap.PixelSize.Height * zoom;
        }
    }

    // ---- Tibia.dat flags ----

    private sealed record Num(string Label, Func<AppearanceFlags, long> Get, Action<AppearanceFlags, long> Set, long Min = 0, long Max = ushort.MaxValue);

    private sealed record DatFlag(string Name, string Label, Func<AppearanceFlags, bool> Has, Action<AppearanceFlags, bool> Toggle, params Num[] Values);

    private static DatFlag Bool(string name, string label, Func<AppearanceFlags, bool> get, Action<AppearanceFlags, bool> set) => new(name, label, get, set);

    // names as in Appearances.xml; only the flags of the loaded structure are shown, the writer drops others
    private static readonly (string Section, DatFlag[] Flags)[] DatFlags =
    [
        ("Ground & layering", [
            new DatFlag("Ground", "Ground", f => f.Bank != null, (f, on) => f.Bank = on ? new AppearanceFlagBank { Waypoints = 100 } : null,
                new Num("Speed", f => f.Bank?.Waypoints ?? 0, (f, v) => f.Bank.Waypoints = (uint)v)),
            Bool("Clip", "Ground border (clip)", f => f.Clip, (f, on) => f.Clip = on),
            Bool("Bottom", "On bottom", f => f.Bottom, (f, on) => f.Bottom = on),
            Bool("Top", "On top", f => f.Top, (f, on) => f.Top = on),
            Bool("FullTile", "Full ground", f => f.Fullbank, (f, on) => f.Fullbank = on),
        ]),
        ("Use & content", [
            Bool("Container", "Container", f => f.Container, (f, on) => f.Container = on),
            Bool("Stackable", "Stackable", f => f.Cumulative, (f, on) => f.Cumulative = on),
            Bool("Usable", "Usable", f => f.Usable, (f, on) => f.Usable = on),
            Bool("ForceUse", "Force use", f => f.Forceuse, (f, on) => f.Forceuse = on),
            Bool("Multiuse", "Multi use", f => f.Multiuse, (f, on) => f.Multiuse = on),
            new DatFlag("Writeable", "Writable", f => f.Write != null, (f, on) => f.Write = on ? new AppearanceFlagWrite { MaxTextLength = 255 } : null,
                new Num("Max length", f => f.Write?.MaxTextLength ?? 0, (f, v) => f.Write.MaxTextLength = (uint)v)),
            new DatFlag("WriteableOnce", "Writable once", f => f.WriteOnce != null, (f, on) => f.WriteOnce = on ? new AppearanceFlagWriteOnce { MaxTextLengthOnce = 255 } : null,
                new Num("Max length", f => f.WriteOnce?.MaxTextLengthOnce ?? 0, (f, v) => f.WriteOnce.MaxTextLengthOnce = (uint)v)),
            Bool("LiquidContainer", "Fluid container", f => f.Liquidcontainer, (f, on) => f.Liquidcontainer = on),
            Bool("LiquidPool", "Splash", f => f.Liquidpool, (f, on) => f.Liquidpool = on),
            Bool("Wrappable", "Wrappable", f => f.Wrap, (f, on) => f.Wrap = on),
            Bool("UnWrappable", "Unwrappable", f => f.Unwrap, (f, on) => f.Unwrap = on),
            Bool("ShowCharges", "Show charges", f => f.Wearout, (f, on) => f.Wearout = on),
        ]),
        ("Movement & blocking", [
            Bool("Impassable", "Unpassable", f => f.Unpass, (f, on) => f.Unpass = on),
            Bool("Unmovable", "Unmovable", f => f.Unmove, (f, on) => f.Unmove = on),
            Bool("BlocksSight", "Blocks missiles", f => f.Unsight, (f, on) => f.Unsight = on),
            Bool("BlocksPathfinding", "Blocks path", f => f.Avoid, (f, on) => f.Avoid = on),
            Bool("Pickupable", "Pickupable", f => f.Take, (f, on) => f.Take = on),
            Bool("Hangable", "Hangable", f => f.Hang, (f, on) => f.Hang = on),
            Bool("HooksSouth", "Hook south", f => f.HookSouth, (f, on) => f.HookSouth = on),
            Bool("HooksEast", "Hook east", f => f.HookEast, (f, on) => f.HookEast = on),
            Bool("Rotateable", "Rotatable", f => f.Rotate, (f, on) => f.Rotate = on),
            Bool("NoMovementAnimation", "No movement animation", f => f.NoMovementAnimation, (f, on) => f.NoMovementAnimation = on),
        ]),
        ("Display", [
            new DatFlag("LightSource", "Light", f => f.Light != null, (f, on) => f.Light = on ? new AppearanceFlagLight { Brightness = 3, Color = 215 } : null,
                new Num("Level", f => f.Light?.Brightness ?? 0, (f, v) => f.Light.Brightness = (uint)v),
                new Num("Color", f => f.Light?.Color ?? 0, (f, v) => f.Light.Color = (uint)v, 0, 255)),
            new DatFlag("Displaced", "Offset", f => f.Shift != null, (f, on) => f.Shift = on ? new AppearanceFlagShift { X = 8, Y = 8 } : null,
                new Num("X", f => f.Shift?.X ?? 0, (f, v) => f.Shift.X = (int)v, short.MinValue, short.MaxValue),
                new Num("Y", f => f.Shift?.Y ?? 0, (f, v) => f.Shift.Y = (int)v, short.MinValue, short.MaxValue)),
            new DatFlag("Elevated", "Elevation", f => f.Height != null, (f, on) => f.Height = on ? new AppearanceFlagHeight { Elevation = 8 } : null,
                new Num("Height", f => f.Height?.Elevation ?? 0, (f, v) => f.Height.Elevation = (uint)v)),
            new DatFlag("MinimapColor", "Minimap", f => f.Automap != null, (f, on) => f.Automap = on ? new AppearanceFlagAutomap { Color = 0 } : null,
                new Num("Color", f => f.Automap?.Color ?? 0, (f, v) => f.Automap.Color = (uint)v, 0, 255)),
            Bool("AlwaysSeen", "Don't hide", f => f.DontHide, (f, on) => f.DontHide = on),
            Bool("Translucent", "Translucent", f => f.Translucent, (f, on) => f.Translucent = on),
            Bool("LyingObject", "Lying object", f => f.LyingObject, (f, on) => f.LyingObject = on),
            Bool("AlwaysAnimated", "Always animated", f => f.AnimateAlways, (f, on) => f.AnimateAlways = on),
            Bool("TopEffect", "Top effect", f => f.Topeffect, (f, on) => f.Topeffect = on),
        ]),
        ("Info", [
            new DatFlag("HelpInfo", "Lens help", f => f.Lenshelp != null, (f, on) => f.Lenshelp = on ? new AppearanceFlagLenshelp { Id = 1100 } : null,
                new Num("Id", f => f.Lenshelp?.Id ?? 0, (f, v) => f.Lenshelp.Id = (uint)v)),
            Bool("Lookthrough", "Ignore look", f => f.IgnoreLook, (f, on) => f.IgnoreLook = on),
            new DatFlag("Clothes", "Clothes slot", f => f.Clothes != null, (f, on) => f.Clothes = on ? new AppearanceFlagClothes { Slot = 1 } : null,
                new Num("Slot", f => f.Clothes?.Slot ?? 0, (f, v) => f.Clothes.Slot = (uint)v)),
        ]),
    ];

    private Control DatCard()
    {
        var panel = new StackPanel();
        VersionInfo? version = Workspace.Version;
        AppearanceFlags flags = _appearance.Flags;

        foreach (var (section, list) in DatFlags)
        {
            var visible = list.Where(f => version?.HasFlag(f.Name) ?? true).ToList();
            if (visible.Count == 0)
                continue;
            panel.Children.Add(new TextBlock { Text = section, Classes = { "section" } });

            var grid = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (DatFlag flag in visible.Where(f => f.Values.Length == 0))
            {
                var box = new CheckBox { Content = flag.Label, IsChecked = flag.Has(flags), MinWidth = 160, Margin = new Thickness(0, 0, 8, 0) };
                box.IsCheckedChanged += (_, _) =>
                {
                    flag.Toggle(flags, box.IsChecked == true);
                    Workspace.DatDirty = true;
                };
                grid.Children.Add(box);
            }
            panel.Children.Add(grid);

            foreach (DatFlag flag in visible.Where(f => f.Values.Length > 0))
                panel.Children.Add(ValueFlagRow(flag, flags));
        }

        if (flags.Market != null)
        {
            var m = flags.Market;
            panel.Children.Add(new TextBlock { Text = "Market", Classes = { "section" } });
            panel.Children.Add(Muted($"{m.Name} · category {m.Category} · trade as {m.TradeAsObjectId} · show as {m.ShowAsObjectId} · level {m.MinimumLevel} (kept, edit on desktop)"));
        }

        return Card("Client flags · Tibia.dat", panel);
    }

    private Control ValueFlagRow(DatFlag flag, AppearanceFlags flags)
    {
        var row = new StackPanel { Margin = new Thickness(0, 4) };
        var box = new CheckBox { Content = flag.Label, IsChecked = flag.Has(flags) };
        row.Children.Add(box);

        var values = new WrapPanel { Margin = new Thickness(28, 0, 0, 0) };
        var inputs = new List<NumericUpDown>();
        foreach (Num num in flag.Values)
        {
            var input = new NumericUpDown
            {
                Minimum = num.Min,
                Maximum = num.Max,
                Increment = 1,
                FormatString = "0",
                Width = 150,
                Value = flag.Has(flags) ? num.Get(flags) : 0,
                IsEnabled = flag.Has(flags),
            };
            input.ValueChanged += (_, e) =>
            {
                if (!flag.Has(flags) || e.NewValue is not decimal v)
                    return;
                num.Set(flags, (long)v);
                Workspace.DatDirty = true;
            };
            inputs.Add(input);
            values.Children.Add(new StackPanel
            {
                Margin = new Thickness(0, 0, 12, 4),
                Children = { Muted(num.Label), input },
            });
        }
        row.Children.Add(values);

        box.IsCheckedChanged += (_, _) =>
        {
            bool on = box.IsChecked == true;
            flag.Toggle(flags, on);
            for (int i = 0; i < inputs.Count; i++)
            {
                inputs[i].IsEnabled = on;
                inputs[i].Value = on ? flag.Values[i].Get(flags) : 0;
            }
            Workspace.DatDirty = true;
        };
        return row;
    }

    // ---- items.otb ----

    private static readonly (string Property, string Label)[] OtbBools =
    [
        ("Unpassable", "Unpassable"), ("BlockMissiles", "Blocks missiles"), ("BlockPathfinder", "Blocks path"),
        ("HasElevation", "Elevation"), ("ForceUse", "Force use"), ("MultiUse", "Multi use"),
        ("Pickupable", "Pickupable"), ("Movable", "Movable"), ("Stackable", "Stackable"),
        ("Readable", "Readable"), ("Rotatable", "Rotatable"), ("Hangable", "Hangable"),
        ("HookSouth", "Hook south"), ("HookEast", "Hook east"), ("IgnoreLook", "Ignore look"),
        ("FullGround", "Full ground"), ("AllowDistanceRead", "Distance read"), ("IsAnimation", "Animation"),
    ];

    private static readonly (string Property, string Label)[] OtbNumbers =
    [
        ("GroundSpeed", "Ground speed"), ("LightLevel", "Light level"), ("LightColor", "Light color"),
        ("MinimapColor", "Minimap color"), ("TradeAs", "Trade as"), ("MaxReadWriteChars", "Max write chars"), ("MaxReadChars", "Max read chars"),
    ];

    private Control OtbCard(ServerItem item)
    {
        var panel = new StackPanel();
        string group = item.Type == ServerItemType.None && item.RawGroup != 0 ? $"group {item.RawGroup}" : item.Type.ToString();
        panel.Children.Add(Muted($"Server id {item.ServerId} · client id {item.ClientId} · {group}"));

        var grid = new WrapPanel();
        foreach (var (property, label) in OtbBools)
        {
            var info = typeof(ServerItem).GetProperty(property)!;
            var box = new CheckBox { Content = label, IsChecked = (bool)info.GetValue(item)!, MinWidth = 160, Margin = new Thickness(0, 0, 8, 0) };
            box.IsCheckedChanged += (_, _) =>
            {
                info.SetValue(item, box.IsChecked == true);
                Workspace.OtbDirty = true;
            };
            grid.Children.Add(box);
        }
        panel.Children.Add(grid);

        var numbers = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (property, label) in OtbNumbers)
        {
            var info = typeof(ServerItem).GetProperty(property)!;
            var input = new NumericUpDown { Minimum = 0, Maximum = ushort.MaxValue, Increment = 1, FormatString = "0", Width = 150, Value = (ushort)info.GetValue(item)! };
            input.ValueChanged += (_, e) =>
            {
                if (e.NewValue is not decimal v) return;
                info.SetValue(item, (ushort)v);
                Workspace.OtbDirty = true;
            };
            numbers.Children.Add(new StackPanel { Margin = new Thickness(0, 0, 12, 4), Children = { Muted(label), input } });
        }
        panel.Children.Add(numbers);
        return Card("Server · items.otb", panel);
    }

    // ---- items.xml ----

    private Control XmlCard(ItemEntry? entry)
    {
        var panel = new StackPanel { Spacing = 6 };
        if (entry == null)
        {
            panel.Children.Add(Muted(ItemsXml.IsLoaded
                ? "No items.xml entry for this id (adding entries is desktop-only in this alpha)."
                : "items.xml is not loaded."));
            return Card("Server · items.xml", panel);
        }

        void Text(string label, Func<string> get, Action<string> set, bool multiline = false)
        {
            var box = new TextBox { Text = get(), AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap };
            box.TextChanged += (_, _) =>
            {
                if (box.Text == get()) return;
                set(box.Text ?? "");
                Workspace.XmlDirty = true;
            };
            panel.Children.Add(Muted(label));
            panel.Children.Add(box);
        }

        void Number(string label, Func<int> get, Action<int> set)
        {
            var input = new NumericUpDown { Minimum = 0, Maximum = int.MaxValue, Increment = 1, FormatString = "0", Value = get() };
            input.ValueChanged += (_, e) =>
            {
                if (e.NewValue is not decimal v || (int)v == get()) return;
                set((int)v);
                Workspace.XmlDirty = true;
            };
            panel.Children.Add(Muted(label));
            panel.Children.Add(input);
        }

        Text("Name", () => entry.Name, v => entry.Name = v);
        Text("Article", () => entry.Article, v => entry.Article = v);
        Text("Plural", () => entry.Plural, v => entry.Plural = v);
        Text("Description", () => entry.Description, v => entry.Description = v, multiline: true);
        Number("Weight (×100, 100 = 1.00 oz)", () => entry.Weight, v => entry.Weight = v);
        Number("Attack", () => entry.Attack, v => entry.Attack = v);
        Number("Defense", () => entry.Defense, v => entry.Defense = v);
        Number("Armor", () => entry.Armor, v => entry.Armor = v);
        if (entry.Id != _appearance.Id)
            panel.Children.Add(Muted($"items.xml id {entry.Id}"));
        return Card("Server · items.xml", panel);
    }

    // ---- helpers ----

    private static IBrush? Brush(string key) =>
        Application.Current!.TryGetResource(key, Application.Current.ActualThemeVariant, out object? value) && value is IBrush brush ? brush
        : Application.Current.TryGetResource(key, Application.Current.ActualThemeVariant, out value) && value is Color c ? new SolidColorBrush(c) : null;

    private static TextBlock Muted(string text) => new() { Text = text, Classes = { "muted" } };

    private static Control Card(string? title, Control body)
    {
        var panel = new StackPanel { Spacing = 6 };
        if (title != null)
            panel.Children.Add(new TextBlock { Text = title, Classes = { "title" } });
        panel.Children.Add(body);
        return new Border { Classes = { "card" }, Child = panel };
    }
}
