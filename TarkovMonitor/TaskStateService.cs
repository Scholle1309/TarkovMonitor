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
        /// <summary>Hidden by the user.</summary>
        Hidden,
    }

    public class TaskView
    {
        public TarkovDev.Task Task { get; init; } = default!;
        public TaskState State { get; init; }
        public QuestOverride Override { get; init; }
        /// <summary>Why the task counts as accepted: log, tracker objective progress or the user.</summary>
        public string AcceptedReason { get; init; } = "";
        public string TraderName { get; init; } = "";
        /// <summary>Maps the task or one of its objectives takes place on (empty = anywhere).</summary>
        public List<TarkovDev.Map> Maps { get; init; } = new();
        /// <summary>True when the task can be worked on the map currently loaded.</summary>
        public bool OnCurrentMap { get; init; }
        /// <summary>Ids of the maps on which Tarkov.dev draws at least one marker for this task.</summary>
        public HashSet<string> MarkerMapIds { get; init; } = new();
        public bool HasMarkerOnCurrentMap { get; init; }
        public DateTime? AcceptedAt { get; init; }
    }

    public record TaskRecommendation(TaskView View, List<TarkovDev.Task> DirectUnlocks, int TotalUnlocks)
    {
        public int Experience => View.Task.experience;
        public double MixScore { get; set; }
    }

    public enum RecommendationMode
    {
        /// <summary>Completion unlocks the most other tasks.</summary>
        Unlocks,
        /// <summary>Highest experience reward.</summary>
        Experience,
        /// <summary>Kappa-relevant tasks, ordered by unlocks.</summary>
        Kappa,
        /// <summary>Only tasks doable on the current map.</summary>
        CurrentMap,
        /// <summary>Balanced score of unlocks and experience.</summary>
        Mix,
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
            var evidence = GetAcceptedEvidence(tasks);
            var overrides = questLogStore.GetOverrides(profileId, sessionMode);
            var strict = Properties.Settings.Default.mapStrictAcceptedTasks;
            var hidden = questLogStore.ComputeHiddenTaskIds(profileId, sessionMode, tasks, completed, evidence, strict);
            var playerLevel = progress?.playerLevel ?? 0;
            var currentMapId = mapsService.ShownMap?.id;

            var result = new List<TaskView>(tasks.Count);
            foreach (var task in tasks)
            {
                history.Tasks.TryGetValue(task.id, out var own);
                overrides.TryGetValue(task.id, out var decision);
                TaskState state;
                var reason = "";
                if (completed.Contains(task.id))
                {
                    state = TaskState.Completed;
                }
                else if (decision == QuestOverride.Hidden)
                {
                    state = TaskState.Hidden;
                }
                else if (failed.Contains(task.id) && own?.IsOpen != true && decision != QuestOverride.Accepted)
                {
                    state = TaskState.Failed;
                }
                else if (decision == QuestOverride.Accepted)
                {
                    state = TaskState.Accepted;
                    reason = "user";
                }
                else if (own?.IsOpen == true)
                {
                    state = TaskState.Accepted;
                    reason = "log";
                }
                else if (evidence.Contains(task.id))
                {
                    state = TaskState.Accepted;
                    reason = "progress";
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
                var markerMapIds = new HashSet<string>(task.objectives.SelectMany(objective => objective.MarkerMapIds));

                result.Add(new TaskView
                {
                    Task = task,
                    State = state,
                    Override = decision,
                    AcceptedReason = reason,
                    TraderName = traders.Find(trader => trader.id == task.trader)?.name ?? "",
                    Maps = taskMaps,
                    OnCurrentMap = currentMapId != null && (mapIds.Count == 0 || mapIds.Contains(currentMapId)),
                    MarkerMapIds = markerMapIds,
                    HasMarkerOnCurrentMap = currentMapId != null && markerMapIds.Contains(currentMapId),
                    AcceptedAt = own?.Started,
                });
            }
            return result;
        }

        /// <summary>
        /// Tasks with objective progress on the tracker. Objectives only advance
        /// while a task is active, so progress proves the task was accepted even
        /// when the acceptance predates the game logs.
        /// </summary>
        public static HashSet<string> GetAcceptedEvidence(IReadOnlyCollection<TarkovDev.Task> tasks)
        {
            var result = new HashSet<string>();
            var objectives = TarkovTracker.Progress?.data?.taskObjectivesProgress;
            if (objectives == null || objectives.Count == 0)
            {
                return result;
            }
            var objectiveToTask = new Dictionary<string, string>();
            foreach (var task in tasks)
            {
                foreach (var objective in task.objectives)
                {
                    if (!string.IsNullOrEmpty(objective.id))
                    {
                        objectiveToTask[objective.id] = task.id;
                    }
                }
            }
            foreach (var objective in objectives)
            {
                if ((objective.complete || objective.count > 0)
                    && objective.id != null
                    && objectiveToTask.TryGetValue(objective.id, out var taskId))
                {
                    result.Add(taskId);
                }
            }
            return result;
        }

        /// <summary>Tracker progress of one objective: completed flag and counted amount.</summary>
        public static (bool Complete, int Count) GetObjectiveProgress(string objectiveId)
        {
            var entry = TarkovTracker.Progress?.data?.taskObjectivesProgress?.Find(item => item.id == objectiveId);
            return entry == null ? (false, 0) : (entry.complete, entry.count);
        }

        /// <summary>Completed objectives out of all objectives of a task.</summary>
        public static (int Done, int Total) ObjectiveSummary(TarkovDev.Task task)
        {
            var done = task.objectives.Count(objective => GetObjectiveProgress(objective.id).Complete);
            return (done, task.objectives.Count);
        }

        /// <summary>True when objectives can be written to the tracker right now.</summary>
        public static bool CanWriteObjectives => TarkovTracker.ValidToken && !string.IsNullOrEmpty(TarkovTracker.CurrentProfileId);

        /// <summary>Set an objective on the tracker; the cached progress follows.</summary>
        public Task SetObjectiveAsync(string objectiveId, bool? complete, int? count)
        {
            return TarkovTracker.SetObjectiveProgress(objectiveId, complete, count);
        }

        /// <summary>Manual decision by the user for the active profile.</summary>
        public void SetOverride(string taskId, QuestOverride kind)
        {
            var (profileId, sessionMode) = ResolveProfile();
            questLogStore.SetOverride(profileId, sessionMode, taskId, kind);
        }

        /// <summary>
        /// Accepted tasks worth doing next: the ones whose completion unlocks the
        /// most other tasks. Direct unlocks are tasks that become available right
        /// away; the total counts everything further down the chain. While a raid
        /// is running, tasks on the current map are preferred.
        /// </summary>
        public List<TaskRecommendation> GetRecommendations(List<TaskView> views, int count, RecommendationMode mode = RecommendationMode.Unlocks)
        {
            var progress = TarkovTracker.Progress?.data;
            var playerLevel = progress?.playerLevel ?? 0;
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
            var (profileId, sessionMode) = ResolveProfile();
            var history = questLogStore.GetHistory(profileId, sessionMode);

            // prerequisite id -> tasks that need it completed
            var dependents = new Dictionary<string, List<TarkovDev.Task>>();
            foreach (var task in TarkovDev.Tasks)
            {
                foreach (var requirement in task.taskRequirements)
                {
                    if (string.IsNullOrEmpty(requirement.task) || !requirement.status.Contains("complete"))
                    {
                        continue;
                    }
                    if (!dependents.TryGetValue(requirement.task, out var list))
                    {
                        list = new List<TarkovDev.Task>();
                        dependents[requirement.task] = list;
                    }
                    list.Add(task);
                }
            }

            var stateById = views.ToDictionary(view => view.Task.id, view => view.State);
            var preferCurrentMap = mapsService.RaidActive;
            var result = new List<TaskRecommendation>();
            foreach (var view in views.Where(view => view.State == TaskState.Accepted))
            {
                var direct = new List<TarkovDev.Task>();
                if (dependents.TryGetValue(view.Task.id, out var next))
                {
                    foreach (var candidate in next)
                    {
                        if (stateById.TryGetValue(candidate.id, out var state)
                            && state is TaskState.Completed or TaskState.Failed or TaskState.Accepted)
                        {
                            continue;
                        }
                        if (PrerequisitesMet(candidate, completed, failed, history, playerLevel, assumeComplete: view.Task.id))
                        {
                            direct.Add(candidate);
                        }
                    }
                }
                var total = CountDownstream(view.Task.id, dependents, completed);
                result.Add(new TaskRecommendation(view, direct, total));
            }

            // Balanced score: unlocks and experience each normalised to 0..1.
            var maxUnlocks = result.Count == 0 ? 1.0 : Math.Max(1.0, result.Max(item => item.DirectUnlocks.Count + item.TotalUnlocks * 0.5));
            var maxExperience = result.Count == 0 ? 1.0 : Math.Max(1.0, result.Max(item => (double)item.Experience));
            foreach (var item in result)
            {
                item.MixScore = (item.DirectUnlocks.Count + item.TotalUnlocks * 0.5) / maxUnlocks + item.Experience / maxExperience;
            }

            IOrderedEnumerable<TaskRecommendation> ordered = mode switch
            {
                RecommendationMode.Experience => result
                    .Where(item => item.Experience > 0)
                    .OrderByDescending(item => item.Experience)
                    .ThenByDescending(item => item.DirectUnlocks.Count),
                RecommendationMode.Kappa => result
                    .Where(item => item.View.Task.kappaRequired)
                    .OrderByDescending(item => item.DirectUnlocks.Count)
                    .ThenByDescending(item => item.TotalUnlocks)
                    .ThenByDescending(item => item.Experience),
                RecommendationMode.CurrentMap => result
                    .Where(item => item.View.OnCurrentMap)
                    .OrderByDescending(item => item.View.Maps.Count > 0)
                    .ThenByDescending(item => item.DirectUnlocks.Count)
                    .ThenByDescending(item => item.TotalUnlocks)
                    .ThenByDescending(item => item.Experience),
                RecommendationMode.Mix => result
                    .OrderByDescending(item => preferCurrentMap && item.View.OnCurrentMap)
                    .ThenByDescending(item => item.MixScore),
                _ => result
                    .Where(item => item.DirectUnlocks.Count > 0 || item.TotalUnlocks > 0)
                    .OrderByDescending(item => preferCurrentMap && item.View.OnCurrentMap)
                    .ThenByDescending(item => item.DirectUnlocks.Count)
                    .ThenByDescending(item => item.TotalUnlocks)
                    .ThenByDescending(item => item.Experience),
            };
            return ordered.ThenBy(item => item.View.Task.name).Take(count).ToList();
        }

        private static int CountDownstream(string taskId, Dictionary<string, List<TarkovDev.Task>> dependents, HashSet<string> completed)
        {
            var seen = new HashSet<string>();
            var queue = new Queue<string>();
            queue.Enqueue(taskId);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!dependents.TryGetValue(current, out var next))
                {
                    continue;
                }
                foreach (var task in next)
                {
                    if (completed.Contains(task.id) || !seen.Add(task.id))
                    {
                        continue;
                    }
                    queue.Enqueue(task.id);
                }
            }
            return seen.Count;
        }

        private static bool PrerequisitesMet(TarkovDev.Task task, HashSet<string> completed, HashSet<string> failed, QuestHistory history, int playerLevel, string? assumeComplete = null)
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
                        "complete" => completed.Contains(requirement.task) || requirement.task == assumeComplete,
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
