namespace TarkovMonitor
{
    /// <summary>What kind of thing a task objective needs.</summary>
    public enum QuestNeedKind
    {
        /// <summary>A key or keycard to open a door or container.</summary>
        Key,
        /// <summary>An item to stash at a spot.</summary>
        Place,
        /// <summary>A marker or camera to install.</summary>
        Mark,
        /// <summary>Items to hand over to the trader.</summary>
        HandOver,
        /// <summary>A weapon to build.</summary>
        Build,
    }

    /// <summary>
    /// Something an objective needs. The items are alternatives: any one of them will do
    /// (for example one of several cigarette brands, or one of two keys for the same door).
    /// </summary>
    public sealed record QuestNeed(QuestNeedKind Kind, List<TarkovDev.Item> Items, int Count, bool FoundInRaid);

    /// <summary>
    /// Reads keys and items from the Tarkov.dev task data and resolves their ids against the
    /// item list the app loads anyway, so names and icons follow the chosen language.
    /// </summary>
    public static class QuestNeeds
    {
        // Items that are installed rather than stashed, with their own symbols.
        private const string WifiCamera = "5b4391a586f7745321235ab2";
        private const string Ms2000Marker = "5991b51486f77447b112d44f";
        private const string SignalJammer = "5ac78a9b86f7741cca0bbd8d";
        private static readonly HashSet<string> InstalledItems = new() { WifiCamera, Ms2000Marker, SignalJammer };

        /// <summary>The MS2000 marker drawn as a symbol: a small device with an antenna and signal arcs, tilted to fill the square.</summary>
        public const string MarkerIcon =
            "<g transform=\"rotate(45 12 12) translate(12 12) scale(1.25) translate(-12 -11.5)\">"
            + "<path fill-rule=\"evenodd\" d=\"M9.5 9h5A1.5 1.5 0 0 1 16 10.5v9A1.5 1.5 0 0 1 14.5 21h-5A1.5 1.5 0 0 1 8 19.5v-9A1.5 1.5 0 0 1 9.5 9zM10.2 11v2.6h3.6V11z\"/>"
            + "<path d=\"M11.3 3.2h1.4V9h-1.4z\"/>"
            + "<path fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.5\" stroke-linecap=\"round\" d=\"M8.7 1.9a4.6 4.6 0 0 0 0 5.6M15.3 1.9a4.6 4.6 0 0 1 0 5.6\"/>"
            + "</g>";

        /// <summary>The signal jammer drawn as a symbol: a box with three straight antennas on top, the middle one upright, the outer two angled.</summary>
        public const string JammerIcon =
            "<path fill-rule=\"evenodd\" d=\"M7.5 12h9A1.5 1.5 0 0 1 18 13.5v7a1.5 1.5 0 0 1-1.5 1.5h-9A1.5 1.5 0 0 1 6 20.5v-7A1.5 1.5 0 0 1 7.5 12zM9 15v2.6h6V15z\"/>"
            + "<path fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" d=\"M12 12V2.5M8.6 12L4.4 4.2M15.4 12l4.2-7.8\"/>"
            + "<circle cx=\"12\" cy=\"2.3\" r=\"1.5\"/><circle cx=\"4.3\" cy=\"4\" r=\"1.5\"/><circle cx=\"19.7\" cy=\"4\" r=\"1.5\"/>";

        /// <summary>Stashing drawn as a symbol: an arrow pointing down into a box, using the whole square.</summary>
        public const string StashIcon =
            "<path d=\"M10.9 0.8h2.2v6.2h-2.2z\"/>"
            + "<path d=\"M6.4 6.2h11.2L12 12.2z\"/>"
            + "<path fill-rule=\"evenodd\" d=\"M3.2 13.2h17.6a1.2 1.2 0 0 1 1.2 1.2v7.1a1.5 1.5 0 0 1-1.5 1.5h-17A1.5 1.5 0 0 1 2 21.5v-7.1a1.2 1.2 0 0 1 1.2-1.2zM8.8 15.8v2h6.4v-2z\"/>";

        /// <summary>The symbol for a need (Material icons; stash, MS2000 and jammer drawn), so needs read at a glance without item pictures.</summary>
        public static string Icon(QuestNeed need) => need.Kind switch
        {
            QuestNeedKind.Key => MudBlazor.Icons.Material.Filled.VpnKey,
            QuestNeedKind.Place => StashIcon,
            QuestNeedKind.Mark when need.Items.Any(item => item.id == WifiCamera) => MudBlazor.Icons.Material.Filled.PhotoCamera,
            QuestNeedKind.Mark when need.Items.Any(item => item.id == Ms2000Marker) => MarkerIcon,
            QuestNeedKind.Mark when need.Items.Any(item => item.id == SignalJammer) => JammerIcon,
            QuestNeedKind.Mark => MudBlazor.Icons.Material.Filled.SettingsInputAntenna,
            QuestNeedKind.HandOver => MudBlazor.Icons.Material.Filled.PanTool,
            _ => MudBlazor.Icons.Material.Filled.Build,
        };

