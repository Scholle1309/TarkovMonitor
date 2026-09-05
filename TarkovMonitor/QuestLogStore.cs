using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace TarkovMonitor
{
    /// <summary>
    /// Keeps the quest events found in the game logs (task accepted, finished,
    /// failed) in the monitor database. The game logs are the only source that
    /// knows which quests were actually accepted in game; TarkovTracker only
    /// stores whether a quest is complete. The events are used to hide map
    /// markers of quests that are "available" but were never accepted.
    /// </summary>
    internal class QuestLogStore
    {
        private static readonly Regex LogLineHeader = new(
            @"^(?<date>\d{4}-\d{2}-\d{2}) (?<time>\d{2}:\d{2}:\d{2}\.\d{3})(?: [+-]\d{2}:\d{2})?\|[^|]*\|[^|]*\|[^|]*\|(?<message>.*)$",
            RegexOptions.Compiled);

        private readonly object gate = new();
        private readonly SqliteConnection connection;

        public event EventHandler? Changed;

        public QuestLogStore(string databasePath)
        {
            connection = new SqliteConnection($"Data Source={databasePath};");
            connection.Open();
            CreateTables();
        }

        /// <summary>Number of quest events currently stored.</summary>
        public int EventCount
        {
            get
            {
                lock (gate)
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT COUNT(*) FROM quest_log_events;";
                    return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
                }
            }
        }

        private void CreateTables()
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS quest_log_events (
                    profile_id TEXT NOT NULL,
                    session_mode TEXT NOT NULL,
                    task_id TEXT NOT NULL,
                    status INTEGER NOT NULL,
                    event_time TEXT NOT NULL,
                    PRIMARY KEY (profile_id, session_mode, task_id, status, event_time)
                );
                CREATE TABLE IF NOT EXISTS quest_log_horizon (
                    profile_id TEXT NOT NULL,
                    session_mode TEXT NOT NULL,
                    horizon TEXT NOT NULL,
                    PRIMARY KEY (profile_id, session_mode)
                );
                CREATE TABLE IF NOT EXISTS quest_overrides (
                    profile_id TEXT NOT NULL,
                    session_mode TEXT NOT NULL,
                    task_id TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    PRIMARY KEY (profile_id, session_mode, task_id)
                );";
            command.ExecuteNonQuery();
        }

        /// <summary>Manual decision for a task: accepted (treat as open) or hidden.</summary>
        public void SetOverride(string profileId, EftSessionMode sessionMode, string taskId, QuestOverride kind)
        {
            if (string.IsNullOrEmpty(profileId) || string.IsNullOrEmpty(taskId))
            {
                return;
            }
            lock (gate)
            {
                using var command = connection.CreateCommand();
                if (kind == QuestOverride.None)
                {
                    command.CommandText = "DELETE FROM quest_overrides WHERE profile_id = @profile AND session_mode = @mode AND task_id = @task;";
                }
                else
                {
                    command.CommandText = @"
                        INSERT INTO quest_overrides (profile_id, session_mode, task_id, kind) VALUES (@profile, @mode, @task, @kind)
                        ON CONFLICT(profile_id, session_mode, task_id) DO UPDATE SET kind = excluded.kind;";
                    command.Parameters.AddWithValue("@kind", kind.ToString());
                }
                command.Parameters.AddWithValue("@profile", profileId);
                command.Parameters.AddWithValue("@mode", sessionMode.ToString());
                command.Parameters.AddWithValue("@task", taskId);
                command.ExecuteNonQuery();
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public Dictionary<string, QuestOverride> GetOverrides(string profileId, EftSessionMode sessionMode)
        {
            var result = new Dictionary<string, QuestOverride>();
            if (string.IsNullOrEmpty(profileId))
            {
                return result;
            }
            lock (gate)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT task_id, kind FROM quest_overrides WHERE profile_id = @profile AND session_mode = @mode;";
                command.Parameters.AddWithValue("@profile", profileId);
                command.Parameters.AddWithValue("@mode", sessionMode.ToString());
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (Enum.TryParse<QuestOverride>(reader.GetString(1), out var kind))
                    {
                        result[reader.GetString(0)] = kind;
                    }
                }
            }
            return result;
        }

        /// <summary>Record a quest event seen live by the game watcher.</summary>
        public void AddLiveEvent(Profile profile, string taskId, TaskStatus status, DateTime eventTime)
        {
            if (string.IsNullOrEmpty(profile.Id) || string.IsNullOrEmpty(taskId) || status == TaskStatus.None)
            {
                return;
            }
            bool inserted;
            lock (gate)
            {
                inserted = Insert(profile.Id, profile.SessionMode, taskId, status, eventTime, null);
                // The game is the authority: an acceptance lifts a manual "hidden",
                // a finish or failure lifts a manual "accepted".
                var obsolete = status == TaskStatus.Started ? QuestOverride.Hidden : QuestOverride.Accepted;
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM quest_overrides WHERE profile_id = @profile AND session_mode = @mode AND task_id = @task AND kind = @kind;";
                command.Parameters.AddWithValue("@profile", profile.Id);
                command.Parameters.AddWithValue("@mode", profile.SessionMode.ToString());
                command.Parameters.AddWithValue("@task", taskId);
                command.Parameters.AddWithValue("@kind", obsolete.ToString());
                inserted |= command.ExecuteNonQuery() > 0;
            }
            if (inserted)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Read every log folder on disk and store the quest events found in
        /// the notification logs. Safe to run repeatedly; events are keyed.
        /// </summary>
        public QuestLogScanResult ScanLogs(string logsPath, GameWatcher watcher)
        {
            var result = new QuestLogScanResult();
            if (string.IsNullOrWhiteSpace(logsPath) || !Directory.Exists(logsPath))
            {
                return result;
            }

            var horizons = new Dictionary<(string, EftSessionMode), DateTime>();
            foreach (var folder in Directory.GetDirectories(logsPath))
            {
                List<LogDetails> details;
                try
                {
                    details = watcher.GetLogDetails(folder);
                }
                catch
                {
                    continue;
                }
                if (details.Count == 0)
                {
                    continue;
                }
                result.Folders++;
                foreach (var detail in details)
                {
                    var key = (detail.Profile.Id, detail.Profile.SessionMode);
                    if (!horizons.TryGetValue(key, out var horizon) || detail.Date < horizon)
                    {
                        horizons[key] = detail.Date;
                    }
                }

                var ordered = details.OrderBy(detail => detail.Date).ToList();
                foreach (var file in Directory.GetFiles(folder))
                {
                    if (!file.Contains("notifications.log") && !file.Contains("notifications_000.log"))
                    {
                        continue;
                    }
                    foreach (var questEvent in ReadQuestEvents(file))
                    {
                        // The profile that was selected when the message arrived.
                        var owner = ordered.LastOrDefault(detail => detail.Date <= questEvent.Time) ?? ordered[0];
                        lock (gate)
                        {
                            if (Insert(owner.Profile.Id, owner.Profile.SessionMode, questEvent.TaskId, questEvent.Status, questEvent.Time, null))
                            {
                                result.NewEvents++;
                            }
                        }
                        result.Events++;
                    }
                }
            }

            lock (gate)
            {
                foreach (var pair in horizons)
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = @"
                        INSERT INTO quest_log_horizon (profile_id, session_mode, horizon) VALUES (@profile, @mode, @horizon)
                        ON CONFLICT(profile_id, session_mode) DO UPDATE SET horizon = MIN(horizon, excluded.horizon);";
                    command.Parameters.AddWithValue("@profile", pair.Key.Item1);
                    command.Parameters.AddWithValue("@mode", pair.Key.Item2.ToString());
                    command.Parameters.AddWithValue("@horizon", Iso(pair.Value));
                    command.ExecuteNonQuery();
                }
                result.Horizon = horizons.Count > 0 ? horizons.Values.Min() : null;
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return result;
        }

        private bool Insert(string profileId, EftSessionMode sessionMode, string taskId, TaskStatus status, DateTime eventTime, SqliteTransaction? transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                INSERT OR IGNORE INTO quest_log_events (profile_id, session_mode, task_id, status, event_time)
                VALUES (@profile, @mode, @task, @status, @time);";
            command.Parameters.AddWithValue("@profile", profileId);
            command.Parameters.AddWithValue("@mode", sessionMode.ToString());
            command.Parameters.AddWithValue("@task", taskId);
            command.Parameters.AddWithValue("@status", (int)status);
            command.Parameters.AddWithValue("@time", Iso(eventTime));
            return command.ExecuteNonQuery() > 0;
        }

        private static string[]? TryReadLines(string logFile)
        {
            try
            {
                using var stream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                return reader.ReadToEnd().Split('\n');
            }
            catch
            {
                return null;
            }
        }

        private static IEnumerable<QuestLogEvent> ReadQuestEvents(string logFile)
        {
            var lines = TryReadLines(logFile);
            if (lines == null)
            {
                yield break;
            }

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');
                var header = LogLineHeader.Match(line);
                if (!header.Success || !header.Groups["message"].Value.Contains("Got notification | ChatMessageReceived"))
                {
                    continue;
                }
                // The JSON block follows on the next lines and ends with a line holding only "}".
                var json = new System.Text.StringBuilder();
                var j = i + 1;
                for (; j < lines.Length; j++)
                {
                    var jsonLine = lines[j].TrimEnd('\r');
                    json.Append(jsonLine).Append('\n');
                    if (jsonLine == "}")
                    {
                        break;
                    }
                }
                i = j;

                QuestLogEvent? questEvent = null;
                try
                {
                    var node = JsonNode.Parse(json.ToString());
                    var message = node?["message"];
                    var type = message?["type"]?.GetValue<int>() ?? 0;
                    if (type < (int)TaskStatus.Started || type > (int)TaskStatus.Finished)
                    {
                        continue;
                    }
                    var templateId = message?["templateId"]?.GetValue<string>() ?? "";
                    var taskId = templateId.Split(' ')[0];
                    if (string.IsNullOrEmpty(taskId))
                    {
                        continue;
                    }
                    var time = DateTime.ParseExact(
                        header.Groups["date"].Value + " " + header.Groups["time"].Value,
                        "yyyy-MM-dd HH:mm:ss.fff",
                        CultureInfo.InvariantCulture);
                    questEvent = new QuestLogEvent(taskId, (TaskStatus)type, time);
                }
                catch
                {
                    // Malformed block; skip it.
                }
                if (questEvent != null)
                {
                    yield return questEvent;
                }
            }
        }

        /// <summary>Latest event time per task and status for one profile.</summary>
        public QuestHistory GetHistory(string profileId, EftSessionMode sessionMode)
        {
            var history = new QuestHistory();
            if (string.IsNullOrEmpty(profileId))
            {
                return history;
            }
            lock (gate)
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT horizon FROM quest_log_horizon WHERE profile_id = @profile AND session_mode = @mode;";
                    command.Parameters.AddWithValue("@profile", profileId);
                    command.Parameters.AddWithValue("@mode", sessionMode.ToString());
                    var horizon = command.ExecuteScalar() as string;
                    if (horizon != null)
                    {
                        history.Horizon = ParseIso(horizon);
                    }
                }
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT task_id, status, MAX(event_time) FROM quest_log_events WHERE profile_id = @profile AND session_mode = @mode GROUP BY task_id, status;";
                    command.Parameters.AddWithValue("@profile", profileId);
                    command.Parameters.AddWithValue("@mode", sessionMode.ToString());
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        var taskId = reader.GetString(0);
                        var status = (TaskStatus)reader.GetInt32(1);
                        var time = ParseIso(reader.GetString(2));
                        if (!history.Tasks.TryGetValue(taskId, out var entry))
                        {
                            entry = new QuestHistoryEntry();
                            history.Tasks[taskId] = entry;
                        }
                        switch (status)
                        {
                            case TaskStatus.Started: entry.Started = time; break;
                            case TaskStatus.Finished: entry.Finished = time; break;
                            case TaskStatus.Failed: entry.Failed = time; break;
                        }
                    }
                }
            }
            return history;
        }

        /// <summary>
        /// Tasks that should not be shown as active on the map because the logs
        /// prove they were never accepted:
        /// the moment they became available (last prerequisite done) lies
        /// inside the logged period, and no "task started" message followed.
        /// Tasks with a logged, still open acceptance are never hidden. Tasks
        /// whose availability cannot be dated from the logs are left alone.
        /// </summary>
        public HashSet<string> ComputeHiddenTaskIds(
            string profileId,
            EftSessionMode sessionMode,
            IReadOnlyCollection<TarkovDev.Task> tasks,
            IReadOnlySet<string> completedByTracker,
            IReadOnlySet<string>? acceptedEvidence = null,
            bool strict = false)
        {
            var hidden = new HashSet<string>();
            var history = GetHistory(profileId, sessionMode);
            var overrides = GetOverrides(profileId, sessionMode);
            acceptedEvidence ??= new HashSet<string>();

            foreach (var task in tasks)
            {
                if (completedByTracker.Contains(task.id))
                {
                    continue;
                }
                if (overrides.TryGetValue(task.id, out var decision))
                {
                    if (decision == QuestOverride.Hidden)
                    {
                        hidden.Add(task.id);
                    }
                    continue;
                }
                history.Tasks.TryGetValue(task.id, out var own);
                if (own?.IsOpen == true || acceptedEvidence.Contains(task.id))
                {
                    // Accepted in game (logged, or objective progress on the tracker): keep.
                    continue;
                }
                if (strict)
                {
                    // Only proven acceptances count.
                    hidden.Add(task.id);
                    continue;
                }
                if (history.Horizon == null || history.Tasks.Count == 0)
                {
                    continue;
                }
                if (task.taskRequirements.Count == 0)
                {
                    // Available from the start of the profile; the logs cannot tell.
                    continue;
                }

                DateTime? availableAt = null;
                var datable = true;
                foreach (var requirement in task.taskRequirements)
                {
                    if (string.IsNullOrEmpty(requirement.task))
                    {
                        continue;
                    }
                    DateTime? satisfiedAt = null;
                    history.Tasks.TryGetValue(requirement.task, out var prerequisite);
                    foreach (var status in requirement.status)
                    {
                        DateTime? candidate = status switch
                        {
                            "complete" => prerequisite?.Finished,
                            "failed" => prerequisite?.Failed,
                            "active" => prerequisite?.Started,
                            _ => null,
                        };
                        if (candidate != null && (satisfiedAt == null || candidate < satisfiedAt))
                        {
                            satisfiedAt = candidate;
                        }
                    }
                    if (satisfiedAt == null)
                    {
                        // Satisfied before the logs begin (or not at all): cannot be dated.
                        datable = false;
                        break;
                    }
                    if (availableAt == null || satisfiedAt > availableAt)
                    {
                        availableAt = satisfiedAt;
                    }
                }
                if (!datable || availableAt == null || availableAt < history.Horizon)
                {
                    continue;
                }
                if (own?.Started != null && own.Started >= availableAt)
                {
                    continue;
                }
                hidden.Add(task.id);
            }
            return hidden;
        }

        private static string Iso(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);

        private static DateTime ParseIso(string value) => DateTime.ParseExact(value, "yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);

        private sealed record QuestLogEvent(string TaskId, TaskStatus Status, DateTime Time);
    }

    public enum QuestOverride
    {
        None,
        /// <summary>Treat the task as accepted and open.</summary>
        Accepted,
        /// <summary>Never show the task as active.</summary>
        Hidden,
    }

    public class QuestLogScanResult
    {
        public int Folders { get; set; }
        public int Events { get; set; }
        public int NewEvents { get; set; }
        public DateTime? Horizon { get; set; }
    }

    public class QuestHistoryEntry
    {
        public DateTime? Started { get; set; }
        public DateTime? Finished { get; set; }
        public DateTime? Failed { get; set; }

        /// <summary>Accepted and neither finished nor failed since.</summary>
        public bool IsOpen => Started != null
            && (Finished == null || Finished < Started)
            && (Failed == null || Failed < Started);
    }

    public class QuestHistory
    {
        public DateTime? Horizon { get; set; }
        public Dictionary<string, QuestHistoryEntry> Tasks { get; } = new();
    }
}
