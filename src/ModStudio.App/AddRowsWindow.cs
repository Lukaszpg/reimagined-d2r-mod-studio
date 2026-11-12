using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace ModStudio.App;

/// <summary>Asks how many blank rows to append to the current table.</summary>
public sealed class AddRowsWindow : Window
{
    private const int MaxRowsPerOperation = 100_000;

    public AddRowsWindow()
    {
        Title = "Add rows";
        Width = 390;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new(20), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "Number of blank rows to append to the end of the table:", TextWrapping = TextWrapping.Wrap });
        var count = new TextBox { Text = "1", Width = 150, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(count);
        var error = new TextBlock { Text = "Enter a whole number between 1 and 100,000.", Foreground = new SolidColorBrush(Color.Parse("#E39A6B")), IsVisible = false };
        panel.Children.Add(error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var cancel = new Button { Content = "Cancel" };
        var add = new Button { Content = "Add rows", Classes = { "accent" } };
        buttons.Children.Add(cancel);
        buttons.Children.Add(add);
        panel.Children.Add(buttons);
        Content = panel;

        bool ValidCount(out int number) => int.TryParse(count.Text, out number) && number is >= 1 and <= MaxRowsPerOperation;
        count.TextChanged += (_, _) =>
        {
            add.IsEnabled = ValidCount(out _);
            error.IsVisible = !add.IsEnabled;
        };
        count.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(null); e.Handled = true; }
            else if (e.Key == Key.Enter && ValidCount(out int number)) { Close((int?)number); e.Handled = true; }
        };
        cancel.Click += (_, _) => Close(null);
        add.Click += (_, _) => { if (ValidCount(out int number)) Close((int?)number); };
        Opened += (_, _) => { count.Focus(); count.SelectAll(); };
    }
}
