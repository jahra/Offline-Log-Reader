using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using OfflineLogReader.Core;

namespace OfflineLogReader;
public partial class LogView : UserControl
{
    public static readonly DependencyProperty QueryProperty = DependencyProperty.Register(nameof(Query), typeof(string), typeof(LogView), new PropertyMetadata("", SearchChanged));
    public static readonly DependencyProperty MatchCaseProperty = DependencyProperty.Register(nameof(MatchCase), typeof(bool), typeof(LogView), new PropertyMetadata(false, SearchChanged));
    public string Query { get => (string)GetValue(QueryProperty); set => SetValue(QueryProperty, value); }
    public bool MatchCase { get => (bool)GetValue(MatchCaseProperty); set => SetValue(MatchCaseProperty, value); }
    public event Action<LogEntry>? ContextRequested;
    private CancellationTokenSource? detailCancellation;
    private string selectedText = "";
    public LogView() { InitializeComponent(); Unloaded += (_, _) => detailCancellation?.Cancel(); }
    public void SetEntries(LogEntry[] entries) { Rows.ItemsSource = entries; }
    public void Select(LogEntry entry) { Rows.SelectedItem = entry; Rows.ScrollIntoView(entry); }
    private static void SearchChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((LogView)d).RenderDetail();
    private async void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        detailCancellation?.Cancel();
        detailCancellation?.Dispose();
        detailCancellation = new CancellationTokenSource();
        var token = detailCancellation.Token;
        selectedText = "";
        RenderDetail();
        if (Rows.SelectedItem is not LogEntry entry) { Origin.Text = "Vyberte záznam pro zobrazení detailu"; return; }
        Origin.Text = $"{entry.File.Path} • řádek {entry.Line:N0}";
        Origin.ToolTip = Origin.Text;
        try
        {
            // Very large malformed records are previewed in a bounded detail view.
            string text = await Task.Run(() => entry.File.Read(entry.Offset, Math.Min(entry.Length, 256 * 1024)), token);
            if (token.IsCancellationRequested) return;
            selectedText = text + (entry.Length > 256 * 1024 ? "\n\n[Detail zkrácen na 256 KB; hledání prochází celý záznam.]" : "");
            RenderDetail();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) Origin.Text = "Čtení selhalo: " + ex.Message; }
    }
    private void RenderDetail()
    {
        if (Detail == null) return;
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        HighlightText.Fill(paragraph.Inlines, selectedText, Query, MatchCase);
        Detail.Document = new FlowDocument(paragraph) { PagePadding = new Thickness(10) };
    }
    private void ContextClick(object sender, RoutedEventArgs e) { if (Rows.SelectedItem is LogEntry entry) ContextRequested?.Invoke(entry); }
    private async void CopyClick(object sender, RoutedEventArgs e)
    {
        if (Rows.SelectedItem is not LogEntry entry) return;
        try { var text = await Task.Run(() => entry.Text); Clipboard.SetText(text); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Kopírování se nezdařilo"); }
    }
}

