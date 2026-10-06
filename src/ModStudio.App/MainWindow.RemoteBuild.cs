using Avalonia.Controls;
using Avalonia.Interactivity;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

public partial class MainWindow
{
    private string SelectedCMakeBuildType =>
        (BuildTypePicker.SelectedItem as ComboBoxItem)?.Content?.ToString() == "Debug" ? "Debug" : "Release";

    private async void BuildSettingsClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            Require(project != null && operation == null && !externalBusy, "Open a project and wait for the current operation.");
            var current = CMakeBuildSettings.Load(project!);
            var next = await new BuildSettingsWindow(current).ShowDialog<CMakeBuildSettings?>(this);
            if (next == null) return;
            next.Save(project!);
            Status.Text = next.Configured
                ? $"GitHub/CMake build saved · {GitHubRepository.Parse(next.Repository).FullName} · {(next.RevisionKind == "pull-request" ? "PR #" : "")}{next.Revision}"
                : "GitHub/CMake build disabled for this project.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task<RemoteBuildArtifact> BuildConfiguredDllAsync(CMakeBuildSettings settings, CancellationToken token)
    {
        Require(project != null, "Open a project first.");
        Log($"Remote CMake build starting · {SelectedCMakeBuildType} · {settings.Repository} · {settings.RevisionKind} {settings.Revision}");
        return await RemoteCMakeBuildService.BuildAsync(project!, settings, SelectedCMakeBuildType, token, Log);
    }

}
