using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ModStudio.Core;

namespace ModStudio.App;

/// <summary>
/// What the visual form starts from. <see cref="Id"/> is null when the form makes a new row (it asks for the id, starting from
/// <see cref="SuggestedId"/>); otherwise it is the row whose visual changes. <see cref="IdProblem"/> refuses ids the table
/// cannot take. <see cref="Current"/> is the visual the row has now, preselected.
/// </summary>
internal sealed record NewVisualRequest(VisualSpec Spec, ModProject Project, Func<IReadOnlyList<string>> GameData, string Heading, string? Id, string SuggestedId,
    Func<string, string?> IdProblem, string? Current = null, Func<Task>? ChooseGameData = null);

/// <summary>What the form decided: the row's id and, unless the row is to have no HD visual yet, the plan that gives it one.</summary>
internal sealed record NewVisualResult(string Id, VisualPlan? Plan);

/// <summary>
/// The form a new missile, monster or item opens (and "Change…" on a builder's linked-files card): name the row, then pick what
/// HD draws for it, an existing visual or a new one copied from a base, or none yet. Before anything is written it lists every
/// file it will create or change; the row goes into the open table, the HD files straight into the project.
/// </summary>
internal sealed class NewVisualWindow : Window
{
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#D8BC86")), Muted = new SolidColorBrush(Color.Parse("#B9AD97")),
        Warn = new SolidColorBrush(Color.Parse("#E39A6B")), White = new SolidColorBrush(Color.Parse("#E6E6E6"));
    private readonly NewVisualRequest request;
    private readonly Func<NewVisualResult, Task> commit;
    private readonly TextBox id = new() { Width = 300 }, name = new() { Width = 300 };
    private readonly AutoCompleteBox existing = Picker(), source = Picker();
    private readonly RadioButton useExisting = new() { GroupName = "visual" }, copyNew = new() { GroupName = "visual" }, none = new() { GroupName = "visual" };
    private readonly CheckBox copyVariant = new() { IsChecked = true };
    private readonly StackPanel existingPanel = new() { Spacing = 6, Margin = new(26, 0, 0, 0) }, copyPanel = new() { Spacing = 6, Margin = new(26, 0, 0, 0) },
        summary = new() { Spacing = 5 }, gameDataPanel = new() { Spacing = 6 };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, Foreground = Warn }, choicesNote = new() { TextWrapping = TextWrapping.Wrap, Foreground = Muted, FontSize = 11 };
    private readonly Button create = new() { Classes = { "accent" } };
    private readonly DispatcherTimer planTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private bool nameEdited, updating, listAvailable;
    private int planVersion;

    internal NewVisualResult? Result { get; private set; }
    internal Task PendingChoices { get; private set; } = Task.CompletedTask;
    internal Task PendingPlan { get; private set; } = Task.CompletedTask;
    internal Button CreateButton => create;
    internal RadioButton UseExisting => useExisting;
    internal RadioButton CopyNew => copyNew;
    internal RadioButton None => none;
    internal TextBox IdBox => id;
    internal TextBox NameBox => name;
    internal AutoCompleteBox ExistingPicker => existing;
    internal AutoCompleteBox SourcePicker => source;
    internal string SummaryText => string.Join("\n", summary.Children.OfType<Control>().Select(Text));
    internal string ErrorText => error.Text ?? "";

    private static AutoCompleteBox Picker() => new() { Width = 300, FilterMode = AutoCompleteFilterMode.Contains, MinimumPrefixLength = 0, MaxDropDownHeight = 320 };
    private static string Text(Control control) => control switch
    {
        TextBlock block => block.Inlines is { Count: > 0 } inlines ? string.Concat(inlines.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text)) : block.Text ?? "",
        Panel panel => string.Join(" ", panel.Children.Select(Text)),
        _ => ""
    };

    public NewVisualWindow(NewVisualRequest request, Func<NewVisualResult, Task> commit)
    {
        this.request = request; this.commit = commit;
        var spec = request.Spec;
        bool isNew = request.Id == null;
        Title = request.Heading; Width = 760; SizeToContent = SizeToContent.Height; CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(26), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = request.Heading, FontSize = 24, Foreground = Accent });
        panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Muted, Text = Intro(spec) });

        if (isNew)
        {
            AutomationProperties.SetName(id, spec.IdColumn);
            panel.Children.Add(Row($"{spec.IdColumn} (the row's id)", id));
        }
        panel.Children.Add(new TextBlock { Text = Capital(spec.Noun).ToUpperInvariant(), Foreground = Accent, FontSize = 11, FontWeight = FontWeight.SemiBold, Margin = new(0, 6, 0, 0) });
        gameDataPanel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Warn, FontSize = 12,
            Text = $"No {spec.MapName} in the project and no game data folder chosen. Studio copies the game's list into the project before adding to it (a list holding only your entry would hide every other one in HD), so choose your extracted game data folder to pick a visual now." });
        if (request.ChooseGameData != null)
        {
            var choose = new Button { Content = "Choose game data folder…", Padding = new(10, 3), MinHeight = 0, HorizontalAlignment = HorizontalAlignment.Left };
            choose.Click += async (_, _) => { try { await request.ChooseGameData(); PendingChoices = LoadChoicesAsync(); } catch (Exception ex) { error.Text = ex.Message; } };
            gameDataPanel.Children.Add(choose);
        }
        panel.Children.Add(gameDataPanel);

        useExisting.Content = $"Use an existing {spec.Noun}";
        AutomationProperties.SetName(existing, "Existing " + spec.Noun);
        existing.PlaceholderText = spec.IsItem ? "axe/hand_axe" : spec.Family == VisualFamily.Missile ? "fireball" : "zombie1";
        existingPanel.Children.Add(existing);
        existingPanel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Muted, FontSize = 11, Text = spec.IsItem
            ? "The same picture another item uses; editing that sprite later changes both."
            : $"Rows can share one {spec.TargetNoun}: editing it later changes every row that uses it." });
        panel.Children.Add(useExisting); panel.Children.Add(existingPanel);

        copyNew.Content = $"Create a new {spec.Noun}, copied from a base";
        AutomationProperties.SetName(source, "Base " + spec.Noun);
        AutomationProperties.SetName(name, "New " + spec.Noun + " name");
        source.PlaceholderText = existing.PlaceholderText;
        name.PlaceholderText = spec.IsItem ? "folder/name" : "name";
        copyPanel.Children.Add(Row("Copy from", source, 120));
        copyPanel.Children.Add(Row(spec.IsItem ? "New sprite path" : "New name", name, 120));
        if (spec.Family == VisualFamily.Monster)
        {
            copyVariant.Content = "Also copy its colour variants, so recolouring this monster leaves the base alone";
            copyPanel.Children.Add(copyVariant);
        }
        copyPanel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Muted, FontSize = 11, Text = spec.Family switch
        {
            VisualFamily.Missile => "The copy is a unit definition of its own you can edit (particle files, offsets, scale); it starts out drawing exactly what the base draws.",
            VisualFamily.Monster => "The copy is a unit definition of its own you can edit; it keeps loading the base's models and animations.",
            _ => "The copy is a sprite of its own (with its reduced-resolution twin) for you to replace with your own picture."
        } });
        panel.Children.Add(copyNew); panel.Children.Add(copyPanel);

        none.Content = spec.Family switch
        {
            VisualFamily.Missile => "No HD effect for now (the missile is invisible in HD)",
            VisualFamily.Monster => "No HD model for now (the monster has no model in HD)",
            VisualFamily.BaseItem => "No picture for now (the item has no inventory picture in HD)",
            _ => "Use its base item's picture (no entry)"
        };
        if (isNew) panel.Children.Add(none);
        panel.Children.Add(choicesNote);

        panel.Children.Add(new Border { Padding = new(14), Background = new SolidColorBrush(Color.Parse("#292727")), CornerRadius = new(4), Child = summary });
        panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => Close();
        create.Content = isNew ? request.Heading.Replace("New ", "Create ", StringComparison.Ordinal) : $"Save {spec.Noun}";
        buttons.Children.Add(cancel); buttons.Children.Add(create); panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

        updating = true;
        id.Text = request.Id ?? request.SuggestedId;
        existing.Text = request.Current ?? "";
        if (request.Current != null) source.Text = request.Current;
        useExisting.IsChecked = true;
        updating = false;
        SuggestName();

        id.TextChanged += (_, _) => { SuggestName(); Changed(); };
        name.TextChanged += (_, _) => { if (!updating) nameEdited = true; Changed(); };
        source.TextChanged += (_, _) => { SuggestName(); Changed(); };
        existing.TextChanged += (_, _) => Changed();
        foreach (var radio in new[] { useExisting, copyNew, none }) radio.IsCheckedChanged += (_, _) => Changed();
        copyVariant.IsCheckedChanged += (_, _) => Changed();
        planTimer.Tick += (_, _) => { planTimer.Stop(); PendingPlan = RefreshPlanAsync(); };
        create.Click += async (_, _) => await CreateAsync();
        Opened += (_, _) => { if (isNew) { id.Focus(); id.SelectAll(); } else existing.Focus(); };
        Closed += (_, _) => planTimer.Stop();
        PendingChoices = LoadChoicesAsync();
    }

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Intro(VisualSpec spec) => spec.Family switch
    {
        VisualFamily.Missile => "HD mode does not use CelFile: it draws the particle effect data/hd/missiles/missiles.json names for the missile's id. A missile without an entry there is invisible in HD, so pick its effect now.",
        VisualFamily.Monster => "HD mode draws the 3D unit data/hd/character/monsters.json names for the monster's Id (monstats2 and Code only shape the legacy sprites). A monster without an entry has no HD model, so pick one now.",
        VisualFamily.BaseItem => "HD draws the inventory sprite data/hd/items/items.json names for the item's code. An item without an entry has no picture in HD, so pick one now.",
        _ => $"HD draws the inventory sprite data/hd/items/{spec.MapName} names for the item's index, and its base item's picture when it has no entry."
    };

    private static Control Row(string label, Control input, double width = 200)
    {
        var grid = new Grid { ColumnDefinitions = new($"{width},*") };
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(input, 1); input.HorizontalAlignment = HorizontalAlignment.Left; grid.Children.Add(input);
        return grid;
    }

    private string CurrentId => (request.Id ?? id.Text ?? "").Trim();

    /// <summary>Until it is typed, the new visual's name follows the row's id (in the base sprite's folder for items).</summary>
    private void SuggestName()
    {
        if (nameEdited) return;
        var key = HdVisuals.KeyFor(request.Spec, CurrentId);
        var from = (source.Text ?? "").Trim().Replace('\\', '/');
        var folder = request.Spec.IsItem && from.LastIndexOf('/') is var slash and > 0 ? from[..slash] + "/" : "";
        updating = true; name.Text = key.Length == 0 ? "" : folder + key; updating = false;
    }

    private async Task LoadChoicesAsync()
    {
        choicesNote.Text = "Reading the visuals in the project and the game data…";
        try
        {
            var gameData = request.GameData();
            var (choices, available) = await Task.Run(() => (HdVisuals.Choices(request.Project, gameData, request.Spec), HdAppearance.Find(request.Project, gameData, request.Spec.Map) != null));
            existing.ItemsSource = choices; source.ItemsSource = choices;
            listAvailable = available;
            choicesNote.Text = choices.Length == 0 ? "" : $"{choices.Length:N0} {request.Spec.Noun}{(choices.Length == 1 ? "" : "s")} to choose from (project and game data). Type to filter.";
        }
        catch (Exception ex) { choicesNote.Text = "Visuals unavailable: " + ex.Message; listAvailable = false; }
        gameDataPanel.IsVisible = !listAvailable;
        useExisting.IsEnabled = copyNew.IsEnabled = listAvailable;
        // Without the game's list a visual cannot be added safely: a new row starts with none.
        if (!listAvailable && request.Id == null) none.IsChecked = true;
        Changed();
    }

    private void Changed()
    {
        if (updating) return;
        existingPanel.IsEnabled = useExisting.IsChecked == true; copyPanel.IsEnabled = copyNew.IsChecked == true;
        planTimer.Stop(); planTimer.Start();
    }

    /// <summary>The request as the fields stand; null with the reason when it cannot be made.</summary>
    private (string Id, string? Value, string? CopyFrom, bool CopyVariant, string? Problem) Inputs()
    {
        var rowId = CurrentId;
        if (request.Id == null && request.IdProblem(rowId) is { } problem) return (rowId, null, null, false, problem);
        if (none.IsChecked == true) return (rowId, null, null, false, null);
        if (!listAvailable) return (rowId, null, null, false, $"Choose your game data folder to pick a {request.Spec.Noun}{(request.Id == null ? ", or start with none" : "")}.");
        if (copyNew.IsChecked == true)
        {
            var from = (source.Text ?? "").Trim();
            if (from.Length == 0) return (rowId, null, null, false, $"Choose the {request.Spec.Noun} to copy.");
            return (rowId, (name.Text ?? "").Trim(), from, copyVariant.IsChecked == true, null);
        }
        return (rowId, (existing.Text ?? "").Trim(), null, false, null);
    }

    private VisualPlan? Plan((string Id, string? Value, string? CopyFrom, bool CopyVariant, string? Problem) inputs) =>
        inputs.Value == null ? null : HdVisuals.Plan(request.Project, request.GameData(), request.Spec, inputs.Id, inputs.Value, inputs.CopyFrom, inputs.CopyVariant);

    private async Task RefreshPlanAsync()
    {
        int version = ++planVersion;
        var inputs = Inputs();
        if (inputs.Problem != null) { Show(null, inputs.Problem); return; }
        try
        {
            var plan = await Task.Run(() => Plan(inputs));
            if (version == planVersion) Show(plan, null);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException)
        {
            if (version == planVersion) Show(null, ex.Message);
        }
    }

    /// <summary>Lists what Create will do, file by file; a request that cannot be made shows why and cannot be created.</summary>
    private void Show(VisualPlan? plan, string? problem)
    {
        summary.Children.Clear();
        error.Text = problem ?? "";
        create.IsEnabled = problem == null && !(plan is { Changes.Length: 0 } && request.Id != null);
        if (problem != null) { summary.Children.Add(new TextBlock { Text = "Nothing will be written until this is fixed.", Foreground = Muted, FontSize = 12 }); return; }
        summary.Children.Add(new TextBlock { Text = request.Id == null ? "When you click Create:" : "When you click Save:", Foreground = White, FontSize = 12, FontWeight = FontWeight.SemiBold });
        if (request.Id == null) summary.Children.Add(Bullet($"{request.Spec.Table} (the open table)", $"add a row with {request.Spec.IdColumn} \"{CurrentId}\". Save writes it; Undo removes it."));
        foreach (var change in plan?.Changes ?? []) summary.Children.Add(Bullet(change.Relative, change.What));
        if (plan == null) summary.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Muted, Text = request.Spec.Family == VisualFamily.NamedItem
            ? "No HD file changes: the item shows its base item's picture." : $"No HD file changes. Choose its {request.Spec.Noun} later from the builder's Linked HD files card." });
        foreach (var warning in plan?.Warnings ?? []) summary.Children.Add(new TextBlock { Text = warning, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Warn });
        if (plan is { Changes.Length: > 0 })
            summary.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Muted, Margin = new(0, 4, 0, 0),
                Text = "HD files are written to the project as soon as you click" + (request.Id == null ? " Create, and stay if you undo the row." : " Save.") + " The builder's Linked HD files card then shows the row → list entry → file chain with buttons to open each." });
    }

    private static Control Bullet(string file, string what)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        text.Inlines!.Add(new Avalonia.Controls.Documents.Run("•  " + file) { Foreground = Accent, FontWeight = FontWeight.SemiBold });
        text.Inlines.Add(new Avalonia.Controls.Documents.Run("  " + what) { Foreground = White });
        return text;
    }

    private async Task CreateAsync()
    {
        create.IsEnabled = false; error.Text = "";
        try
        {
            planTimer.Stop(); planVersion++;
            var inputs = Inputs();
            Storage.Require(inputs.Problem == null, inputs.Problem ?? "");
            // The plan is made again from the fields as they are now, so what is written is what they say.
            var plan = await Task.Run(() => Plan(inputs));
            var result = new NewVisualResult(inputs.Id, plan is { Changes.Length: > 0 } ? plan : null);
            await commit(result);
            Result = result;
            Close();
        }
        catch (Exception ex) { error.Text = ex.Message; create.IsEnabled = true; }
    }
}
