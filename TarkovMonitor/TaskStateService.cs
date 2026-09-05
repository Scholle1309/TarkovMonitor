namespace TarkovMonitor
{
    public enum TaskState
    {
        /// <summary>Accepted in game (per the logs) and still open.</summary>
        Accepted,
        /// <summary>Prerequisites done per the tracker, acceptance cannot be dated from the logs.</summary>
        Available,
        /// <summary>Prerequisites done, but the logs show it was never accepted.</summary>
        NeverAccepted,
        /// <summary>Prerequisites or level not met.</summary>
        Locked,
        Completed,
        Failed,
    }

    public class TaskView
    {
        public TarkovDev.Task Task { get; init; } = default!;
        public TaskState State { get; init; }
        public string TraderName { get; init; } = "";
        /// <summary>Maps the task or one of its objectives takes place on (empty = anywhere).</summary>
        public List<TarkovDev.Map> Maps { get; init; } = new();
        /// <summary>True when the task can be worked on the map currently loaded.</summary>
        public bool OnCurrentMap { get; init; }
        public DateTime? AcceptedAt { get; init; }
    }

    /// <summary>
    /// Combines the Tarkov.dev task catalogue, the TarkovTracker progress and
    /// the quest events from the game logs into one state per task. Used by the
    /// Tasks tab; the same rules decide which map markers are hidden.
    /// </summary>
    internal class TaskStateService
    {
        private readonly QuestLogStore questLogStore;
        private readonly MapsService mapsService;

        public TaskStateService(QuestLogStore questLogStore, MapsService mapsService)
        {
            this.questLogStore = questLogStore;
            this.mapsService = mapsService;
        }

        /// <summary>Profile whose history applies (tracker session first, then the logs).</summary>
        public static (string ProfileId, EftSessionMode SessionMode) ResolveProfile()
        {
            var profileId = TarkovTracker.CurrentProfileId;
            if (!string.IsNullOrEmpty(profileId) && TarkovTracker.CurrentSessionMode != EftSessionMode.Unknown)
            {
                return (profileId, TarkovTracker.CurrentSessionMode);
            }
            var logProfile = GameWatcher.CurrentProfile.Snapshot();
            return (logProfile.Id ?? "", logProfile.SessionMode);
        }

        public bool HasTrackerProgress => TarkovTracker.Progress?.data?.tasksProgress?.Count > 0;

        public List<TaskView> GetTasks()
        {
            var tasks = TarkovDev.Tasks;
            var maps = TarkovDev.Maps;
            var traders = TarkovDev.Traders;
            var progress = TarkovTracker.Progress?.data;
            var (profileId, sessionMode) = ResolveProfile();
            var history = questLogStore.GetHistory(profileId, sessionMode);

            var completed = new HashSet<string>();
            var failed = new HashSet<string>();
            foreach (var entry in progress?.tasksProgress ?? new())
            {
                if (entry.complete)
                {
                    completed.Add(entry.id);
                }
                else if (entry.failed || entry.invalid)
                {
                    failed.Add(entry.id);
                }
            }
            var hidden = questLogStore.ComputeHiddenTaskIds(profileId, sessionMode, tasks, completed);
            var playerLevel = progress?.playerLevel ?? 0;
            var currentMapId = mapsService.CurrentMap?.id;

            var result = new List<TaskView>(tasks.Count);
            foreach (var task in tasks)
            {
                history.Tasks.TryGetValue(task.id, out var own);
                TaskState state;
                if (completed.Contains(task.id))
                {
                    state = TaskState.Completed;
                }
                else if (failed.Contains(task.id) && own?.IsOpen != true)
                {
                    state = TaskState.Failed;
                }
                else if (own?.IsOpen == true)
                {
                    state = TaskState.Accepted;
                }
                else if (hidden.Contains(task.id))
                {
                    state = TaskState.NeverAccepted;
                }
                else if (PrerequisitesMet(task, completed, failed, history, playerLevel))
                {
                    state = TaskState.Available;
                }
                else
                {
                    state = TaskState.Locked;
                }

                var mapIds = new List<string>();
                if (!string.IsNullOrEmpty(task.map))
                {
                    mapIds.Add(task.map);
                }
                foreach (var objective in task.objectives)
                {
                    foreach (var mapId in objective.maps)
                    {
                        if (!mapIds.Contains(mapId))
                        {
                            mapIds.Add(mapId);
                        }
                    }
                }
                var taskMaps = mapIds
                    .Select(mapId => maps.Find(map => map.id == mapId))
                    .Where(map => map != null)
                    .Select(map => map!)
                    .ToList();

                result.Add(new TaskView
                {
                    Task = task,
                    State = state,
                    TraderName = traders.Find(trader => trader.id == task.trader)?.name ?? "",
                    Maps = taskMaps,
                    OnCurrentMap = currentMapId != null && (mapIds.Count == 0 || mapIds.Contains(currentMapId)),
                    AcceptedAt = own?.Started,
                });
            }
            return result;
        }

        private static bool PrerequisitesMet(TarkovDev.Task task, HashSet<string> completed, HashSet<string> failed, QuestHistory history, int playerLevel)
        {
            if (playerLevel > 0 && task.minPlayerLevel > playerLevel)
            {
                return false;
            }
            foreach (var requirement in task.taskRequirements)
            {
                if (string.IsNullOrEmpty(requirement.task))
                {
                    continue;
                }
                var satisfied = false;
                foreach (var status in requirement.status)
                {
                    satisfied = status switch
                    {
                        "complete" => completed.Contains(requirement.task),
                        "failed" => failed.Contains(requirement.task),
                        "active" => history.Tasks.TryGetValue(requirement.task, out var entry) && entry.IsOpen,
                        _ => false,
                    };
                    if (satisfied)
                    {
                        break;
                    }
                }
                if (!satisfied)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
