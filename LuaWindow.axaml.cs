using Assets_editor;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using MoonSharp.Interpreter;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Xml;
using Tibia.Protobuf.Appearances;
using ProtoCollections = Google.Protobuf.Collections;

namespace Assets_Editor;

public partial class LuaWindow : Window, INotifyPropertyChanged
{
    private Script? _luaScript;
    private ObservableCollection<dynamic> _dataItems = [];
    private string? _results;
    private ObservableCollection<ISeries> _chartSeries = [];
    private List<string> _availableColumns = [];
    private readonly ScriptManager _scriptManager;
    private string? _currentScriptName;
    private CancellationTokenSource? _luaCancellation;
    private readonly ConcurrentQueue<string> _printQueue = new();

    private const string LightSyntaxFile = @"lua-syntax-light.xshd";
    private const string DarkSyntaxFile = @"lua-syntax-dark.xshd";

    public ObservableCollection<ISeries> ChartSeries
    {
        get => _chartSeries ??= [];
        set { _chartSeries = value; OnPropertyChanged(nameof(ChartSeries)); }
    }

    public string? Results
    {
        get => _results;
        set { _results = value; OnPropertyChanged(nameof(Results)); }
    }

    public new event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public LuaWindow()
    {
        InitializeComponent();
        DataContext = this;
        InitializeLuaEngine();
        InitializeTextEditor();
        Results = "Lua Script Editor ready. Write your code and click Execute.";
        _scriptManager = new ScriptManager();

        LuaCodeEditor.Text = @"-- Lua Script Editor
-- Sample script

function generateChartData()
    local result = {}
    for i = 1, 15 do
        table.insert(result, {
            Level = i,
            Experience = i * i * 100,
            Health = 100 + (i * 10),
            Mana = 50 + (i * 5)
        })
    end
    return result
end

return generateChartData()
";
        RefreshScriptList();
    }

    private void RegisterLuaType<T>(string? luaName = null) where T : new()
    {
        luaName ??= typeof(T).Name;
        UserData.RegisterType<T>();
        _luaScript!.Globals[luaName] = DynValue.NewCallback((ctx, args) => UserData.Create(new T()));
    }

    private void InitializeLuaEngine()
    {
        try
        {
            _luaScript = new Script();
            UserData.RegisterType<Dictionary<string, object>>();
            UserData.RegisterType<List<object>>();
            _luaScript.Globals["print"] = DynValue.NewCallback(PrintToResults);

            RegisterLuaType<Appearance>();
            RegisterLuaType<AppearanceFlags>();
            RegisterLuaType<AppearanceFlagMarket>();
            RegisterLuaType<FrameGroup>();
            RegisterLuaType<SpriteInfo>();
            RegisterLuaType<SpriteAnimation>();
            RegisterLuaType<SpritePhase>();

            UserData.RegisterType<ProtoCollections.RepeatedField<uint>>();
            UserData.RegisterType<ProtoCollections.RepeatedField<FrameGroup>>();
            UserData.RegisterType<ProtoCollections.RepeatedField<SpritePhase>>();
            UserData.RegisterType<LuaThings>();

            Table g_things = new(_luaScript);
            _luaScript.Globals["g_things"] = g_things;
            g_things["getOutfits"] = DynValue.NewCallback(LuaThings.Lua_getOutfits);
            g_things["getItems"] = DynValue.NewCallback(LuaThings.Lua_getItems);
            g_things["getEffects"] = DynValue.NewCallback(LuaThings.Lua_getEffects);
            g_things["getMissiles"] = DynValue.NewCallback(LuaThings.Lua_getMissiles);
            g_things["getOutfitById"] = DynValue.NewCallback(LuaThings.Lua_getOutfitById);
            g_things["getItemById"] = DynValue.NewCallback(LuaThings.Lua_getItemById);
            g_things["getEffectById"] = DynValue.NewCallback(LuaThings.Lua_getEffectById);
            g_things["getMissileById"] = DynValue.NewCallback(LuaThings.Lua_getMissileById);
        }
        catch (Exception ex)
        {
            ErrorManager.ShowError($"Failed to initialize Lua engine: {ex.Message}");
        }
    }

