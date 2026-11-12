using Avalonia.Input;
using Avalonia.Threading;

namespace ModStudio.App;

public partial class MainWindow
{
    private long lastShiftTap;
    private bool shiftHeld;

    private void InitializeWorkspaceShortcuts()
    {
        KeyDown += (_, e) =>
        {
            if (e.Key is Key.LeftShift or Key.RightShift)
            {
                if (shiftHeld) return;
                shiftHeld = true;
                var now = Environment.TickCount64;
                if (lastShiftTap != 0 && now - lastShiftTap <= 500)
                {
                    lastShiftTap = 0;
                    SetPanelVisible("explorer", true);
                    LeftTabs.SelectedIndex = 0;
                    Dispatcher.UIThread.Post(() =>
                    {
                        ExplorerSearch.Focus();
                        ExplorerSearch.SelectAll();
                    }, DispatcherPriority.Input);
                    e.Handled = true;
                }
                else lastShiftTap = now;
                return;
            }

            // Any intervening key means two Shift presses are no longer a double-Shift gesture.
            lastShiftTap = 0;
            var command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
            if (command && e.Key == Key.F && Active?.FocusRowFilter() == true) e.Handled = true;
        };

        KeyUp += (_, e) =>
        {
            if (e.Key is Key.LeftShift or Key.RightShift) shiftHeld = false;
        };
    }
}
