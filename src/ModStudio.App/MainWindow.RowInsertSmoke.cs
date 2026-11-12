using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

public partial class MainWindow
{
    /// <summary>
    /// Add row above/below on a wide table: the new rows appear selected in the column the user was in, the column page does
    /// not jump back to the first columns, and the grid columns themselves survive (a row change never rebuilds them).
    /// </summary>
    private async Task SmokeRowInsertAsync(EditorPane pane)
    {
        var table = pane.Document.Table!; var columns = table.Columns; int rows = table.Records.Count;
        Require(columns.Length > 30 && rows > 2, "Row-insert fixture needs offscreen columns and several rows.");
        pane.Jump(1, columns[30]); await Task.Delay(100); UpdateLayout();
        var gridColumns = pane.TableGrid.Columns.ToArray();
        Require(pane.ColumnIndexOf(pane.TableGrid.CurrentColumn!) == 30 && pane.SelectedColumn == columns[30], "Jump did not land on the offscreen column.");
        var timer = Stopwatch.StartNew(); pane.InsertRows(1); timer.Stop(); await Task.Delay(60);
        Require(pane.Document.Table!.Records.Count == rows + 1 && pane.SelectedRow == 1 && pane.SelectedColumn == columns[30] && pane.SelectedCells.SequenceEqual([(1, 30)]), "Add row above did not select the new row in the current column.");
        Require(pane.TableGrid.Columns.SequenceEqual(gridColumns), "Adding a row rebuilt the grid columns.");
        Require(pane.Document.Diagnostics.Any(d => d.Severity == "Warning" && d.Message == TableData.RowOrderAdvice) && pane.Document.Diagnostics.All(d => d.Severity != "Error"), "Inserting before original rows should raise only the order advisory.");
        var below = Stopwatch.StartNew(); pane.InsertRows(3, 2); below.Stop(); await Task.Delay(60);
        Require(pane.Document.Table!.Records.Count == rows + 3 && pane.SelectedCells.ToHashSet().SetEquals([(3, 30), (4, 30)]) && pane.SelectedRow == 3, "Add rows below did not select both new rows.");
        Console.WriteLine($"Row insert on {columns.Length} columns x {rows} rows: above {timer.Elapsed.TotalMilliseconds:F1} ms, two below {below.Elapsed.TotalMilliseconds:F1} ms");
        pane.DeleteSelectedRows(); pane.Document.Undo(); pane.Refresh(); await Task.Delay(60);
        Require(pane.Document.Table!.Records.Count == rows + 3, "Undo after deleting inserted rows did not restore them.");
        pane.Document.Undo(); pane.Document.Undo(); pane.Refresh(); await Task.Delay(60);
        Require(pane.Document.Table!.Records.Count == rows && pane.Document.Diagnostics.All(d => d.Severity != "Warning") && !pane.Document.IsDirty, "Undoing the inserts did not restore the table and clear the advisory.");
        var dialog = new AddRowsWindow();
        var prompt = dialog.ShowDialog<int?>(this); await Task.Delay(60);
        var input = dialog.GetVisualDescendants().OfType<TextBox>().Single();
        var add = dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Add rows"));
        input.Text = "0"; Require(!add.IsEnabled, "Add rows must reject zero.");
        input.Text = "100001"; Require(!add.IsEnabled, "Add rows must reject excessive counts.");
        input.Text = "3"; Require(add.IsEnabled, "Add rows must accept a valid count.");
        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var count = await prompt;
        Require(count == 3, "Add rows dialog did not return the requested count.");

        pane.InsertRows(pane.Document.Table!.Records.Count, count!.Value); await Task.Delay(60);
        Require(pane.Document.Table!.Records.Count == rows + 3 && pane.SelectedRow == rows &&
            pane.SelectedCells.ToHashSet().SetEquals([(rows, 30), (rows + 1, 30), (rows + 2, 30)]),
            "Bulk append did not select the requested blank rows in the current column.");
        Require(pane.Document.Diagnostics.All(d => d.Severity != "Warning") && pane.TableGrid.Columns.SequenceEqual(gridColumns),
            "Bulk append should not shift original row IDs or rebuild grid columns.");
        pane.Document.Undo(); pane.Refresh();
        Require(pane.Document.Table!.Records.Count == rows && !pane.Document.IsDirty, "Bulk append must undo in one step.");

    }
}
