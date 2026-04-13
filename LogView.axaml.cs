using System;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Assets_Editor
{
    public partial class LogView : Window
    {
        public class LogEntry
        {
            public DateTime Timestamp { get; set; }
            public string Level { get; set; } = "";
            public string Message { get; set; } = "";
        }

        public ObservableCollection<LogEntry> LogEntries { get; } = new ObservableCollection<LogEntry>();

        public LogView()
        {
            InitializeComponent();
            LogListView.ItemsSource = LogEntries;
        }

        public void AddLogEntry(LogEntry entry)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.InvokeAsync(() => AddLogEntry(entry));
                return;
            }
            LogEntries.Add(entry);
        }
    }
}
