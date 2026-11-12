using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

public partial class MainWindow
{
    private async Task<bool> ReviewDeploymentOwnershipAsync(DeploymentOwnershipConflict conflict)
        => await ChooseAsync("Reuse existing deployment?",
            $"{conflict.Target}\n\nThis folder was deployed by project {conflict.PreviousProjectId}. Converting or re-importing a project can give it a new identity.\n\nBack up files that this build will replace, including the old ownership record, under this project's .studio/deployment-backups folder, then deploy and assign this folder to the current project? Files outside this build will remain in place.",
            "Back up and reuse this folder", "Cancel") == "Back up and reuse this folder";
    private readonly DispatcherTimer externalTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private FileSystemWatcher? externalWatcher;
    private bool externalBusy, externalActive, externalPending, externalLaunching;
    private DateTime externalChanged, externalScan;
    private string externalStatus = "No external editing session.";

    private void InitializeExternalEditor()
    {
        externalTimer.Tick += async (_, _) =>
        {
            if (!externalActive || externalBusy || operation != null || DateTime.UtcNow - externalChanged < TimeSpan.FromMilliseconds(900)) return;
            if (!externalPending && DateTime.UtcNow - externalScan < TimeSpan.FromSeconds(30)) return;
            await SyncExternalAsync(false);
        };
        externalTimer.Start();
        Closed += (_, _) => { externalTimer.Stop(); externalWatcher?.Dispose(); };
    }
    private void WatchExternalSession()
    {
        externalWatcher?.Dispose(); externalWatcher = null; externalActive = false;
        if (project == null) return;
        var session = ExternalEditorSync.Load(project); externalActive = session != null;
        if (session == null) { externalStatus = "No external editing session."; return; }
        externalPending = true; externalChanged = DateTime.UtcNow;
        externalStatus = $"External editing · {session.Profile} · {session.Target}";
        if (!Directory.Exists(session.Target)) { Status.Text = "External deployment is unavailable. Restore its folder before syncing."; return; }
        externalWatcher = new(session.Target) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
        void Changed(object? sender, FileSystemEventArgs e)
        {
            if (!e.FullPath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && e is not RenamedEventArgs) return;
            Dispatcher.UIThread.Post(() => { externalPending = true; externalChanged = DateTime.UtcNow; });
        }
        externalWatcher.Changed += Changed; externalWatcher.Created += Changed; externalWatcher.Deleted += Changed; externalWatcher.Renamed += Changed;
        externalWatcher.Error += (_, _) => Dispatcher.UIThread.Post(() => externalPending = true);
        externalWatcher.EnableRaisingEvents = true;
    }
    private bool IsExternalTable(string path) => project != null &&
        (Contains(System.IO.Path.Combine(project.Root, "source/tables"), path) && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
         Contains(System.IO.Path.Combine(project.Root, "data/global/excel"), path) && path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
    private ProjectFileEditor? ProjectFileEditorFor(string path)
    {
        if (project == null) return null;
        var settings = StudioPreferences.Load(StudioPreferences.DefaultFile).ExternalEditor;
        return (settings.ProjectFileEditors ?? []).FirstOrDefault(editor => editor.Matches(project, path));
    }
    private bool HasProjectFileEditor(string path) => ProjectFileEditorFor(path) != null;
    private void RefreshProjectFileEditorActions()
    {
        foreach (var pane in tabs.Select(tab => tab.Content).OfType<EditorPane>()) pane.RefreshExternalEditorAction();
    }
    private async Task OpenProjectFileEditorAsync(string source)
    {
        try
        {
            Require(project != null && operation == null && !externalBusy, "Open a project and wait for the current operation.");
            var mapping = ProjectFileEditorFor(source);
            Require(mapping != null, "No external editor is configured for this project file.");
            if (!await SaveAllAsync()) return;
            using var process = Process.Start(mapping!.StartInfo(project!));
            Status.Text = $"Opened {Relative(project!.Root, source)} in its configured external editor.";
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private MenuItem CreateExternalEditorItem(string? source = null)
    {
        var item = new MenuItem { Header = source == null ? "Open External Editor Workspace…" : "Open External Editor…", IsEnabled = source == null || IsExternalTable(source) };
        item.Click += async (_, _) => await OpenExternalAsync(source); return item;
    }
    private async Task<bool> SyncExternalAsync(bool interactive)
    {
        if (project == null || externalBusy) return false;
        externalBusy = true; externalPending = false; externalScan = DateTime.UtcNow;
        var current = project;
        // Prevent typing from racing the disk transaction; dirty documents remain untouched until saved.
        bool enabled = RootGrid.IsEnabled; RootGrid.IsEnabled = false;
        try
        {
            var dirty = tabs.Select(t => t.Content).OfType<EditorPane>().Where(p => p.Document.IsDirty || p.Document.InEditGroup).Select(p => p.Document.FilePath).ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            IReadOnlyList<string> changed;
            try { changed = await Task.Run(() => ExternalEditorSync.Synchronize(current, dirty)); }
            catch (ExternalSyncConflict conflict) when (interactive)
            {
                var answer = await ChooseAsync("External editing conflicts", conflict.Message + "\n\nChoose which values to keep for the conflicting cells. Other cell edits will still merge.", "Keep Studio values", "Keep external values", "Cancel");
                if (answer is not ("Keep Studio values" or "Keep external values")) return false;
                changed = await Task.Run(() => ExternalEditorSync.Synchronize(current, dirty, answer == "Keep external values" ? ExternalConflictChoice.External : ExternalConflictChoice.Studio, conflict.Stamp));
            }
            if (project != current) return false;
            foreach (var pane in tabs.Select(t => t.Content).OfType<EditorPane>())
                if (!pane.Document.IsDirty && !pane.Document.InEditGroup && changed.Contains(pane.Document.FilePath) && pane.Document.ExternalChange) { pane.Document.ReloadClean(); pane.Refresh(); }
            externalStatus = "External editor synchronized · " + DateTime.Now.ToString("T");
            if (changed.Count > 0 || interactive) { Status.Text = externalStatus; git.QueueRefresh(); }
            return true;
        }
        catch (Exception e)
        {
            externalStatus = "External sync paused: " + e.Message; Status.Text = externalStatus;
            if (interactive) ShowError(e); return false;
        }
        finally { externalBusy = false; RootGrid.IsEnabled = enabled; }
    }
    private async Task OpenExternalAsync(string? source)
    {
        if (externalLaunching) return;
        externalLaunching = true;
        try
        {
            Require(project != null && operation == null && !externalBusy, "Open a project and wait for the current operation.");
            if (source != null) Require(IsExternalTable(source), "Select a source TXT table.");
            var prefs = StudioPreferences.Load(StudioPreferences.DefaultFile);
            if (!prefs.ExternalEditor.Configured && string.IsNullOrWhiteSpace(prefs.ExternalEditor.Executable))
            {
                if (!await ExternalEditorSettingsAsync()) return;
                prefs = StudioPreferences.Load(StudioPreferences.DefaultFile);
            }
            if (!await SaveAllAsync()) return;
            if (NativeExternalEditorTarget.Resolve(project!, source) is { } native)
            {
                using var nativeProcess = Process.Start(prefs.ExternalEditor.StartInfo(native.File, native.Workspace, source == null));
                Status.Text = "Native TXT source opened in external editor. Saves update the project directly.";
                return;
            }
            if (!await SyncExternalAsync(true)) return;
            var current = project!; var profile = Profile; var settings = RunSettings.Load(current, profile);
            Require(!string.IsNullOrWhiteSpace(settings.DeploymentDirectory), "Choose a deployment folder in Run settings first.");
            // Validate the configured executable before changing the deployment.
            Require(string.IsNullOrWhiteSpace(prefs.ExternalEditor.Executable) || File.Exists(prefs.ExternalEditor.Executable), "External editor executable is unavailable.");
            operation = new();
            try
            {
                var build = await controller.ExecuteAsync(current, profile, settings, true, false, operation.Token, Log, ReviewDeploymentOwnershipAsync);
                var session = await Task.Run(() => ExternalEditorSync.Begin(current, build, settings.DeploymentDirectory));
                WatchExternalSession();
                var workspace = Inside(session.Target, build.ModName + ".mpq/data/global/excel");
                var file = "";
                if (source != null)
                {
                    var table = session.Tables.Single(t => t.Source == Relative(current.Root, source));
                    var output = table.Outputs.Keys.First();
                    if (table.Outputs.Count > 1)
                    {
                        var selected = await ChooseAsync("Choose TXT bank", "This table generates multiple TXT files. Choose the file to open.", [.. table.Outputs.Keys, "Cancel"]);
                        if (selected == null || selected == "Cancel") return;
                        output = selected;
                    }
                    file = Inside(session.Target, output);
                }
                // One TXT per table: sibling banks receive the same edits on sync, so opening each of them only duplicates tabs.
                var tracked = session.Tables.Select(t => Inside(session.Target, t.Outputs.Keys.First())).Where(File.Exists).ToList();
                using var process = Process.Start(prefs.ExternalEditor.StartInfo(file, workspace, source == null, tracked));
                Status.Text = source == null ? "External workspace opened. Saved TXT edits sync while Studio is open." : "External table opened. Saved TXT edits sync while Studio is open.";
            }
            finally { operation.Dispose(); operation = null; }
        }
        catch (OperationCanceledException) { Status.Text = "Opening external editor canceled."; }
        catch (Exception e) { ShowError(e); }
        finally { externalLaunching = false; }
    }
    private async Task<bool> ExternalEditorSettingsAsync()
    {
        Require(project != null, "Open a project first.");
        var prefs = StudioPreferences.Load(StudioPreferences.DefaultFile); var current = prefs.ExternalEditor;
        var dialog = new Window { Title = "External editor", Width = 720, Height = 780, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new(20), Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "Use any TXT editor for individual tables or the deployed Excel folder. Leave the executable empty to use the system default application (folders open in the file manager). Project-file editors below are direct per-file launch shortcuts and do not use the TXT synchronization workflow.", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        TextBox Field(string label, string value, bool multiline = false)
        {
            panel.Children.Add(new TextBlock { Text = label }); var box = new TextBox { Text = value, AcceptsReturn = multiline, MinHeight = multiline ? 70 : 30 }; panel.Children.Add(box); return box;
        }
        var executable = Field("TXT / workspace editor executable", current.Executable);
        var browse = new Button { Content = "Browse editor…" }; panel.Children.Add(browse);
        browse.Click += async (_, _) => { var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose external editor", AllowMultiple = false }); if (files.Count > 0) executable.Text = files[0].TryGetLocalPath(); };
        var fileArgs = Field("TXT file arguments · one argument per line", string.Join("\n", current.FileArguments ?? ["{file}"]), true);
        var workspaceArgs = Field("Workspace arguments · one argument per line", string.Join("\n", current.WorkspaceArguments ?? ["{workspace}"]), true);
        executable.TextChanged += (_, _) =>
        {
            if (System.IO.Path.GetFileNameWithoutExtension(executable.Text ?? "").Equals("txteditor", StringComparison.OrdinalIgnoreCase) && workspaceArgs.Text == "{workspace}") workspaceArgs.Text = "{files}";
        };
        panel.Children.Add(new TextBlock { Text = "{file}: selected TXT. {workspace}: deployed Excel folder. {files}: each TXT in that folder as a separate argument. Do not add quotes. TXTeditor accepts file paths; use {files} to open all workspace tables.", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var preset = new Button { Content = "Use TXTeditor arguments" }; preset.Click += (_, _) => { fileArgs.Text = "{file}"; workspaceArgs.Text = "{files}"; }; panel.Children.Add(preset);

        panel.Children.Add(new Separator { Margin = new(0, 8) });
        panel.Children.Add(new TextBlock { Text = "PROJECT FILE EDITORS", FontWeight = Avalonia.Media.FontWeight.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = "Map any file inside the current project to an external application. A matching open file gets an Open external editor icon as the last square toolbar action. Arguments are passed exactly as separate arguments; use {file}, {fileDir}, or {project}.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        var error = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var mappingHost = new StackPanel { Spacing = 8 };
        panel.Children.Add(mappingHost);
        var mappingRows = new List<(StackPanel Panel, TextBox ProjectFile, TextBox Executable, TextBox Arguments)>();

        void AddMapping(ProjectFileEditor? configured = null)
        {
            var card = new StackPanel { Spacing = 6, Margin = new(0, 4, 0, 8) };
            var heading = new DockPanel();
            var remove = new Button { Content = "Remove", HorizontalAlignment = HorizontalAlignment.Right };
            DockPanel.SetDock(remove, Dock.Right); heading.Children.Add(remove);
            heading.Children.Add(new TextBlock { Text = "Project file editor", VerticalAlignment = VerticalAlignment.Center });
            card.Children.Add(heading);

            var projectFile = new TextBox { Text = configured?.ProjectFile ?? "", PlaceholderText = "Project-relative file, e.g. data/hd/ui/example.json" };
            card.Children.Add(projectFile);
            var browseProject = new Button { Content = "Browse project file…", HorizontalAlignment = HorizontalAlignment.Left };
            card.Children.Add(browseProject);

            var app = new TextBox { Text = configured?.Executable ?? "", PlaceholderText = "External application executable" };
            card.Children.Add(app);
            var browseApp = new Button { Content = "Browse application…", HorizontalAlignment = HorizontalAlignment.Left };
            card.Children.Add(browseApp);

            var arguments = new TextBox { Text = string.Join("\n", configured?.Arguments ?? ["{file}"]), AcceptsReturn = true, MinHeight = 64 };
            card.Children.Add(new TextBlock { Text = "Arguments · one argument per line" });
            card.Children.Add(arguments);
            card.Children.Add(new Separator());

            mappingHost.Children.Add(card);
            var row = (card, projectFile, app, arguments);
            mappingRows.Add(row);
            remove.Click += (_, _) => { mappingRows.Remove(row); mappingHost.Children.Remove(card); };
            browseProject.Click += async (_, _) =>
            {
                try
                {
                    var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose a project file", AllowMultiple = false });
                    var selected = files.FirstOrDefault()?.TryGetLocalPath();
                    if (selected == null) return;
                    Require(Contains(project!.Root, selected) && !Contains(project.Cache, selected), "Choose a file inside the current project, outside .studio.");
                    projectFile.Text = Relative(project.Root, selected);
                    error.Text = "";
                }
                catch (Exception ex) { error.Text = ex.Message; }
            };
            browseApp.Click += async (_, _) =>
            {
                var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose external application", AllowMultiple = false });
                if (files.Count > 0) app.Text = files[0].TryGetLocalPath();
            };
        }

        foreach (var mapping in current.ProjectFileEditors ?? []) AddMapping(mapping);
        var addMapping = new Button { Content = "Add project file editor", HorizontalAlignment = HorizontalAlignment.Left };
        addMapping.Click += (_, _) => AddMapping();
        panel.Children.Add(addMapping);
        panel.Children.Add(error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; var save = new Button { Content = "Save" }; var cancel = new Button { Content = "Cancel" }; buttons.Children.Add(save); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        cancel.Click += (_, _) => dialog.Close(false);
        save.Click += (_, _) =>
        {
            try
            {
                Require(string.IsNullOrWhiteSpace(executable.Text) || File.Exists(executable.Text), "Choose an existing TXT/workspace editor executable.");
                string[] Args(TextBox box) => (box.Text ?? "").Split('\n').Select(s => s.TrimEnd('\r')).Where(s => s.Length > 0).ToArray();

                var mappings = new List<ProjectFileEditor>();
                var resolved = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                foreach (var row in mappingRows)
                {
                    if (string.IsNullOrWhiteSpace(row.ProjectFile.Text) && string.IsNullOrWhiteSpace(row.Executable.Text)) continue;
                    Require(!string.IsNullOrWhiteSpace(row.ProjectFile.Text), "Choose a project file for every project-file editor.");
                    Require(!string.IsNullOrWhiteSpace(row.Executable.Text), "Choose an executable for every project-file editor.");
                    var candidate = new ProjectFileEditor(row.ProjectFile.Text!.Trim(), row.Executable.Text!.Trim(), Args(row.Arguments));
                    candidate.Validate(project!);
                    var target = candidate.ResolveFile(project!);
                    Require(resolved.Add(target), "Only one project-file external editor can be configured for the same file.");
                    mappings.Add(candidate with { ProjectFile = Relative(project!.Root, target), Arguments = candidate.Arguments is { Length: > 0 } ? candidate.Arguments : ["{file}"] });
                }

                prefs.ExternalEditor = new(executable.Text?.Trim() ?? "", Args(fileArgs), Args(workspaceArgs), true, mappings.ToArray());
                prefs.Save(StudioPreferences.DefaultFile);
                RefreshProjectFileEditorActions();
                dialog.Close(true);
            }
            catch (Exception e) { error.Text = e.Message; }
        };
        dialog.Content = new ScrollViewer { Content = panel }; return await dialog.ShowDialog<bool>(this);
    }
    private async void ExternalEditorClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var menu = new ContextMenu();
            menu.Items.Add(CreateExternalEditorItem());
            if (Active is { } pane && IsExternalTable(pane.Document.FilePath)) menu.Items.Add(CreateExternalEditorItem(pane.Document.FilePath));
            var settings = new MenuItem { Header = "External editor settings…" }; settings.Click += async (_, _) => await ExternalEditorSettingsAsync(); menu.Items.Add(settings);
            var sync = new MenuItem { Header = "Synchronize / resolve conflicts…", IsEnabled = externalActive }; sync.Click += async (_, _) => { if (await SaveAllAsync()) await SyncExternalAsync(true); }; menu.Items.Add(sync);
            var end = new MenuItem { Header = "End external editing", IsEnabled = externalActive };
            end.Click += async (_, _) => { try { if (await SaveAllAsync() && await SyncExternalAsync(true)) { ExternalEditorSync.End(project!); WatchExternalSession(); Status.Text = "External editing ended."; } } catch (Exception ex) { ShowError(ex); } }; menu.Items.Add(end);
            menu.Items.Add(new Separator()); menu.Items.Add(new MenuItem { Header = externalStatus, IsEnabled = false });
            if (sender is Control control) { control.ContextMenu = menu; menu.Open(control); }
        }
        catch (Exception ex) { ShowError(ex); }
        await Task.CompletedTask;
    }
}
