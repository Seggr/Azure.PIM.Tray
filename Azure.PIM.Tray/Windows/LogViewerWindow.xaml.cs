using System.Text;
using System.Windows;
using Azure.PIM.Tray.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace Azure.PIM.Tray.Windows;

public partial class LogViewerWindow : Window
{
    public LogViewerWindow()
    {
        InitializeComponent();
        WindowIconHelper.ApplyLogViewerIcon(this);
        WindowIconHelper.CenterOnActiveScreen(this);

        CboMinLevel.SelectedIndex = (int)AppLog.MinLevel;
        ChkLogToDisk.IsChecked    = AppLog.LogToDisk;
        BtnOpenLogDir.IsEnabled   = AppLog.LogToDisk;

        Reload();
        AppLog.EntryAdded += OnEntryAdded;
        Closed += (_, _) => AppLog.EntryAdded -= OnEntryAdded;
    }

    private void OnEntryAdded()
    {
        Dispatcher.InvokeAsync(Reload);
    }

    private void Reload()
    {
        if (LogList is null) return;

        var all = AppLog.GetAll();

        var minDisplay = CboLevel.SelectedIndex switch
        {
            1 => LogLevel.Debug,
            2 => LogLevel.Info,
            3 => LogLevel.Warning,
            4 => LogLevel.Error,
            _ => (LogLevel?)null
        };

        var srcFilter = TxtSourceFilter?.Text?.Trim() ?? string.Empty;

        var filtered = all
            .Where(e => minDisplay is null || e.Level >= minDisplay)
            .Where(e => srcFilter.Length == 0
                     || e.Source.Contains(srcFilter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Timestamp)
            .Select(e => new LogRow(e))
            .ToList();

        LogList.ItemsSource = filtered;

        var total   = all.Count;
        var shown   = filtered.Count;
        var errors  = all.Count(e => e.Level == LogLevel.Error);
        var warns   = all.Count(e => e.Level == LogLevel.Warning);
        CountLabel.Text = total == 0
            ? "No entries."
            : shown == total
                ? $"{total} entries  ({errors} error{(errors == 1 ? "" : "s")}, {warns} warning{(warns == 1 ? "" : "s")})"
                : $"Showing {shown} of {total}  ({errors} error{(errors == 1 ? "" : "s")}, {warns} warning{(warns == 1 ? "" : "s")})";
    }

    private void CboLevel_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => Reload();

    private void Filter_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => Reload();

    private void CboMinLevel_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CboMinLevel.SelectedItem is System.Windows.Controls.ComboBoxItem item
            && item.Tag?.ToString() is string tag
            && int.TryParse(tag, out var idx))
        {
            AppLog.MinLevel = (LogLevel)idx;
            var config = ConnectionService.LoadConfig();
            ConnectionService.SaveConfig(config with { LogLevel = ((LogLevel)idx).ToString() });
        }
    }

    private void ChkLogToDisk_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        var enabled = ChkLogToDisk.IsChecked == true;
        AppLog.LogToDisk        = enabled;
        BtnOpenLogDir.IsEnabled = enabled;

        // Persist to config
        var config = ConnectionService.LoadConfig();
        ConnectionService.SaveConfig(config with { LogToDisk = enabled });
    }

    private void OpenLogDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppLog.LogDir);
            System.Diagnostics.Process.Start("explorer.exe", AppLog.LogDir);
        }
        catch { /* ignore */ }
    }

    private void ClearLogs_Click(object sender, RoutedEventArgs e)
        => AppLog.Clear();

    private void CopyItem_Click(object sender, RoutedEventArgs e)
    {
        if (LogList.SelectedItem is not LogRow row) return;
        System.Windows.Clipboard.SetText($"{row.TimestampDisplay}\t{row.Level}\t{row.Source}\t{row.Message}");
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        var entries = AppLog.GetAll();
        if (entries.Count == 0) return;

        var sb = new StringBuilder();
        sb.AppendLine("Timestamp\tLevel\tSource\tMessage");
        foreach (var entry in entries.OrderByDescending(e => e.Timestamp))
            sb.AppendLine($"{entry.Timestamp:yyyy-MM-dd HH:mm:ss zzz}\t{entry.Level}\t{entry.Source}\t{entry.Message}");

        System.Windows.Clipboard.SetText(sb.ToString());
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed class LogRow
    {
        public string   TimestampDisplay { get; }
        public LogLevel Level            { get; }
        public string   Source           { get; }
        public string   Message          { get; }
        public Brush LevelColor { get; }

        public LogRow(LogEntry e)
        {
            TimestampDisplay = e.Timestamp.LocalDateTime.ToString("HH:mm:ss.fff");
            Level            = e.Level;
            Source           = e.Source;
            Message          = e.Message;
            LevelColor = e.Level switch
            {
                LogLevel.Error   => Brushes.DarkRed,
                LogLevel.Warning => new SolidColorBrush(Color.FromRgb(0xB8, 0x6B, 0x00)),
                LogLevel.Debug   => new SolidColorBrush(Color.FromRgb(0x00, 0x70, 0xC0)),
                _                => new SolidColorBrush(Color.FromRgb(0x22, 0x72, 0x22))
            };
        }
    }
}
