using System.Text.Json.Nodes;
using ModStudio.Core;
using static ModStudio.Core.Storage;

/// <summary>HD visuals for new rows: following a row to its HD list entry and file, planning a change, copying a base visual, and writing the entry in the list's own layout.</summary>
internal static class HdVisualsTests
{
    public static void Run(string root, Action<bool, string> check, Action<Action, string> throws)
    {
        var project = new ModProject(Path.Combine(root, "hd-visuals"), "test", "Test"); Directory.CreateDirectory(project.Root);
        var gameData = Path.Combine(root, "hd-visuals-game", "data");
        void Write(string relative, string text) { var file = Path.Combine(gameData, relative); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, text, Utf8); }
        string Own(string relative) => Path.Combine(project.Root, relative.Replace('/', Path.DirectorySeparatorChar));
        var missiles = VisualSpec.For("missiles")!; var monsters = VisualSpec.For("monstats")!; var weapons = VisualSpec.For("weapons")!; var uniques = VisualSpec.For("uniqueitems")!;

        check(VisualSpec.For("skills") == null && VisualSpec.For("armor") is { Family: VisualFamily.BaseItem, Category: "armor" } && VisualSpec.For("setitems")!.Map.EndsWith("sets.json"), "Missiles, monsters and items each name their HD list");
        check(HdVisuals.KeyFor(missiles, "BigHead1") == "bighead_1" && HdVisuals.KeyFor(monsters, "Zombie9") == "zombie9" && HdVisuals.KeyFor(uniques, "Fechmar's Axe") == "fechmars_axe"
            && HdVisuals.KeyFor(uniques, "Bul-Kathos' Sacred Charge") == "bul_kathos_sacred_charge" && HdVisuals.KeyFor(weapons, "HAX") == "hax", "New keys are spelled as the base game spells them");

        // Missiles: a one-line list, as the game ships it.
        Write("hd/missiles/missiles.json", "{\"dependencies\":{},\"fireball\":\"fireball\"}");
        Write("hd/missiles/fireball.json", "{\"dependencies\":{\"particles\":[]},\"type\":\"UnitDefinition\",\"name\":\"fireball\",\"entities\":[]}");
        throws(() => HdVisuals.Plan(project, [], missiles, "mybolt", "fireball"), "Without the game's list nothing is planned: a project list holding one entry would hide every other missile");
        var none = HdVisuals.Link(project, [gameData], missiles, "mybolt");
        check(none is { Key: null, Target: null, Map.InProject: false, EntryOffset: -1 } && none.Notes.Single().Contains("draws nothing"), "A missile without an entry is said to draw nothing in HD");
        var reuse = HdVisuals.Plan(project, [gameData], missiles, "mybolt", "fireball");
        check(reuse.Changes.Single() is { Relative: "data/hd/missiles/missiles.json", Creates: true } change && change.What.Contains("\"mybolt\": \"fireball\"") && change.What.Contains("copied into the project")
            && reuse.Warnings.Length == 0, "Using an existing effect plans one entry and says the game's list is copied first");
        var copy = HdVisuals.Plan(project, [gameData], missiles, "mybolt", "mybolt", "fireball");
        check(copy.Changes.Length == 2 && copy.Changes[0] is { Relative: "data/hd/missiles/mybolt.json", Creates: true } && copy.Changes[0].What.Contains("fireball.json"), "A new effect plans its copy before the entry");
        var written = HdVisuals.Apply(project, [gameData], copy);
        check(written.Length == 2 && File.Exists(Own("data/hd/missiles/mybolt.json")) && File.ReadAllText(Own("data/hd/missiles/missiles.json")) == "{\"dependencies\":{},\"fireball\":\"fireball\",\"mybolt\":\"mybolt\"}",
            "Applying copies the effect and adds the entry to the project's one-line list");
        check(JsonNode.Parse(File.ReadAllText(Own("data/hd/missiles/mybolt.json")))!["name"]!.GetValue<string>() == "mybolt" && File.ReadAllText(Own("data/hd/missiles/mybolt.json")).StartsWith("{\"dependencies\":{\"particles\":[]},\"type\""),
            "The copied unit is renamed and keeps its one-line layout");
        var link = HdVisuals.Link(project, [gameData], missiles, "MyBolt");
        check(link is { Key: "mybolt", Value: "mybolt", Target.InProject: true, Map.InProject: true } && File.ReadAllText(link.Map.Path).Substring(link.EntryOffset).StartsWith("\"mybolt\":"),
            "The link follows the row to the project's entry and file, with where the entry is written");
        throws(() => HdVisuals.Plan(project, [gameData], missiles, "other", "mybolt", "fireball"), "Copying onto a name that exists is refused");
        throws(() => HdVisuals.Plan(project, [gameData], missiles, "other", "../evil"), "Effect names cannot leave the folder");
        check(HdVisuals.Plan(project, [gameData], missiles, "other", "nothing").Warnings.Single().Contains("draw nothing"), "Naming an effect that does not exist is a warning");
        check(HdVisuals.Choices(project, [gameData], missiles).SequenceEqual(["fireball", "mybolt"]), "Effect choices list project and game units, not the list itself");

