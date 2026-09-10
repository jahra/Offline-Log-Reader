using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using OfflineLogReader.Core;

namespace OfflineLogReader;

public sealed class ExclusionEditor
{
    public string Text { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool MatchCase { get; set; }
}

public sealed class SourceOption(string name) : INotifyPropertyChanged
{
    private bool selected;
    public string Name { get; } = name;
    public bool Selected { get => selected; set { selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    private readonly List<LogFile> files = new();
    private readonly ObservableCollection<ExclusionEditor> exclusions = new();
    private readonly List<Window> resultWindows = new();
    private SourceOption[] sourceOptions = [];
    private LogEntry[] all = [], visible = [];
    private LogEntry[]? hits;
    private int hitIndex = -1;
    private CancellationTokenSource? operation;
    private bool closeAfterOperation;
    public MainWindow()
    {
        InitializeComponent();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        ExclusionItems.ItemsSource = exclusions;
        Log.ContextRequested += entry => ShowContext(all, entry, SearchText.Text, SearchCase.IsChecked == true);
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, (_, _) => { SearchText.Focus(); SearchText.SelectAll(); }));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Open, (_, _) => OpenClick(this, new RoutedEventArgs()), (_, e) => e.CanExecute = operation == null));
        Closing += OnClosing;
        Loaded += async (_, _) =>
        {
            var paths = Environment.GetCommandLineArgs().Skip(1).ToArray();
            if (paths.Length > 0) await LoadFiles(paths);
        };
    }

    private async Task Work<T>(Func<CancellationToken, IProgress<WorkProgress>, T> work, Action<T> commit)
    {
        if (operation != null) return;
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        SetBusy(true);
        Status.Text = "Probíhá zpracování…";
        var reporter = new Progress<WorkProgress>(p =>
        {
            if (operation != cancellation) return;
            Progress.Value = Math.Clamp(p.Fraction, 0, 1);
            Status.Text = p.Message;
        });
        try
        {
            var result = await Task.Run(() => work(cancellation.Token, reporter));
            commit(result);
        }
        catch (OperationCanceledException) { Status.Text = "Operace zrušena. Předchozí zobrazení zůstalo zachováno."; }
        catch (Exception ex) { Status.Text = "Operace se nezdařila."; MessageBox.Show(this, ex.Message, "Offline Log Reader", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally
        {
            operation = null;
            SetBusy(false);
            if (closeAfterOperation) Close();
        }
    }
    private void SetBusy(bool busy)
    {
        OpenButton.IsEnabled = ClearButton.IsEnabled = EncodingChoice.IsEnabled = FilterPanel.IsEnabled = SearchPanel.IsEnabled = !busy;
        Progress.Visibility = CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Progress.Value = 0;
    }
    private async void OpenClick(object sender, RoutedEventArgs e)
    {
        if (operation != null) return;
        var dialog = new OpenFileDialog { Multiselect = true, Filter = "Log soubory (*.txt;*.log)|*.txt;*.log|Všechny soubory (*.*)|*.*" };
        if (dialog.ShowDialog(this) == true) await LoadFiles(dialog.FileNames);
    }
    private async Task LoadFiles(string[] paths)
    {
        if (operation != null) return;
        var known = files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var additions = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Where(p => !known.Contains(p)).ToArray();
        if (additions.Length == 0) { Status.Text = "Vybrané soubory jsou již načtené."; return; }
        Encoding encoding = EncodingChoice.SelectedIndex == 1 ? Encoding.GetEncoding(1250) : new UTF8Encoding(false);
        int order = files.Count;
        await Work((token, progress) =>
        {
            var parsed = new List<ParsedLog>();
            try
            {
                long totalBytes = additions.Sum(p => new FileInfo(p).Length), done = 0;
                foreach (string path in additions)
                {
                    token.ThrowIfCancellationRequested();
                    long size = new FileInfo(path).Length;
                    long before = done;
                    var child = new InlineProgress(p => progress.Report(new WorkProgress(totalBytes == 0 ? 1 : (before + size * p.Fraction) / totalBytes, p.Message)));
                    parsed.Add(LogParser.Parse(path, encoding, order + parsed.Count, child, token));
                    done += size;
                }
                progress.Report(new WorkProgress(1, "Řazení záznamů podle času…"));
                var merged = LogParser.Merge(all.Concat(parsed.SelectMany(p => p.Entries)), token);
                progress.Report(new WorkProgress(1, "Příprava seznamu severity a source…"));
                var severityValues = new HashSet<string>(StringComparer.Ordinal);
                var sourceValues = new HashSet<string>(StringComparer.Ordinal);
                int unknown = 0;
                DateTime? first = null, last = null;
                foreach (var entry in merged)
                {
                    token.ThrowIfCancellationRequested();
                    severityValues.Add(entry.Severity); sourceValues.Add(entry.Source);
                    if (entry.Timestamp is DateTime timestamp) { first ??= timestamp; last = timestamp; }
                    else unknown++;
                }
                return (Parsed: parsed, Entries: merged, Severities: severityValues.Order().ToArray(), Sources: sourceValues.Order().Select(x => new SourceOption(x)).ToArray(), Unknown: unknown, First: first, Last: last);
            }
            catch { foreach (var parsedFile in parsed) parsedFile.File.Dispose(); throw; }
        }, result =>
        {
            files.AddRange(result.Parsed.Select(p => p.File));
            all = result.Entries;
            ResetFilterInputs();
            SeverityList.ItemsSource = result.Severities;
            sourceOptions = result.Sources;
            SourceList.ItemsSource = sourceOptions;
            SetVisible(all);
            FileSummary.Text = $"Soubory: {files.Count} • {all.Length:N0} záznamů" + (result.First.HasValue ? $" • {result.First:dd.MM.yyyy} – {result.Last:dd.MM.yyyy}" : "");
            FileSummary.ToolTip = string.Join(Environment.NewLine, files.Select(f => $"{f.Path} ({f.Encoding.WebName})"));
            Status.Text += $" • nerozpoznané záznamy: {result.Unknown:N0}";
        });
    }
    private sealed class InlineProgress(Action<WorkProgress> action) : IProgress<WorkProgress> { public void Report(WorkProgress value) => action(value); }
    private void SetVisible(LogEntry[] entries)
    {
        visible = entries;
        Log.SetEntries(entries);
        hits = null;
        hitIndex = -1;
        EmptyHint.Visibility = entries.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Text = all.Length == 0 ? "Otevřete soubory a prohlížejte logy napříč dny.\nVeškerá data zůstávají na tomto počítači." : "Žádný záznam neodpovídá zvoleným filtrům.";
        Status.Text = $"Zobrazeno {entries.Length:N0} / {all.Length:N0} záznamů";
    }
    private static DateTime? ParseDate(string value, bool upper)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string[] formats = ["dd.MM.yyyy HH:mm:ss", "d.M.yyyy H:mm:ss", "dd.MM.yyyy HH:mm", "d.M.yyyy H:mm", "dd.MM.yyyy", "d.M.yyyy"];
        if (!DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new FormatException("Datum zadejte jako dd.MM.yyyy nebo dd.MM.yyyy HH:mm:ss.");
        return upper && !value.Contains(':') ? (date == DateTime.MaxValue.Date ? DateTime.MaxValue : date.AddDays(1).AddTicks(-1)) : date;
    }
    private async void ApplyClick(object sender, RoutedEventArgs e)
    {
        QueryOptions query;
        try
        {
            query = new QueryOptions(ParseDate(FromText.Text, false), ParseDate(ToText.Text, true), SeverityList.SelectedItems.Cast<string>().ToArray(), sourceOptions.Where(x => x.Selected).Select(x => x.Name).ToArray(), exclusions.Select(x => new Exclusion(x.Text, x.MatchCase, x.Enabled)).ToArray());
            if (query.From > query.To) throw new FormatException("Datum Od musí být menší nebo rovno datu Do.");
        }
        catch (FormatException ex) { MessageBox.Show(this, ex.Message, "Zkontrolujte období"); return; }
        await Work((token, progress) => LogQuery.Run(all, query, progress, token), SetVisible);
    }
    private void ResetFilterInputs()
    {
        FromText.Clear(); ToText.Clear(); SourceSearch.Clear();
        SeverityList.UnselectAll(); foreach (var source in sourceOptions) source.Selected = false; exclusions.Clear();
    }
    private void ResetClick(object sender, RoutedEventArgs e) { ResetFilterInputs(); SetVisible(all); }
    private void AddExclusionClick(object sender, RoutedEventArgs e) => exclusions.Add(new ExclusionEditor());
    private void RemoveExclusionClick(object sender, RoutedEventArgs e) { if (((FrameworkElement)sender).DataContext is ExclusionEditor editor) exclusions.Remove(editor); }
    private void SourceSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (SourceList?.ItemsSource == null) return;
        var view = CollectionViewSource.GetDefaultView(SourceList.ItemsSource);
        view.Filter = x => ((SourceOption)x).Name.Contains(SourceSearch.Text, StringComparison.OrdinalIgnoreCase);
    }
    private void SearchChanged(object sender, RoutedEventArgs e)
    {
        if (Log == null || SearchText == null) return;
        hits = null; hitIndex = -1;
        Log.Query = SearchText.Text;
        Log.MatchCase = SearchCase.IsChecked == true;
    }
    private QueryOptions SearchQuery() => new(null, null, [], [], [], SearchText.Text, SearchCase.IsChecked == true);
    private async void FindAllClick(object sender, RoutedEventArgs e)
    {
        if (SearchText.Text.Length == 0) { SearchText.Focus(); return; }
        var basis = SearchAll.IsChecked == true ? all : visible;
        var query = SearchQuery();
        var snapshot = all;
        await Work((token, progress) => LogQuery.Run(basis, query, progress, token), found =>
        {
            ShowResults($"Find All: {query.Search}", found, snapshot, query.Search, query.MatchCase);
            Status.Text = $"Find All: {found.Length:N0} odpovídajících záznamů";
        });
    }
    private async Task Navigate(int direction)
    {
        if (operation != null || SearchText.Text.Length == 0) return;
        if (hits == null)
        {
            var basis = SearchAll.IsChecked == true ? all : visible;
            var query = SearchQuery();
            await Work((token, progress) => LogQuery.Run(basis, query, progress, token), found => hits = found);
            hitIndex = direction > 0 ? -1 : 0;
        }
        if (hits == null) return;
        if (hits.Length == 0) { Status.Text = "Hledaný text nebyl nalezen."; return; }
        hitIndex = (hitIndex + direction + hits.Length) % hits.Length;
        var entry = hits[hitIndex];
        if (Array.IndexOf(visible, entry) >= 0) Log.Select(entry);
        else ShowContext(all, entry, SearchText.Text, SearchCase.IsChecked == true);
        Status.Text = $"Nalezený záznam {hitIndex + 1:N0} / {hits.Length:N0}";
    }
    private async void NextClick(object sender, RoutedEventArgs e) => await Navigate(1);
    private async void PreviousClick(object sender, RoutedEventArgs e) => await Navigate(-1);
    private async void SearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await Navigate(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1); }
        if (e.Key == Key.Escape) { SearchText.Clear(); Log.Focus(); }
    }
    private void ShowResults(string title, LogEntry[] entries, LogEntry[] snapshot, string query, bool matchCase, LogEntry? selection = null)
    {
        var window = new ResultsWindow(title, entries, query, matchCase) { Owner = this };
        window.View.ContextRequested += entry => ShowContext(snapshot, entry, query, matchCase);
        resultWindows.Add(window);
        window.Closed += (_, _) => resultWindows.Remove(window);
        window.Show();
        if (selection != null) window.View.Select(selection);
    }
    private void ShowContext(LogEntry[] snapshot, LogEntry entry, string query, bool matchCase) => ShowResults("Kontext • všechny záznamy bez filtrů", snapshot, snapshot, query, matchCase, entry);
    private void OnDragOver(object sender, DragEventArgs e) { e.Effects = operation == null && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (operation == null && e.Data.GetData(DataFormats.FileDrop) is string[] paths) await LoadFiles(paths);
    }
    private void CancelClick(object sender, RoutedEventArgs e) => operation?.Cancel();
    private void ClearClick(object sender, RoutedEventArgs e)
    {
        if (operation != null) return;
        foreach (var window in resultWindows.ToArray()) window.Close();
        all = []; SetVisible([]); ResetFilterInputs();
        SeverityList.ItemsSource = null; SourceList.ItemsSource = null;
        foreach (var file in files) file.Dispose();
        files.Clear(); FileSummary.Text = "Přetáhněte logy sem nebo zvolte Přidat soubory"; FileSummary.ToolTip = null;
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (operation != null) { e.Cancel = true; closeAfterOperation = true; operation.Cancel(); return; }
        ClearClick(this, new RoutedEventArgs());
    }
}