    private void InitializeTextEditor()
    {
        bool isDark = MainWindow.IsDarkModeSet();
        string xshdFile = isDark ? DarkSyntaxFile : LightSyntaxFile;
        if (!File.Exists(xshdFile)) return;
        try
        {
            using XmlTextReader reader = new(xshdFile);
            var definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            LuaCodeEditor.SyntaxHighlighting = definition;
        }
        catch { }
    }

    private DynValue PrintToResults(ScriptExecutionContext ctx, CallbackArguments args)
    {
        StringBuilder sb = new();
        for (int i = 0; i < args.Count; i++)
        {
            if (i > 0) sb.Append('\t');
            sb.Append(args[i].ToPrintString());
        }
        _printQueue.Enqueue(sb.ToString());
        return DynValue.Nil;
    }

    private void FlushPrintInternal()
    {
        if (_printQueue.IsEmpty) return;
        StringBuilder sb = new();
        while (_printQueue.TryDequeue(out string? line))
            sb.AppendLine(line);
        Results += sb.ToString();
    }

    private void ExecuteButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_luaScript == null) { Results = "Error: Lua engine not initialized."; return; }
        string luaCode = LuaCodeEditor.Text;
        if (string.IsNullOrWhiteSpace(luaCode)) { Results = "No Lua code to execute."; return; }

        Results = "";
        _luaCancellation = new CancellationTokenSource();
        CancelButton.IsEnabled = true;
        ExecuteButton.IsEnabled = false;

        Task.Run(() =>
        {
            try
            {
                var lua = _luaScript;
                DynValue func = lua.LoadString(luaCode);
                DynValue coroutine = lua.CreateCoroutine(func);
                coroutine.Coroutine.AutoYieldCounter = 500;

                DynValue? result = null;
                while (coroutine.Coroutine.State != CoroutineState.Dead)
                {
                    try { result = coroutine.Coroutine.Resume(); }
                    catch (ScriptRuntimeException ex)
                    {
                        Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            FlushPrintInternal();
                            Results += $"\nLua Runtime Error: {ex.Message}";
                            CancelButton.IsEnabled = false;
                            ExecuteButton.IsEnabled = true;
                        });
                        return;
                    }

                    if (_luaCancellation.Token.IsCancellationRequested)
                    {
                        Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            FlushPrintInternal();
                            Results += "\nScript execution cancelled.";
                            CancelButton.IsEnabled = false;
                            ExecuteButton.IsEnabled = true;
                        });
                        return;
                    }
                }

                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    CancelButton.IsEnabled = false;
                    ExecuteButton.IsEnabled = true;
                    FlushPrintInternal();
                    if (result != null && result.Type != DataType.Void)
                    {
                        Results += $"\nScript executed successfully! (Result: {result.Type})";
                        if (result.Type == DataType.Table && (ShowInGridCheckBox?.IsChecked ?? false))
                            UpdateDataGridFromLuaResult(result);
                    }
                    else
                        Results += "\nScript executed successfully (no return value).";
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    FlushPrintInternal();
                    Results += $"\nError: {ex.Message}";
                    CancelButton.IsEnabled = false;
                    ExecuteButton.IsEnabled = true;
                });
            }
        });
    }

    private void UpdateDataGridFromLuaResult(DynValue result)
    {
        try
        {
            var rows = new ObservableCollection<Dictionary<string, object>>();
            var columns = new HashSet<string>();

            foreach (var pair in result.Table.Pairs)
            {
                if (pair.Value.Type == DataType.Table)
                {
                    var row = new Dictionary<string, object>();
                    foreach (var field in pair.Value.Table.Pairs)
                    {
                        string key = field.Key.ToPrintString();
                        row[key] = field.Value.Type switch
                        {
                            DataType.Number => field.Value.Number,
                            DataType.String => field.Value.String,
                            DataType.Boolean => field.Value.Boolean,
                            _ => field.Value.ToPrintString()
                        };
                        columns.Add(key);
                    }
                    rows.Add(row);
                }
            }

            DataGrid.Columns.Clear();
            foreach (var col in columns)
            {
                DataGrid.Columns.Add(new DataGridTextColumn
                {
                    Header = col,
                    Binding = new Avalonia.Data.Binding($"[{col}]")
                });
            }
            DataGrid.ItemsSource = rows;

            _availableColumns = columns.ToList();
            XAxisComboBox.ItemsSource = _availableColumns;
            if (_availableColumns.Count > 0)
                XAxisComboBox.SelectedIndex = 0;

            _dataItems = new ObservableCollection<dynamic>(rows);
        }
        catch (Exception ex)
        {
            Results += $"\nFailed to populate DataGrid: {ex.Message}";
        }
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        _luaCancellation?.Cancel();
    }

    private void ClearButton_Click(object? sender, RoutedEventArgs e)
    {
        LuaCodeEditor.Text = "";
        Results = "";
    }

    private void XAxisComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) { }

    private void RefreshChartButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_dataItems.Count == 0 || _availableColumns.Count == 0) return;

        try
        {
            string? xAxis = XAxisComboBox.SelectedItem?.ToString();
            if (xAxis == null) return;

            var numericCols = _availableColumns.Where(c => c != xAxis &&
                _dataItems.OfType<Dictionary<string, object>>().All(d => d.ContainsKey(c) && d[c] is double)).ToList();

            var series = new ObservableCollection<ISeries>();
            foreach (var col in numericCols)
            {
                var values = _dataItems.OfType<Dictionary<string, object>>()
                    .Select(d => new ObservablePoint(Convert.ToDouble(d[xAxis]), Convert.ToDouble(d[col])))
                    .ToArray();

                series.Add(new LineSeries<ObservablePoint>
                {
                    Values = values,
                    Name = col,
                    GeometrySize = 5
                });
            }
            ChartSeries = series;
        }
        catch (Exception ex)
        {
            Results += $"\nChart error: {ex.Message}";
        }
    }

    private void RefreshScriptList()
    {
        var scripts = _scriptManager.Scripts.Select(s => s.Name).ToList();
        ScriptListComboBox.ItemsSource = scripts;
        if (_currentScriptName != null)
        {
            int idx = scripts.IndexOf(_currentScriptName);
            if (idx >= 0) ScriptListComboBox.SelectedIndex = idx;
        }
    }

    private void ScriptListComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ScriptListComboBox.SelectedItem is string name)
        {
            _currentScriptName = name;
            string? content = _scriptManager.GetScript(name)?.Code;
            if (content != null)
                LuaCodeEditor.Text = content;
        }
    }

    private void NewScriptButton_Click(object? sender, RoutedEventArgs e)
    {
        _currentScriptName = null;
        LuaCodeEditor.Text = "-- New Script\n";
    }

    private async void SaveScriptButton_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentScriptName))
        {
            // Simple input - just use a default name
            _currentScriptName = $"script_{DateTime.Now:yyyyMMdd_HHmmss}";
        }
        if (_scriptManager.ScriptExists(_currentScriptName))
            _scriptManager.UpdateScript(_currentScriptName, LuaCodeEditor.Text);
        else
            _scriptManager.AddScript(new ScriptData { Name = _currentScriptName, Code = LuaCodeEditor.Text });
        RefreshScriptList();
    }

    private void DeleteScriptButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentScriptName != null)
        {
            _scriptManager.DeleteScript(_currentScriptName);
            _currentScriptName = null;
            RefreshScriptList();
        }
    }

    private void RefreshScriptsButton_Click(object? sender, RoutedEventArgs e)
    {
        RefreshScriptList();
    }

    private async void ExportCsvButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_dataItems.Count == 0) return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export CSV",
            DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV Files") { Patterns = ["*.csv"] }]
        });

        if (file != null)
        {
            try
            {
                var sb = new StringBuilder();
                if (_availableColumns.Count > 0)
                    sb.AppendLine(string.Join(",", _availableColumns));

                foreach (var row in _dataItems.OfType<Dictionary<string, object>>())
                {
                    var values = _availableColumns.Select(c => row.ContainsKey(c) ? row[c]?.ToString() ?? "" : "");
                    sb.AppendLine(string.Join(",", values));
                }

                await using var stream = await file.OpenWriteAsync();
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(sb.ToString());
                Results += "\nCSV exported successfully.";
            }
            catch (Exception ex)
            {
                Results += $"\nCSV export error: {ex.Message}";
            }
        }
    }
}
