using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>Which HD list draws a table's rows.</summary>
public enum VisualFamily { Missile, Monster, BaseItem, NamedItem }

/// <summary>
/// How a table's rows link to the HD files they are drawn with: <see cref="Map"/> is the list keyed by the row's
/// <see cref="IdColumn"/>, and each entry names a unit definition (missiles, monsters) or an inventory sprite asset (items).
/// Missiles: hd/missiles/missiles.json → hd/missiles/&lt;unit&gt;.json. Monsters: hd/character/monsters.json, by monstats Id →
/// hd/character/enemy/&lt;unit&gt;.json. Base items: hd/items/items.json by code → hd/global/ui/items/&lt;category&gt;/&lt;asset&gt;.sprite.
/// Uniques and set items: uniques.json / sets.json by index, falling back to the base item's picture when they have no entry.
/// </summary>
public sealed record VisualSpec(VisualFamily Family, string Table, string IdColumn, string Map, string Noun, string Category)
{
    public static VisualSpec? For(string? table) => table switch
    {
        "missiles" => new(VisualFamily.Missile, table, "Missile", "data/hd/missiles/missiles.json", "HD effect", ""),
        "monstats" => new(VisualFamily.Monster, table, "Id", "data/hd/character/monsters.json", "HD model", ""),
        "weapons" => new(VisualFamily.BaseItem, table, "code", "data/hd/items/items.json", "inventory picture", "weapon"),
        "armor" => new(VisualFamily.BaseItem, table, "code", "data/hd/items/items.json", "inventory picture", "armor"),
        "misc" => new(VisualFamily.BaseItem, table, "code", "data/hd/items/items.json", "inventory picture", "misc"),
        "uniqueitems" => new(VisualFamily.NamedItem, table, "index", "data/hd/items/uniques.json", "inventory picture", ""),
        "setitems" => new(VisualFamily.NamedItem, table, "index", "data/hd/items/sets.json", "inventory picture", ""),
        _ => null
    };
    public bool IsItem => Family is VisualFamily.BaseItem or VisualFamily.NamedItem;
    public string MapName => System.IO.Path.GetFileName(Map);
    /// <summary>What an entry names: "unit definition" or "sprite".</summary>
    public string TargetNoun => IsItem ? "sprite" : "unit definition";
    /// <summary>The id column with its article: "a Missile", "an Id", "a code", "an index".</summary>
    public string AnId => ("aeiou".Contains(char.ToLowerInvariant(IdColumn[0])) ? "an " : "a ") + IdColumn;
}

/// <summary>
/// The chain from a row to what HD draws for it: the list (<see cref="Map"/>), the entry keyed by the row's id as the list
/// spells it (<see cref="Key"/>, null when it has none), the unit or asset that entry names (<see cref="Value"/>), and that
/// file (<see cref="Target"/>). <see cref="EntryOffset"/> is where the entry's key starts in the list's text, -1 without one.
/// </summary>
public sealed record VisualLink(VisualSpec Spec, string Id, HdFile? Map, string? Key, string? Value, int EntryOffset, HdFile? Target, string[] Notes);

/// <summary>One file the new-visual form will write, as it lists them before anything is written.</summary>
public sealed record VisualChange(string Relative, string What, bool Creates);

/// <summary>
/// What giving a row an HD visual writes: the entry <see cref="Key"/> → <see cref="Value"/> in the list and, when
/// <see cref="CopyFrom"/> is set, a new unit definition or sprite named <see cref="Value"/> copied from that one first.
/// <see cref="CopyVariant"/> also copies a monster's colour variants so recolouring the copy leaves its base alone.
/// </summary>
public sealed record VisualPlan(VisualSpec Spec, string Id, string Key, string Value, string? CopyFrom, bool CopyVariant, HdFile Map, VisualChange[] Changes, string[] Warnings);

/// <summary>
/// Reads and writes the HD lists that link table rows to their HD visuals, and creates new visuals by copying a base one.
/// Files are read from the project first, then the user's extracted game data; anything written goes into the project, and a
/// list read from the game data is copied whole into the project first, as the game would otherwise lose every other entry.
/// </summary>
public static class HdVisuals
{
    private static readonly string[] ItemCategories = ["weapon", "armor", "misc"];
    private static readonly string[] MonsterFolders = ["data/hd/character/enemy", "data/hd/character/npc"];
    private static readonly ConcurrentDictionary<string, Dictionary<string, (string Key, string Value)>> maps = new(StringComparer.Ordinal);

