using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace OfflineLogReader;

public sealed class HighlightText : TextBlock
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(string), typeof(HighlightText), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty QueryProperty = DependencyProperty.Register(nameof(Query), typeof(string), typeof(HighlightText), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty MatchCaseProperty = DependencyProperty.Register(nameof(MatchCase), typeof(bool), typeof(HighlightText), new PropertyMetadata(false, Changed));
    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Query { get => (string)GetValue(QueryProperty); set => SetValue(QueryProperty, value); }
    public bool MatchCase { get => (bool)GetValue(MatchCaseProperty); set => SetValue(MatchCaseProperty, value); }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var block = (HighlightText)d;
        Fill(block.Inlines, block.Value ?? "", block.Query ?? "", block.MatchCase);
    }
    internal static void Fill(InlineCollection inlines, string text, string query, bool matchCase)
    {
        inlines.Clear();
        if (query.Length == 0) { inlines.Add(new Run(text)); return; }
        int offset = 0, count = 0;
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        while (offset < text.Length)
        {
            int index = text.IndexOf(query, offset, comparison);
            if (index < 0 || count++ >= 10000) { inlines.Add(new Run(text[offset..])); break; }
            if (index > offset) inlines.Add(new Run(text[offset..index]));
            inlines.Add(new Run(text.Substring(index, query.Length)) { Background = Brushes.Gold, Foreground = Brushes.Black });
            offset = index + query.Length;
        }
    }
}