        /// <summary>Keys and items one objective needs, keys first.</summary>
        public static List<QuestNeed> Of(TarkovDev.TaskObjective objective)
        {
            var result = new List<QuestNeed>();
            foreach (var group in objective.requiredKeys)
            {
                var keys = Resolve(group);
                if (keys.Count > 0)
                {
                    result.Add(new QuestNeed(QuestNeedKind.Key, keys, 1, false));
                }
            }
            var (kind, ids, count) = objective.type switch
            {
                "plantItem" => (QuestNeedKind.Place, objective.items, Math.Max(1, objective.count)),
                "mark" => (QuestNeedKind.Mark, Single(objective.markerItem), 1),
                "giveItem" => (QuestNeedKind.HandOver, objective.items, Math.Max(1, objective.count)),
                "buildWeapon" => (QuestNeedKind.Build, Single(objective.item), 1),
                _ => (QuestNeedKind.Key, new List<string>(), 0),
            };
            // Cameras, markers and jammers are installed, also when the task calls it planting.
            if (kind == QuestNeedKind.Place && ids.Count > 0 && ids.All(InstalledItems.Contains))
            {
                kind = QuestNeedKind.Mark;
            }
            if (count > 0)
            {
                var items = Resolve(ids);
                if (items.Count > 0)
                {
                    result.Add(new QuestNeed(kind, items, count, objective.foundInRaid && kind == QuestNeedKind.HandOver));
                }
            }
            return result;
        }

        /// <summary>Everything a whole task needs: the objectives' needs plus task-wide keys they do not list.</summary>
        public static List<QuestNeed> OfTask(TarkovDev.Task task)
        {
            var result = task.objectives.SelectMany(Of).ToList();
            if (!ObjectivesListKeys(task))
            {
                var keys = Resolve(task.neededKeys.SelectMany(entry => entry.keys))
                    .GroupBy(item => item.id)
                    .Select(group => group.First());
                result.InsertRange(0, keys.Select(key => new QuestNeed(QuestNeedKind.Key, new List<TarkovDev.Item> { key }, 1, false)));
            }
            // A key used by several objectives is listed once.
            var seenKeys = new HashSet<string>();
            return result
                .Where(need => need.Kind != QuestNeedKind.Key || seenKeys.Add(string.Join("|", need.Items.Select(item => item.id).OrderBy(id => id))))
                .ToList();
        }

        /// <summary>True when the objectives name their keys themselves (then they are preferred over the task list).</summary>
        public static bool ObjectivesListKeys(TarkovDev.Task task) => task.objectives.Any(objective => objective.requiredKeys.Count > 0);

        /// <summary>Keys the task needs on one map, from the task-wide list.</summary>
        public static List<TarkovDev.Item> TaskKeys(TarkovDev.Task task, string mapId) =>
            Resolve(task.neededKeys.Where(entry => entry.map == mapId).SelectMany(entry => entry.keys))
                .GroupBy(item => item.id)
                .Select(group => group.First())
                .ToList();

        // Categories too broad to tell the player anything.
        private static readonly HashSet<string> GenericCategories = new()
        {
            "54009119af1c881c07000029", // Item
            "566162e44bdc2d3f298b4573", // Compound item
            "5661632d4bdc2d903d8b456b", // Stackable item
            "566168634bdc2d144c8b456c", // Searchable item
            "590c745b86f7743cc433c5f2", // Other
            "5448eb774bdc2d0a728b4567", // Barter item
        };

        /// <summary>
        /// One item by name; up to three alternatives by short name ("Wilston / Malboro / Strike");
        /// more by their shared category ("Armor · 1 of 7").
        /// </summary>
        public static string Label(QuestNeed need, Func<string, string> text)
        {
            if (need.Items.Count == 1)
            {
                return need.Items[0].name;
            }
            var shortNames = need.Items
                .Select(item => string.IsNullOrWhiteSpace(item.shortName) ? item.name : item.shortName!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (shortNames.Count <= 3)
            {
                return string.Join(" / ", shortNames);
            }
            var shared = need.Items[0].categories
                .FirstOrDefault(id => !GenericCategories.Contains(id) && need.Items.All(item => item.categories.Contains(id)));
            var category = TarkovDev.ItemCategoryName(shared);
            return category != null
                ? string.Format(text("NeedOneOfCategory"), category, need.Items.Count)
                : string.Format(text("NeedOneOf"), need.Items.Count);
        }

        private static List<string> Single(string? id) => string.IsNullOrEmpty(id) ? new List<string>() : new List<string> { id };

        private static List<TarkovDev.Item> Resolve(IEnumerable<string> ids) =>
            ids.Select(TarkovDev.FindItem).OfType<TarkovDev.Item>().ToList();
    }
}