    /// <summary>
    /// The key a new entry is written under, spelled as the base game spells its keys: missiles snake-cased with "_" before a
    /// number (bighead_1), monsters and base codes lower case, uniques and sets lower case with punctuation dropped and
    /// spaces as "_" (Fechmar's Axe → fechmars_axe). Lookups match on letters and digits, so any spelling finds the entry.
    /// </summary>
    public static string KeyFor(VisualSpec spec, string id) => spec.Family switch
    {
        VisualFamily.Missile => MissileGraphics.HdKey(id),
        VisualFamily.NamedItem => Regex.Replace(Regex.Replace(id.Trim().ToLowerInvariant(), "[^a-z0-9 _-]", ""), "[\\s_-]+", "_").Trim('_'),
        _ => id.Trim().ToLowerInvariant()
    };

    /// <summary>Whether a unit or asset name is one this list can name: a file name, or for items a path under the category folder.</summary>
    public static bool ValidName(VisualSpec spec, string name) =>
        Regex.IsMatch(name, spec.IsItem ? "^[A-Za-z0-9_-]+(/[A-Za-z0-9_-]+)*$" : "^[A-Za-z0-9_-]+$");

    /// <summary>The visuals a row can use: every unit definition (missiles, monsters) or sprite asset (items), project and game data together.</summary>
    public static string[] Choices(ModProject project, IReadOnlyList<string> gameData, VisualSpec spec)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in gameData.Prepend(project.Root))
        {
            if (spec.Family == VisualFamily.Missile)
            {
                if (HdAppearance.LocateFolder(root, "data/hd/missiles") is { } directory)
                    foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
                        if (!Path.GetFileName(file).Equals("missiles.json", StringComparison.OrdinalIgnoreCase)) names.Add(Path.GetFileNameWithoutExtension(file));
            }
            else if (spec.Family == VisualFamily.Monster)
            {
                foreach (var folder in MonsterFolders)
                    if (HdAppearance.LocateFolder(root, folder) is { } directory)
                        foreach (var file in Directory.EnumerateFiles(directory, "*.json")) names.Add(Path.GetFileNameWithoutExtension(file));
            }
            else
                foreach (var category in spec.Category.Length > 0 ? [spec.Category] : ItemCategories)
                    if (HdAppearance.LocateFolder(root, $"data/hd/global/ui/items/{category}") is { } directory)
                        foreach (var file in Directory.EnumerateFiles(directory, "*.sprite", SearchOption.AllDirectories))
                            if (!file.EndsWith(".lowend.sprite", StringComparison.OrdinalIgnoreCase))
                                names.Add(Path.GetRelativePath(directory, file)[..^".sprite".Length].Replace('\\', '/'));
        }
        return [.. names];
    }

    /// <summary>The unit or sprite a list entry's value names, project first. Null when neither has it.</summary>
    public static HdFile? Target(ModProject project, IReadOnlyList<string> gameData, VisualSpec spec, string value)
    {
        if (!ValidName(spec, value)) return null;
        switch (spec.Family)
        {
            case VisualFamily.Missile: return HdAppearance.Find(project, gameData, $"data/hd/missiles/{value}.json");
            case VisualFamily.Monster:
                foreach (var folder in MonsterFolders) if (HdAppearance.Find(project, gameData, $"{folder}/{value}.json") is { } unit) return unit;
                return null;
            default:
                foreach (var suffix in new[] { ".sprite", ".lowend.sprite" })
                    foreach (var category in ItemCategories.OrderBy(c => c == spec.Category ? 0 : 1))
                        if (HdAppearance.Find(project, gameData, $"data/hd/global/ui/items/{category}/{value}{suffix}") is { } sprite) return sprite;
                return null;
        }
    }

    /// <summary>The unit monsters.json names for a monstats Id; null when there is no list or no entry.</summary>
    public static string? MonsterUnit(ModProject project, IReadOnlyList<string> gameData, string id)
    {
        var spec = VisualSpec.For("monstats")!;
        if (HdAppearance.Find(project, gameData, spec.Map) is not { } map) return null;
        try { return Entries(spec, map).TryGetValue(ItemSprites.Key(id), out var entry) ? entry.Value : null; }
        catch (Exception e) when (e is JsonException or InvalidDataException) { return null; }
    }

    /// <summary>Every monstats Id monsters.json lists, by squashed key, with the unit it names.</summary>
    public static IReadOnlyDictionary<string, string> MonsterUnits(ModProject project, IReadOnlyList<string> gameData)
    {
        var spec = VisualSpec.For("monstats")!;
        if (HdAppearance.Find(project, gameData, spec.Map) is not { } map) return new Dictionary<string, string>();
        try { return Entries(spec, map).ToDictionary(p => p.Key, p => p.Value.Value, StringComparer.Ordinal); }
        catch (Exception e) when (e is JsonException or InvalidDataException) { return new Dictionary<string, string>(); }
    }

    /// <summary>Follows a row's id through its list to the file HD draws. Reads only; what is missing is said in the notes.</summary>
    public static VisualLink Link(ModProject project, IReadOnlyList<string> gameData, VisualSpec spec, string id)
    {
        id = id.Trim();
        var map = HdAppearance.Find(project, gameData, spec.Map);
        if (map == null)
            return new(spec, id, null, null, null, -1, null, [gameData.Count == 0
                ? $"No {spec.MapName} in the project. Choose your extracted game data folder to see what HD draws."
                : $"No {spec.Map} in the project or the game data."]);
        Dictionary<string, (string Key, string Value)> entries;
        try { entries = Entries(spec, map); }
        catch (Exception e) when (e is JsonException or InvalidDataException) { return new(spec, id, map, null, null, -1, null, [$"{map.Relative}: {e.Message}"]); }
        if (id.Length == 0 || !entries.TryGetValue(ItemSprites.Key(id), out var entry))
            return new(spec, id, map, null, null, -1, null, [spec.Family switch
            {
                VisualFamily.Missile => $"{(id.Length > 0 ? id : "This missile")} has no entry in missiles.json, so HD draws nothing for it.",
                VisualFamily.Monster => $"{(id.Length > 0 ? id : "This monster")} has no entry in monsters.json, so HD has no model for it.",
                VisualFamily.BaseItem => $"{(id.Length > 0 ? id : "This item")} has no entry in items.json, so it has no inventory picture in HD.",
                _ => $"No entry in {spec.MapName}: it uses its base item's picture."
            }]);
        var target = Target(project, gameData, spec, entry.Value);
        var notes = target != null ? Array.Empty<string>() : [ValidName(spec, entry.Value)
            ? $"{TargetPath(spec, entry.Value)} is not in the project{(gameData.Count == 0 ? "; choose your extracted game data folder to look in the base game" : " or the game data")}."
            : $"\"{entry.Value}\" is not a {spec.TargetNoun} name."];
        return new(spec, id, map, entry.Key, entry.Value, EntryOffset(File.ReadAllText(map.Path, Utf8), entry.Key), target, notes);
    }

    /// <summary>Where a value's file would be, for messages: the unit JSON, or the sprite in the table's category.</summary>
    private static string TargetPath(VisualSpec spec, string value) => spec.Family switch
    {
        VisualFamily.Missile => $"data/hd/missiles/{value}.json",
        VisualFamily.Monster => $"data/hd/character/enemy/{value}.json",
        _ => $"data/hd/global/ui/items/{(spec.Category.Length > 0 ? spec.Category : "<category>")}/{value}.sprite"
    };

    /// <summary>
    /// Checks a request and lists what it will write, without writing. <paramref name="value"/> is the unit or asset the entry
    /// will name; with <paramref name="copyFrom"/> it is a new one, copied from that. Throws when the request cannot be done.
    /// </summary>
    public static VisualPlan Plan(ModProject project, IReadOnlyList<string> gameData, VisualSpec spec, string id, string value, string? copyFrom = null, bool copyVariant = false)
    {
        id = id.Trim(); value = value.Trim().Replace('\\', '/').Trim('/'); copyFrom = copyFrom?.Trim().Replace('\\', '/').Trim('/');
        if (copyFrom?.Length == 0) copyFrom = null;
        var key = KeyFor(spec, id);
        Require(key.Length > 0, $"Give the row {spec.AnId} first: the {spec.MapName} entry is keyed by it.");
        Require(value.Length > 0, $"Choose the {spec.Noun}.");
        Require(ValidName(spec, value), spec.IsItem ? $"\"{value}\" is not a sprite name: letters, digits, _ and -, with / between folders." : $"\"{value}\" is not a {spec.TargetNoun} name: letters, digits, _ and - only.");
        var map = HdAppearance.Find(project, gameData, spec.Map);
        Require(map != null, gameData.Count == 0
            ? $"No {spec.MapName} in the project. Choose your extracted game data folder first: Studio copies the game's list whole, so the other entries keep working."
            : $"No {spec.Map} in the project or the game data.");
        var entries = Entries(spec, map!);
        var changes = new List<VisualChange>(); var warnings = new List<string>();
        var mapTarget = map!.InProject ? map.Relative : spec.Map;
        if (copyFrom != null)
        {
            Require(!copyFrom.Equals(value, StringComparison.OrdinalIgnoreCase), $"Name the new {spec.TargetNoun}: it cannot keep {copyFrom}'s name.");
            var source = Target(project, gameData, spec, copyFrom);
            Require(source != null, $"{copyFrom} is not in the project{(gameData.Count == 0 ? "; choose your extracted game data folder to copy a base-game one" : " or the game data")}.");
            foreach (var (relative, from) in CopyTargets(spec, source!, value, copyVariant, project, gameData))
            {
                Require(HdAppearance.Find(project, gameData, relative) == null, $"{relative} already exists. Pick another name, or use {value} as an existing {spec.Noun}.");
                changes.Add(new(relative, $"Create: a copy of {Path.GetFileName(from.Relative)} ({from.Origin}){Purpose(spec, relative, value, copyFrom)}", true));
            }
        }
        else if (Target(project, gameData, spec, value) == null)
            warnings.Add($"No {TargetPath(spec, value)} in the project{(gameData.Count == 0 ? "" : " or the game data")}: HD would draw nothing until it exists.");
        var entry = EntryText(spec, key, value).Replace("\n", " ");
        if (entries.TryGetValue(ItemSprites.Key(id), out var existing))
        {
            if (existing.Value.Equals(value, StringComparison.OrdinalIgnoreCase) && copyFrom == null) warnings.Add($"{spec.MapName} already points \"{existing.Key}\" at {value}: nothing to change.");
            else changes.Add(new(mapTarget, $"Change the entry \"{existing.Key}\" from {existing.Value} to {value}" + (map.InProject ? "" : " (the game's list is copied into the project first)"), false));
        }
        else changes.Add(new(mapTarget, $"Add the entry {entry}" + (map.InProject ? "" : " (the game's list is copied into the project first, so its other entries keep working)"), !map.InProject));
        return new(spec, id, key, value, copyFrom, copyVariant && spec.Family == VisualFamily.Monster, map, [.. changes], [.. warnings]);
    }

    private static string Purpose(VisualSpec spec, string relative, string value, string copyFrom) =>
        relative.EndsWith("_variant.json", StringComparison.OrdinalIgnoreCase) ? $", so recolouring {value} leaves {copyFrom} alone"
        : relative.EndsWith(".lowend.sprite", StringComparison.OrdinalIgnoreCase) ? " (the reduced-resolution picture)"
        : spec.IsItem ? ". Replace it with your own picture" : $", named {value}. It still loads {copyFrom}'s {(spec.Family == VisualFamily.Monster ? "models and animations" : "particles and textures")}";

    /// <summary>The files a copy creates, each with the file it is copied from: the unit (and a monster's variant file), or the sprite and its low-end sprite.</summary>
    private static List<(string Relative, HdFile From)> CopyTargets(VisualSpec spec, HdFile source, string value, bool copyVariant, ModProject project, IReadOnlyList<string> gameData)
    {
        var result = new List<(string, HdFile)>();
        var folder = source.Relative[..source.Relative.LastIndexOf('/')];
        if (!spec.IsItem)
        {
            result.Add(($"{folder}/{value}.json", source));
            if (copyVariant && spec.Family == VisualFamily.Monster && HdAppearance.VariantOf(File.ReadAllText(source.Path, Utf8)) is { } variantPath && HdAppearance.Find(project, gameData, variantPath) is { } variant)
                result.Add(($"{folder}/{value}/{value}_variant.json", variant));
            return result;
        }
        // The copy goes in the source's category folder (weapon, armor, misc), at the asset path chosen.
        var marker = "data/hd/global/ui/items/";
        var category = source.Relative[marker.Length..].Split('/')[0];
        foreach (var suffix in new[] { ".sprite", ".lowend.sprite" })
        {
            var stem = source.Relative.EndsWith(".lowend.sprite", StringComparison.OrdinalIgnoreCase) ? source.Relative[..^".lowend.sprite".Length] : source.Relative[..^".sprite".Length];
            if (HdAppearance.Find(project, gameData, stem + suffix) is { } from) result.Add(($"{marker}{category}/{value}{suffix}", from));
        }
        return result;
    }

    /// <summary>Writes what a plan lists: copies first, then the list entry. Fails when any file changed since it was planned. Returns the files written.</summary>
    public static string[] Apply(ModProject project, IReadOnlyList<string> gameData, VisualPlan plan)
    {
        var written = new List<string>();
        if (plan.CopyFrom != null)
        {
            var source = Target(project, gameData, plan.Spec, plan.CopyFrom);
            Require(source != null, $"{plan.CopyFrom} is no longer in the project or the game data.");
            var copies = CopyTargets(plan.Spec, source!, plan.Value, plan.CopyVariant, project, gameData);
            var variantCopy = copies.FirstOrDefault(c => c.Relative.EndsWith("_variant.json", StringComparison.OrdinalIgnoreCase)).Relative;
            foreach (var (relative, from) in copies)
            {
                var target = Inside(project.Root, relative);
                var bytes = File.ReadAllBytes(from.Path);
                Require(Hash(bytes) == from.Hash, $"{from.Relative} changed while it was being copied. Try again.");
                if (!plan.Spec.IsItem && relative.Equals(copies[0].Relative, StringComparison.Ordinal)) bytes = RenameUnit(bytes, plan.Value, variantCopy);
                AtomicWrite(target, bytes, requireAbsent: true);
                written.Add(target);
            }
        }
        written.Add(SaveEntry(project, plan.Spec, plan.Map, plan.Id, plan.Value));
        return [.. written];
    }

    /// <summary>A copied unit definition, renamed and (when its variants were copied too) pointing at its own variant file; its layout is kept.</summary>
    private static byte[] RenameUnit(byte[] bytes, string name, string? variant)
    {
        var text = Utf8.GetString(bytes);
        bool bom = text.StartsWith('﻿');
        if (JsonNode.Parse(text.TrimStart('﻿'), null, Document.SourceJsonOptions) is not JsonObject root) return bytes;
        if (root["name"] is JsonValue) root["name"] = name;
        if (variant != null)
            foreach (var component in (root["entities"] as JsonArray ?? []).OfType<JsonObject>().SelectMany(e => (e["components"] as JsonArray ?? []).OfType<JsonObject>()))
                if (component.S("type") == "VariantDefinitionComponent") component["filename"] = variant;
        return Utf8.GetBytes((bom ? "﻿" : "") + Layout(root, text));
    }

    /// <summary>
    /// Points a row's entry at a unit or asset in the project's copy of the list, or removes it when value is null. A list read
    /// from the game data is copied into the project first. An existing entry keeps its key and place; a new one is added at
    /// the end in the list's own layout, so the rest of the file is untouched. Fails when the list changed since it was read.
    /// Returns the file written.
    /// </summary>
    public static string SaveEntry(ModProject project, VisualSpec spec, HdFile map, string id, string? value)
    {
        Require(map.Relative.Equals(spec.Map, StringComparison.OrdinalIgnoreCase), $"Not the HD list for {spec.Table}: {map.Relative}");
        var squashed = ItemSprites.Key(id);
        Require(squashed.Length > 0, $"The row has no {spec.IdColumn}.");
        Require(value == null || ValidName(spec, value), $"Not a {spec.TargetNoun} name: {value}");
        var target = map.InProject ? map.Path : Inside(project.Root, spec.Map);
        var bytes = File.ReadAllBytes(map.Path);
        Require(Hash(bytes) == map.Hash, $"{map.Relative} changed since it was read. Refresh and apply the edit again.");
        var text = Utf8.GetString(bytes);
        bool bom = text.StartsWith('﻿');
        text = text.TrimStart('﻿');
        var root = JsonNode.Parse(text, null, Document.SourceJsonOptions) ?? throw new InvalidDataException($"{map.Relative} is empty.");
        var existing = FindNode(spec, root, squashed);
        if (existing == null && value == null) return target;
        if (existing != null && value != null && JsonNode.DeepEquals(existing.Node, Value(spec, value, existing.Node))) return target;
        // A multi-line list is edited in place, so the diff is the one entry; a one-line list is rewritten on its one line.
        string? output = !text.Contains('\n') || value == null ? null
            : existing == null ? Append(text, EntryText(spec, KeyFor(spec, id), value), root is JsonArray)
            : Replace(spec, text, existing.Key, value);
        if (output == null)
        {
            if (existing is { } found)
            {
                if (value == null) found.Remove();
                else found.Set(Value(spec, value, found.Node));
            }
            else if (root is JsonArray array) array.Add(new JsonObject { [KeyFor(spec, id)] = Value(spec, value!, null) });
            else ((JsonObject)root)[KeyFor(spec, id)] = Value(spec, value!, null);
            output = Layout(root, text);
        }
        // What is written must read back as the list with the entry in place.
        var check = JsonNode.Parse(output, null, Document.SourceJsonOptions) ?? throw new InvalidDataException("The list did not survive the edit.");
        Require(value == null ? FindNode(spec, check, squashed) == null : FindNode(spec, check, squashed) != null, $"Writing {spec.MapName} lost the entry; nothing was saved.");
        AtomicWrite(target, Utf8.GetBytes((bom ? "﻿" : "") + output), map.InProject ? map.Hash : null, requireAbsent: !map.InProject);
        return target;
    }

    /// <summary>A rewritten JSON document in the layout it was read in: one line stays one line, otherwise indented with the file's line endings.</summary>
    private static string Layout(JsonNode root, string original)
    {
        if (!original.Contains('\n')) return root.ToJsonString(Compact);
        var output = Json(root);
        if (original.Contains("\r\n")) output = output.Replace("\n", "\r\n");
        if (!original.EndsWith('\n')) output = output.TrimEnd('\r', '\n');
        return output;
    }

    /// <summary>Adds an entry after a multi-line list's last one, indented and ended as its first entry is.</summary>
    private static string Append(string text, string entry, bool array)
    {
        int close = text.TrimEnd().Length - 1;
        Require(close >= 0 && text[close] == (array ? ']' : '}'), "The list does not end where expected.");
        int last = close - 1;
        while (last >= 0 && char.IsWhiteSpace(text[last])) last--;
        bool empty = last < 0 || text[last] is '[' or '{';
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var indent = Regex.Match(text, "\n([ \\t]+)[\"{]") is { Success: true } m ? m.Groups[1].Value : "  ";
        return text[..(last + 1)] + (empty ? "" : ",") + newline + indent + entry.Replace("\n", newline + indent) + text[(last + 1)..];
    }

    /// <summary>Rewrites the value of an existing entry in the text (an item entry's asset or tier names); null when its text is not the expected shape.</summary>
    private static string? Replace(VisualSpec spec, string text, string key, string value)
    {
        int at = EntryOffset(text, key); if (at < 0) return null;
        int colon = text.IndexOf(':', at + JsonSerializer.Serialize(key, Compact).Length); if (colon < 0) return null;
        const string Literal = "\"(?:[^\"\\\\]|\\\\.)*\"";
        var quoted = JsonSerializer.Serialize(value, Compact);
        if (!spec.IsItem)
        {
            var literal = Regex.Match(text[(colon + 1)..], "^\\s*" + Literal);
            if (!literal.Success) return null;
            int start = colon + 1 + literal.Value.IndexOf('"');
            return text[..start] + quoted + text[(colon + 1 + literal.Length)..];
        }
        // An item entry's value is a flat object of strings, so it ends at the first closing brace.
        int end = text.IndexOf('}', colon); if (end < 0) return null;
        var fields = spec.Family == VisualFamily.BaseItem ? "asset" : "normal|uber|ultra";
        var span = text[(colon + 1)..end];
        var replaced = Regex.Replace(span, $"(\"(?:{fields})\"\\s*:\\s*){Literal}", m => m.Groups[1].Value + quoted);
        return replaced == span ? null : text[..(colon + 1)] + replaced + text[end..];
    }

    /// <summary>An entry as the base game writes it: "key": "unit" in an object list, a one-key object in an item list.</summary>
    private static string EntryText(VisualSpec spec, string key, string value)
    {
        string Quote(string s) => JsonSerializer.Serialize(s, Compact);
        return spec.Family switch
        {
            VisualFamily.BaseItem => $"{{ {Quote(key)}: {{ \"asset\": {Quote(value)} }} }}",
            VisualFamily.NamedItem => $"{{ {Quote(key)}: {{ \"normal\": {Quote(value)}, \"uber\": {Quote(value)}, \"ultra\": {Quote(value)} }} }}",
            _ => $"{Quote(key)}: {Quote(value)}"
        };
    }

    /// <summary>An entry's value: the unit name, or the item asset (every tier of a unique or set item, keeping fields it already has).</summary>
    private static JsonNode Value(VisualSpec spec, string value, JsonNode? existing)
    {
        switch (spec.Family)
        {
            case VisualFamily.BaseItem:
                var asset = existing?.DeepClone() as JsonObject ?? new JsonObject();
                asset["asset"] = value; return asset;
            case VisualFamily.NamedItem:
                var tiers = existing?.DeepClone() as JsonObject ?? new JsonObject();
                foreach (var tier in new[] { "normal", "uber", "ultra" }) tiers[tier] = value;
                return tiers;
            default: return JsonValue.Create(value)!;
        }
    }

    private sealed record NodeRef(string Key, JsonNode Node, Action Remove, Action<JsonNode> Set);

    private static NodeRef? FindNode(VisualSpec spec, JsonNode root, string squashed)
    {
        if (root is JsonObject obj)
        {
            var key = obj.Where(p => IsEntry(spec, p.Value) && ItemSprites.Key(p.Key) == squashed).Select(p => p.Key).FirstOrDefault();
            return key == null ? null : new(key, obj[key]!, () => obj.Remove(key), node => obj[key] = node);
        }
        if (root is JsonArray array)
            foreach (var item in array.OfType<JsonObject>())
            {
                var key = item.Where(p => IsEntry(spec, p.Value) && ItemSprites.Key(p.Key) == squashed).Select(p => p.Key).FirstOrDefault();
                if (key != null) return new(key, item[key]!, () => array.Remove(item), node => item[key] = node);
            }
        return null;
    }

    /// <summary>Whether a list value is an entry: a unit name for missiles and monsters (missiles.json also holds a "dependencies" object), an object for items.</summary>
    private static bool IsEntry(VisualSpec spec, JsonNode? value) => spec.IsItem ? value is JsonObject : value is JsonValue v && v.TryGetValue<string>(out _);

    /// <summary>A list's entries by squashed key, with the key as written and the unit or asset it names (an item's normal tier); cached per file content.</summary>
    private static Dictionary<string, (string Key, string Value)> Entries(VisualSpec spec, HdFile map)
    {
        var cacheKey = spec.Family + "|" + map.Hash;
        if (maps.TryGetValue(cacheKey, out var cached)) return cached;
        var root = JsonNode.Parse(File.ReadAllText(map.Path, Utf8).TrimStart('﻿'), null, Document.SourceJsonOptions);
        IEnumerable<KeyValuePair<string, JsonNode?>> pairs = root switch { JsonArray a => a.OfType<JsonObject>().SelectMany(o => o), JsonObject o => o, _ => [] };
        var result = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var (key, node) in pairs)
        {
            if (!IsEntry(spec, node)) continue;
            string? value = spec.IsItem
                ? new[] { "normal", "asset", "uber", "ultra" }.Select(k => node![k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).FirstOrDefault(s => !string.IsNullOrEmpty(s))?.Replace('\\', '/').Trim('/')
                : node!.GetValue<string>();
            if (value != null) result.TryAdd(ItemSprites.Key(key), (key, value));
        }
        if (maps.Count > 32) maps.Clear();
        return maps[cacheKey] = result;
    }

    /// <summary>Where an entry's key is written in the list's text: its quoted key followed by a colon.</summary>
    private static int EntryOffset(string text, string key)
    {
        var match = Regex.Match(text, Regex.Escape(JsonSerializer.Serialize(key, Compact)) + "\\s*:");
        return match.Success ? match.Index : -1;
    }
}