        // Monsters: an indented CRLF list, and a unit with colour variants.
        var monsterList = "{\r\n  \"zombie1\": \"zombie1\",\r\n  \"zombie2\": \"zombie1\"\r\n}\r\n";
        Write("hd/character/monsters.json", monsterList);
        Write("hd/character/enemy/zombie1.json", "{\"type\":\"UnitDefinition\",\"name\":\"zombie1\",\"entities\":[{\"components\":[{\"type\":\"VariantDefinitionComponent\",\"name\":\"component_variant\",\"filename\":\"data/hd/character/enemy/zombie1/zombie1_variant.json\"}]}]}");
        Write("hd/character/enemy/zombie1/zombie1_variant.json", "{\"entries\":[]}");
        var ghoul = HdVisuals.Plan(project, [gameData], monsters, "ghoul", "ghoul", "zombie1", copyVariant: true);
        check(ghoul.Changes.Select(c => c.Relative).SequenceEqual(["data/hd/character/enemy/ghoul.json", "data/hd/character/enemy/ghoul/ghoul_variant.json", "data/hd/character/monsters.json"])
            && ghoul.Changes[1].What.Contains("leaves zombie1 alone"), "A new model plans its unit, its own colour variants and the entry");
        HdVisuals.Apply(project, [gameData], ghoul);
        check(File.ReadAllText(Own("data/hd/character/monsters.json")) == monsterList.Replace("\"zombie1\"\r\n}", "\"zombie1\",\r\n  \"ghoul\": \"ghoul\"\r\n}"), "The entry is added in the list's indentation and line endings, the rest untouched");
        check(HdAppearance.VariantOf(File.ReadAllText(Own("data/hd/character/enemy/ghoul.json"))) == "data/hd/character/enemy/ghoul/ghoul_variant.json" && File.Exists(Own("data/hd/character/enemy/ghoul/ghoul_variant.json")),
            "The copied unit loads its own copy of the variants");
        var monstats = new[] { new JsonObject { ["Id"] = "zombie2", ["BaseId"] = "zombie1" }, new JsonObject { ["Id"] = "ghoul", ["BaseId"] = "zombie1", ["TransLvl"] = "0" } };
        var appearance = HdAppearance.Resolve(project, [gameData], monstats[1], monstats);
        check(appearance is { UnitName: "ghoul", Unit.InProject: true, Variant.InProject: true, FamilyRows: 1 }, "The HD appearance follows monsters.json before BaseId");
        HdVisuals.Apply(project, [gameData], HdVisuals.Plan(project, [gameData], monsters, "zombie2", "ghoul"));
        check(File.ReadAllText(Own("data/hd/character/monsters.json")).Contains("\r\n  \"zombie2\": \"ghoul\",\r\n") && HdAppearance.Resolve(project, [gameData], monstats[0], monstats).FamilyRows == 2,
            "Changing an entry rewrites only its value");
        check(HdVisuals.Plan(project, [gameData], monsters, "zombie2", "ghoul") is { Changes.Length: 0 } same && same.Warnings.Single().Contains("nothing to change"), "Choosing the visual a row already has changes nothing");

        // Base items: an array of one-key objects; the sprite and its low-end copy.
        Write("hd/items/items.json", "[\r\n  { \"hax\": { \"asset\": \"axe/hand_axe\" } }\r\n]\r\n");
        Write("hd/global/ui/items/weapon/axe/hand_axe.sprite", "SpA1");
        Write("hd/global/ui/items/weapon/axe/hand_axe.lowend.sprite", "SpA1low");
        check(HdVisuals.Choices(project, [gameData], weapons).SequenceEqual(["axe/hand_axe"]), "Picture choices are sprite paths under the category, without low-end copies");
        var axe = HdVisuals.Plan(project, [gameData], weapons, "zz1", "axe/my_axe", "axe/hand_axe");
        check(axe.Changes.Select(c => c.Relative).SequenceEqual(["data/hd/global/ui/items/weapon/axe/my_axe.sprite", "data/hd/global/ui/items/weapon/axe/my_axe.lowend.sprite", "data/hd/items/items.json"]),
            "A new picture copies the sprite and its low-end sprite");
        HdVisuals.Apply(project, [gameData], axe);
        check(File.ReadAllText(Own("data/hd/items/items.json")) == "[\r\n  { \"hax\": { \"asset\": \"axe/hand_axe\" } },\r\n  { \"zz1\": { \"asset\": \"axe/my_axe\" } }\r\n]\r\n"
            && File.ReadAllText(Own("data/hd/global/ui/items/weapon/axe/my_axe.lowend.sprite")) == "SpA1low", "The item entry is added in the list's own style");
        check(HdVisuals.Link(project, [gameData], weapons, "zz1") is { Value: "axe/my_axe", Target.InProject: true } && ItemSprites.Resolve(project, [gameData], "weapons", "", "zz1", "weapons").File?.InProject == true,
            "The item's picture now resolves to the project's copy");

