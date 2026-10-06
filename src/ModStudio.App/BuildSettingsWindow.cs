using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

/// <summary>Local GitHub/CMake settings. The PAT is stored separately in Windows Credential Manager.</summary>
internal sealed class BuildSettingsWindow : Window
{
    private sealed record RevisionOption(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#D8BC86"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#B9AD97"));
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#E39A6B"));

    private readonly TextBox repository = new() { Width = 430 };
    private readonly ComboBox revisionKind = new() { ItemsSource = new[] { "Branch", "Pull request" }, Width = 160 };
    private readonly ComboBox revision = new() { Width = 430, MaxDropDownHeight = 320, PlaceholderText = "Loading from GitHub…" };
    private readonly TextBox deploySubdirectory = new() { Width = 430 };
    private readonly TextBox cmakeExecutable = new() { Width = 430 };
    private readonly TextBox configureArguments = new() { Width = 430 };
    private readonly TextBox token = new() { Width = 430, PasswordChar = '●' };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Muted };
    private string loadedRevisionKind = "";

    public BuildSettingsWindow(CMakeBuildSettings current)
    {
        Title = "Build settings"; Width = 760; Height = 690; MinWidth = 620; MinHeight = 560; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(26), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "GitHub / CMake build", FontSize = 24, Foreground = Accent });
        panel.Children.Add(new TextBlock
        {
            Foreground = Muted,
            TextWrapping = TextWrapping.Wrap,
            Text = "Optional per-project plugin build. Build resolves the selected branch or pull request to an immutable commit SHA, downloads that source into .studio, reads its charsi-package.json build contract, and runs CMake locally. Deploy attaches the resulting DLL to Studio's ordinary build so the existing deployment journal writes and rolls back mod data + DLL as one transaction. Leave Repository empty to disable this integration."
        });

        repository.Text = current.Repository;
        revisionKind.SelectedIndex = current.RevisionKind == "pull-request" ? 1 : 0;
        deploySubdirectory.Text = current.DeploySubdirectory;
        cmakeExecutable.Text = current.CMakeExecutable;
        configureArguments.Text = JsonSerializer.Serialize(current.ConfigureArguments ?? []);

        panel.Children.Add(Row("Repository", repository));
        var revisionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        revisionRow.Children.Add(revisionKind); revisionRow.Children.Add(revision);
        var refresh = new Button { Content = "Refresh" }; revisionRow.Children.Add(refresh);
        panel.Children.Add(Row("Revision", revisionRow));
        panel.Children.Add(new TextBlock { Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Text = "Branches and open pull requests are fetched from GitHub. Changing the revision type reloads this list. A build resolves the selected value again immediately before downloading, so the log records the exact commit SHA that was built." });

        panel.Children.Add(Row("DLL deploy subfolder", deploySubdirectory));
        panel.Children.Add(Row("CMake executable", cmakeExecutable));
        panel.Children.Add(Row("Configure arguments", configureArguments));
        panel.Children.Add(new TextBlock { Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Text = "CMake source, target and DLL name come from charsi-package.json in the selected revision, exactly as in Charsi. Deploy subfolder is relative to the existing Run settings deployment folder (normally d2rloader/plugins). Leave CMake executable empty to discover it from PATH or Visual Studio. Configure arguments use a JSON string array." });

        panel.Children.Add(new TextBlock { Text = "GITHUB TOKEN", Foreground = Accent, FontSize = 11, FontWeight = FontWeight.SemiBold, Margin = new(0, 8, 0, 0) });
        panel.Children.Add(Row("New token", token));
        var tokenButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var saveToken = new Button { Content = "Authenticate & save token" };
        var clearToken = new Button { Content = "Clear saved token" };
        tokenButtons.Children.Add(saveToken); tokenButtons.Children.Add(clearToken); panel.Children.Add(tokenButtons);
        panel.Children.Add(new TextBlock { Foreground = Warn, FontSize = 11, TextWrapping = TextWrapping.Wrap, Text = "A saved token is kept only in Windows Credential Manager, never in project/preferences JSON or CMake arguments. Building a pull request runs that revision's CMake scripts on your machine; only build revisions you trust." });
        panel.Children.Add(status);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new(0, 8, 0, 0) };
        var disable = new Button { Content = "Disable remote build" };
        var cancel = new Button { Content = "Cancel" };
        var save = new Button { Content = "Save", Classes = { "accent" } };
        buttons.Children.Add(disable); buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

        void CredentialStatus()
        {
            if (!GitHubCredentialStore.Available)
            {
                status.Text = "Secure PAT persistence is available on Windows. Public GitHub repositories can still be refreshed without a token.";
                saveToken.IsEnabled = clearToken.IsEnabled = false;
                return;
            }
            try
            {
                var saved = GitHubCredentialStore.Read();
                status.Text = saved == null ? "No GitHub token is stored." : "Saved in Windows Credential Manager · " + GitHubCredentialStore.Describe(saved);
                clearToken.IsEnabled = saved != null;
            }
            catch (Exception ex) { status.Text = ex.Message; status.Foreground = Warn; }
        }

        async Task SaveTokenAsync()
        {
            try
            {
                var candidate = (token.Text ?? "").Trim();
                Require(candidate.Length > 0, "Paste a GitHub token first.");
                using (var before = new GitHubSourceClient(candidate))
                {
                    var login = await before.GetUserAsync();
                    status.Text = $"Authenticated as {login}; saving token…";
                }
                GitHubCredentialStore.Save(candidate);
                var saved = GitHubCredentialStore.Read();
                Require(saved == candidate, "Saved token does not match the authenticated token.");
                using var after = new GitHubSourceClient(saved);
                var confirmed = await after.GetUserAsync();
                token.Text = "";
                status.Foreground = Muted;
                status.Text = $"Saved securely in Windows Credential Manager · authenticated as {confirmed} · {GitHubCredentialStore.Describe(saved!)}";
                clearToken.IsEnabled = true;
                await RefreshRevisionsAsync();
            }
            catch (Exception ex) { status.Foreground = Warn; status.Text = ex.Message; }
        }

        async Task RefreshRevisionsAsync(bool restoreSaved = false)
        {
            var kind = revisionKind.SelectedIndex == 1 ? "pull-request" : "branch";
            var previous = loadedRevisionKind == kind ? (revision.SelectedItem as RevisionOption)?.Value : null;
            if (restoreSaved && current.RevisionKind == kind) previous = current.Revision.TrimStart('#');

            try
            {
                var repo = GitHubRepository.Parse(repository.Text ?? "");
                var candidate = (token.Text ?? "").Trim();
                if (candidate.Length == 0) candidate = GitHubCredentialStore.Read() ?? "";
                using var github = new GitHubSourceClient(candidate.Length == 0 ? null : candidate);
                revision.IsEnabled = false;
                refresh.IsEnabled = false;
                status.Foreground = Muted;
                status.Text = kind == "pull-request" ? "Loading open pull requests from GitHub…" : "Loading branches from GitHub…";

                RevisionOption[] options;
                if (kind == "pull-request")
                {
                    var pulls = await github.GetPullRequestsAsync(repo);
                    options = pulls.Select(p => new RevisionOption(p.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), p.ToString())).ToArray();
                    status.Text = $"Loaded {pulls.Length} open pull request(s).";
                }
                else
                {
                    var branches = await github.GetBranchesAsync(repo);
                    options = branches.Select(name => new RevisionOption(name, name)).ToArray();
                    status.Text = $"Loaded {branches.Length} branch(es).";
                }

                revision.ItemsSource = options;
                revision.SelectedItem = options.FirstOrDefault(option => option.Value.Equals(previous, StringComparison.OrdinalIgnoreCase));
                if (revision.SelectedItem == null && options.Length > 0) revision.SelectedIndex = 0;
                revision.PlaceholderText = options.Length == 0
                    ? (kind == "pull-request" ? "No open pull requests" : "No branches found")
                    : "Select revision";
                loadedRevisionKind = kind;
            }
            catch (Exception ex)
            {
                revision.ItemsSource = Array.Empty<RevisionOption>();
                revision.SelectedItem = null;
                revision.PlaceholderText = "GitHub revisions unavailable";
                loadedRevisionKind = "";
                status.Foreground = Warn;
                status.Text = ex.Message;
            }
            finally
            {
                revision.IsEnabled = true;
                refresh.IsEnabled = true;
            }
        }

        CMakeBuildSettings Build()
        {
            var kind = revisionKind.SelectedIndex == 1 ? "pull-request" : "branch";
            var selected = revision.SelectedItem as RevisionOption;
            Require(selected != null, kind == "pull-request" ? "Select a pull request from GitHub." : "Select a branch from GitHub.");
            var extra = JsonSerializer.Deserialize<string[]>(string.IsNullOrWhiteSpace(configureArguments.Text) ? "[]" : configureArguments.Text!) ?? [];
            var next = new CMakeBuildSettings(
                (repository.Text ?? "").Trim(),
                kind,
                selected.Value,
                (deploySubdirectory.Text ?? "").Trim(),
                (cmakeExecutable.Text ?? "").Trim(),
                extra);
            next.Validate();
            return next;
        }

        saveToken.Click += async (_, _) => await SaveTokenAsync();
        clearToken.Click += (_, _) => { try { GitHubCredentialStore.Delete(); CredentialStatus(); } catch (Exception ex) { status.Foreground = Warn; status.Text = ex.Message; } };
        refresh.Click += async (_, _) => await RefreshRevisionsAsync();
        revisionKind.SelectionChanged += async (_, _) => await RefreshRevisionsAsync();
        repository.LostFocus += async (_, _) => await RefreshRevisionsAsync();
        Opened += async (_, _) => await RefreshRevisionsAsync(true);
        disable.Click += (_, _) => Close(new CMakeBuildSettings());
        cancel.Click += (_, _) => Close(null);
        save.Click += (_, _) => { try { Close(Build()); } catch (Exception ex) { status.Foreground = Warn; status.Text = ex.Message; } };
        CredentialStatus();
    }

    private static Control Row(string label, Control input)
    {
        var grid = new Grid { ColumnDefinitions = new("170,*") };
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(input, 1); grid.Children.Add(input);
        return grid;
    }
}
