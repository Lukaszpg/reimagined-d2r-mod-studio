using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

public partial class MainWindow
{
    private string SelectedCMakeBuildType =>
        (BuildTypePicker.SelectedItem as ComboBoxItem)?.Content?.ToString() == "Debug" ? "Debug" : "Release";

    private bool remoteStatusChecking;
    private DateTime remoteStatusCheckedUtc = DateTime.MinValue;

    private void InitializeRemoteDeploymentStatus()
    {
        Activated += async (_, _) => await RefreshRemoteDeploymentStatusAsync();
    }

    private void SetPlayRevisionState(bool? current, string tooltip)
    {
        if (current == null)
        {
            PlayButton.ClearValue(TemplatedControl.BackgroundProperty);
            PlayButton.ClearValue(TemplatedControl.BorderBrushProperty);
        }
        else
        {
            var color = Color.Parse(current.Value ? "#2E7D32" : "#9D3333");
            PlayButton.Background = new SolidColorBrush(color);
            PlayButton.BorderBrush = new SolidColorBrush(color);
        }
        ToolTip.SetTip(PlayButton, tooltip);
    }

    private async Task RefreshRemoteDeploymentStatusAsync(bool force = false)
    {
        if (project == null)
        {
            SetPlayRevisionState(null, "Build, deploy and launch the configured game target.");
            return;
        }
        if (remoteStatusChecking) return;
        if (!force && DateTime.UtcNow - remoteStatusCheckedUtc < TimeSpan.FromSeconds(15)) return;

        var currentProject = project;
        var profile = Profile;
        remoteStatusChecking = true;
        try
        {
            var settings = CMakeBuildSettings.Load(currentProject);
            if (!settings.Configured)
            {
                SetPlayRevisionState(null, "Build, deploy and launch the configured game target.");
                remoteStatusCheckedUtc = DateTime.UtcNow;
                return;
            }

            var runSettings = RunSettings.Load(currentProject, profile);
            var deployed = DeploymentService.ReadManifest(runSettings.DeploymentDirectory);
            using var github = new GitHubSourceClient(GitHubCredentialStore.Read());
            var selected = await github.ResolveAsync(settings);
            if (project != currentProject || Profile != profile) return;

            var sameRepository = deployed?.SourceRepository != null
                && deployed.SourceRepository.Equals(selected.Repository, StringComparison.OrdinalIgnoreCase);
            var sameSha = deployed?.SourceRevisionSha != null
                && deployed.SourceRevisionSha.Equals(selected.Sha, StringComparison.OrdinalIgnoreCase);
            var isCurrent = sameRepository && sameSha;
            var deployedSha = deployed?.SourceRevisionSha is { Length: > 0 } sha ? (sha.Length > 12 ? sha[..12] : sha) : "none";
            SetPlayRevisionState(isCurrent,
                isCurrent
                    ? $"Current {settings.RevisionKind} {settings.Revision} ({selected.ShortSha}) is deployed."
                    : $"Current {settings.RevisionKind} {settings.Revision} is {selected.ShortSha}; deployed revision is {deployedSha}.");
            remoteStatusCheckedUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            if (project == currentProject && Profile == profile)
                SetPlayRevisionState(false, "Could not verify whether the selected GitHub revision is deployed: " + ex.Message);
        }
        finally { remoteStatusChecking = false; }
    }

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
            await RefreshRemoteDeploymentStatusAsync(true);
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
