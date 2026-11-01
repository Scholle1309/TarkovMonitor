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

        /// <summary>"Item", "Item or Other", or "Item or 4 more" for alternatives.</summary>
        public static string Label(QuestNeed need, Func<string, string> text) => need.Items.Count switch
        {
            1 => need.Items[0].name,
            2 => string.Format(text("NeedOr"), need.Items[0].name, need.Items[1].name),
            _ => string.Format(text("NeedOrMore"), need.Items[0].name, need.Items.Count - 1),
        };

        private static List<string> Single(string? id) => string.IsNullOrEmpty(id) ? new List<string>() : new List<string> { id };

        private static List<TarkovDev.Item> Resolve(IEnumerable<string> ids) =>
            ids.Select(TarkovDev.FindItem).OfType<TarkovDev.Item>().ToList();
    }
}
