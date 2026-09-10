using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OfflineLogReader.Core;

namespace OfflineLogReader;
public sealed class ResultsWindow : Window
{
    public LogView View { get; } = new();
    public ResultsWindow(string title, LogEntry[] entries, string query, bool matchCase)
    {
        Title = title; Width = 1150; Height = 760; MinWidth = 750; MinHeight = 450;
        var panel = new DockPanel { Margin = new Thickness(14) };
        var heading = new TextBlock { Text = $"{title}  •  {entries.Length:N0} záznamů", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10), TextTrimming = TextTrimming.CharacterEllipsis };
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var search = new TextBox { Text = query, ToolTip = "Zvýraznit text v tomto okně (Ctrl+F)" };
        DockPanel.SetDock(search, Dock.Top); panel.Children.Add(search);
        search.TextChanged += (_, _) => View.Query = search.Text;
        View.Query = query; View.MatchCase = matchCase; View.SetEntries(entries);
        panel.Children.Add(View); Content = panel;
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, (_, _) => { search.Focus(); search.SelectAll(); }));
    }
}

