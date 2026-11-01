namespace TarkovMonitor
{
    /// <summary>What kind of thing a task objective needs.</summary>
    public enum QuestNeedKind
    {
        /// <summary>A key or keycard to open a door or container.</summary>
        Key,
        /// <summary>An item to stash, or a marker or camera to place.</summary>
        Place,
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
        /// <summary>The symbol for a kind of need (Material icon), so needs read at a glance without item pictures.</summary>
        public static string Icon(QuestNeedKind kind) => kind switch
        {
            QuestNeedKind.Key => MudBlazor.Icons.Material.Filled.VpnKey,
            QuestNeedKind.Place => MudBlazor.Icons.Material.Filled.MoveToInbox,
            QuestNeedKind.HandOver => MudBlazor.Icons.Material.Filled.Backpack,
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
                "mark" => (QuestNeedKind.Place, Single(objective.markerItem), 1),
                "giveItem" => (QuestNeedKind.HandOver, objective.items, Math.Max(1, objective.count)),
                "buildWeapon" => (QuestNeedKind.Build, Single(objective.item), 1),
                _ => (QuestNeedKind.Key, new List<string>(), 0),
            };
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