        // Uniques: tiers, and changing an existing entry in place.
        Write("hd/items/uniques.json", "[\r\n  { \"the_gnasher\": {\r\n  \"normal\": \"axe/hand_axe\",\r\n  \"uber\": \"axe/hand_axe\",\r\n  \"ultra\": \"axe/hand_axe\"\r\n} }\r\n]\r\n");
        check(HdVisuals.Link(project, [gameData], uniques, "No Entry").Notes.Single().Contains("base item"), "A unique without an entry uses its base item's picture");
        HdVisuals.Apply(project, [gameData], HdVisuals.Plan(project, [gameData], uniques, "The Gnasher", "axe/my_axe"));
        var uniqueText = File.ReadAllText(Own("data/hd/items/uniques.json"));
        check(uniqueText == "[\r\n  { \"the_gnasher\": {\r\n  \"normal\": \"axe/my_axe\",\r\n  \"uber\": \"axe/my_axe\",\r\n  \"ultra\": \"axe/my_axe\"\r\n} }\r\n]\r\n", "Every tier of an existing unique entry is rewritten in place");
        HdVisuals.Apply(project, [gameData], HdVisuals.Plan(project, [gameData], uniques, "Fechmar's Axe", "axe/hand_axe"));
        check(HdVisuals.Link(project, [gameData], uniques, "Fechmar's Axe") is { Key: "fechmars_axe", Value: "axe/hand_axe" } && JsonNode.Parse(File.ReadAllText(Own("data/hd/items/uniques.json"))) is JsonArray { Count: 2 },
            "A new unique entry names its picture for every tier");
        var stale = HdVisuals.Link(project, [gameData], uniques, "x").Map!;
        File.AppendAllText(stale.Path, " ");
        throws(() => HdVisuals.SaveEntry(project, uniques, stale, "x", "axe/hand_axe"), "A list that changed since it was read is not overwritten");

        // Every vanilla row follows its link: almost all reach a file, and nothing throws.
        if (Environment.GetEnvironmentVariable("MODSTUDIO_BASE_EXCEL") is { Length: > 0 } excel && Path.GetFullPath(Path.Combine(excel, "..", "..")) is var vanilla && Directory.Exists(Path.Combine(vanilla, "hd")))
        {
            var sweep = new ModProject(Path.Combine(root, "hd-visuals-sweep"), "sweep", "Sweep"); Directory.CreateDirectory(sweep.Root);
            var report = new List<string>();
            foreach (var (table, file) in new[] { ("missiles", "missiles"), ("monstats", "monstats"), ("weapons", "weapons"), ("armor", "armor"), ("misc", "misc"), ("uniqueitems", "uniqueitems") })
            {
                var spec = VisualSpec.For(table)!;
                var rows = TableData.FromTsv(File.ReadAllBytes(Path.Combine(excel, file + ".txt")), table, $"global/excel/{file}.txt");
                var ids = Enumerable.Range(0, rows.Records.Count).Select(i => rows.Cell(i, spec.IdColumn)).Where(id => id.Length > 0 && id != "Expansion").ToArray();
                var links = ids.Select(id => HdVisuals.Link(sweep, [vanilla], spec, id)).ToArray();
                report.Add($"{table}: {links.Count(l => l.Target != null)}/{ids.Length} reach a file, {links.Count(l => l.Key != null && l.EntryOffset < 0)} entries without an offset");
                check(links.All(l => l.Key == null || l.EntryOffset >= 0), $"Every vanilla {table} entry is found in its list's text");
                check(spec.Family == VisualFamily.NamedItem || links.Count(l => l.Target != null) > ids.Length * 0.9, $"Most vanilla {table} rows reach their HD file: {report[^1]}");
            }
            Console.WriteLine(string.Join("\n", report));
        }
    }
}
