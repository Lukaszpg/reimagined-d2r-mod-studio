using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ModStudio.Core;

namespace ModStudio.App;

/// <summary>
/// Draws how a row reaches what HD draws for it, top to bottom: the table row, the entry its id keys in the HD list, and the
/// unit definition or sprite that entry names. Each file says whether it is the project's or the game's, and the project's
/// open in Studio (the list at its entry). A broken link shows where the chain stops and why.
/// </summary>
internal static class VisualLinkView
{
    private static readonly IBrush ProjectBrush = new SolidColorBrush(Color.Parse("#D8BC86")), GameBrush = new SolidColorBrush(Color.Parse("#7F8A99")),
        MissingBrush = new SolidColorBrush(Color.Parse("#E39A6B")), Muted = new SolidColorBrush(Color.Parse("#A8A29A")), White = new SolidColorBrush(Color.Parse("#E6E6E6")),
        NodeBackground = new SolidColorBrush(Color.Parse("#25262A"));

    public static Control Chain(VisualLink link, string table, int row, Func<string, int, Task>? openAt, Action<string, bool> status)
    {
        var spec = link.Spec;
        var panel = new StackPanel { Spacing = 0 };
        panel.Children.Add(Node($"{table} · row {row}", $"{spec.IdColumn} \"{(link.Id.Length > 0 ? link.Id : "(empty)")}\"", "this row", ProjectBrush, null));
        if (link.Map == null)
        {
            panel.Children.Add(Arrow("looked up in", MissingBrush));
            panel.Children.Add(Node(spec.MapName, spec.Map, "not found", MissingBrush, null));
        }
        else
        {
            panel.Children.Add(Arrow(link.Key != null ? $"keyed \"{link.Key}\" in" : $"no entry for \"{link.Id}\" in", link.Key != null ? Muted : MissingBrush));
            panel.Children.Add(Node(spec.MapName, link.Map.Relative, link.Map.Origin, link.Map.InProject ? ProjectBrush : GameBrush,
                Open(link.Map, link.Key != null ? Math.Max(0, link.EntryOffset) : 0, link.Key != null ? "Open at entry" : "Open", openAt, status)));
            if (link.Value != null)
            {
                panel.Children.Add(Arrow($"\"{link.Key}\": \"{link.Value}\" names", link.Target != null ? Muted : MissingBrush));
                panel.Children.Add(link.Target is { } target
                    ? Node(Path.GetFileName(target.Path), target.Relative, target.Origin, target.InProject ? ProjectBrush : GameBrush, Open(target, 0, "Open", openAt, status))
                    : Node(link.Value, $"the {spec.TargetNoun} is missing", "not found", MissingBrush, null));
            }
        }
        foreach (var note in link.Notes)
            panel.Children.Add(new SelectableTextBlock { Text = note, FontSize = 11, Foreground = link.Key == null && spec.Family == VisualFamily.NamedItem ? Muted : MissingBrush, TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 0) });
        return panel;
    }

    /// <summary>A file in the chain: its name, path and origin, with a coloured edge (gold: project, grey: game data, orange: missing).</summary>
    private static Control Node(string title, string path, string origin, IBrush edge, Button? open)
    {
        var text = new StackPanel { Spacing = 1 };
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        heading.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = White, TextTrimming = TextTrimming.CharacterEllipsis });
        heading.Children.Add(new Border { Background = edge, CornerRadius = new(3), Padding = new(5, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = origin, FontSize = 10, Foreground = Brushes.Black } });
        text.Children.Add(heading);
        text.Children.Add(new SelectableTextBlock { Text = path, FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap });
        var dock = new DockPanel();
        if (open != null) { DockPanel.SetDock(open, Dock.Right); dock.Children.Add(open); }
        dock.Children.Add(text);
        return new Border { Background = NodeBackground, BorderBrush = edge, BorderThickness = new(3, 0, 0, 0), CornerRadius = new(3), Padding = new(10, 6), Child = dock };
    }

    private static Control Arrow(string label, IBrush brush)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new(12, 1, 0, 1) };
        row.Children.Add(new TextBlock { Text = "↓", FontSize = 14, Foreground = brush, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = brush, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 });
        return row;
    }

    /// <summary>Opens a project file in Studio; the game's own files are only read, so theirs is disabled and says why.</summary>
    private static Button Open(HdFile file, int offset, string label, Func<string, int, Task>? openAt, Action<string, bool> status)
    {
        var button = new Button { Content = label, Padding = new(8, 2), MinHeight = 0, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 0, 0), IsEnabled = file.InProject && openAt != null };
        AutomationProperties.SetName(button, label + " " + Path.GetFileName(file.Path));
        ToolTip.SetTip(button, file.InProject ? file.Path : $"{file.Path}\nThis is the game's file, read from your game data folder. Choosing a visual copies the list into the project, where it can be opened and edited.");
        ToolTip.SetShowOnDisabled(button, true);
        button.Click += async (_, _) => { try { await openAt!(file.Path, offset); } catch (Exception ex) { status(ex.Message, true); } };
        return button;
    }
}
