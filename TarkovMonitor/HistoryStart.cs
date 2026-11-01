using System.Globalization;

namespace TarkovMonitor
{
    /// <summary>How a new run of the character began.</summary>
    public enum RunBoundaryKind
    {
        /// <summary>A new character (profile reset or first creation): level, quests, hideout and stash start over.</summary>
        Reset,
        /// <summary>A prestige: level, quests and hideout start over, the character stays.</summary>
        Prestige,
    }

    /// <summary>A moment the character started over, read from the backend log or set by hand.</summary>
    public record RunBoundary(DateTime Time, RunBoundaryKind Kind, EftSessionMode Mode);

    /// <summary>
    /// A span of the character's history: from one run boundary to the next. Index 0 is
    /// the time before the first known boundary. Run counts resets, Level counts the
    /// prestiges within the run; OpenedBy tells which of the two started the period.
    /// </summary>
    public record PrestigePeriod(int Index, int Run, int Level, RunBoundaryKind? OpenedBy, DateTime? FromLocal, DateTime? ToLocal)
    {
        public DateTime? FromUtc => FromLocal?.ToUniversalTime();
        public DateTime? ToUtc => ToLocal?.ToUniversalTime();
    }

    /// <summary>
    /// The points from which game history counts. The history start is where the tool
    /// begins to look at logs at all (chosen when past logs are read, the first start of
    /// the tool, or set by hand). Run boundaries (resets and prestiges) split that history
    /// into periods: the task history counts from the latest boundary, while raids and
    /// sales stay with the character and can be viewed per period on the dashboard.
    /// </summary>
    public static class HistoryStart
    {
        private const string Format = "yyyy-MM-dd'T'HH:mm:ss";
        private static readonly TimeSpan CreationAfterPrestige = TimeSpan.FromMinutes(30);

        public static event EventHandler? Changed;

        // ---------------- history start ----------------

        /// <summary>Local time, or null when everything in the logs folder counts.</summary>
        public static DateTime? Value
        {
            get
            {
                var stored = Properties.Settings.Default.historyStart;
                return DateTime.TryParseExact(stored, Format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
                    ? DateTime.SpecifyKind(parsed, DateTimeKind.Local)
                    : null;
            }
        }

        public static DateTime? ValueUtc => Value?.ToUniversalTime();

        public static void Set(DateTime? local)
        {
            var stored = local?.ToString(Format, CultureInfo.InvariantCulture) ?? "";
            if (stored == Properties.Settings.Default.historyStart)
            {
                return;
            }
            Properties.Settings.Default.historyStart = stored;
            Properties.Settings.Default.Save();
            Changed?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>The later of a time and the start point.</summary>
        public static DateTime Clamp(DateTime local) => Value is { } start && start > local ? start : local;

        // ---------------- run boundaries ----------------

        /// <summary>Session mode the dashboard and task views work with (Unknown matches every mode).</summary>
        public static EftSessionMode CurrentMode => TaskStateService.ResolveProfile().SessionMode;

        /// <summary>Every known boundary of every mode, in time order.</summary>
        public static IReadOnlyList<RunBoundary> Boundaries
        {
            get
            {
                var list = new List<RunBoundary>();
                foreach (var part in (Properties.Settings.Default.runBoundaries ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var fields = part.Split('|');
                    if (fields.Length < 3
                        || !DateTime.TryParseExact(fields[0], Format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var time)
                        || !Enum.TryParse<RunBoundaryKind>(fields[1], out var kind)
                        || !Enum.TryParse<EftSessionMode>(fields[2], out var mode))
                    {
                        continue;
                    }
                    list.Add(new RunBoundary(DateTime.SpecifyKind(time, DateTimeKind.Local), kind, mode));
                }
                if (list.Count == 0)
                {
                    // Older versions stored prestige times only.
                    var legacyMode = CurrentMode == EftSessionMode.Unknown ? EftSessionMode.PVE : CurrentMode;
                    foreach (var part in (Properties.Settings.Default.prestigeEvents ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (DateTime.TryParseExact(part, Format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var time))
                        {
                            list.Add(new RunBoundary(DateTime.SpecifyKind(time, DateTimeKind.Local), RunBoundaryKind.Prestige, legacyMode));
                        }
                    }
                }
                list.Sort((a, b) => a.Time.CompareTo(b.Time));
                return list;
            }
        }

        /// <summary>Boundaries of the current session mode (all of them while the mode is unknown).</summary>
        public static IReadOnlyList<RunBoundary> CurrentBoundaries
        {
            get
            {
                var mode = CurrentMode;
                return Boundaries.Where(item => mode == EftSessionMode.Unknown || item.Mode == mode).ToList();
            }
        }

        /// <summary>
        /// Records a boundary; true when it was new. A profile creation within half an
        /// hour after a prestige is the prestige's own creation step, not a reset.
        /// </summary>
        public static bool AddBoundary(DateTime local, RunBoundaryKind kind, EftSessionMode mode)
        {
            var all = Boundaries.ToList();
            if (all.Any(item => item.Mode == mode && item.Kind == kind && Math.Abs((item.Time - local).TotalMinutes) < 1))
            {
                return false;
            }
            if (kind == RunBoundaryKind.Reset
                && all.Any(item => item.Mode == mode && item.Kind == RunBoundaryKind.Prestige && local >= item.Time && local - item.Time <= CreationAfterPrestige))
            {
                return false;
            }
            if (kind == RunBoundaryKind.Prestige)
            {
                all.RemoveAll(item => item.Mode == mode && item.Kind == RunBoundaryKind.Reset && item.Time >= local && item.Time - local <= CreationAfterPrestige);
            }
            all.Add(new RunBoundary(local, kind, mode));
            Store(all);
            return true;
        }

        /// <summary>Moves the latest prestige of the current mode (or adds one) to the given time.</summary>
        public static void ReplaceLatestPrestige(DateTime local)
        {
            var mode = CurrentMode == EftSessionMode.Unknown ? EftSessionMode.PVE : CurrentMode;
            var all = Boundaries.ToList();
            var latest = all.LastOrDefault(item => item.Mode == mode && item.Kind == RunBoundaryKind.Prestige);
            if (latest != null)
            {
                all.Remove(latest);
            }
            all.Add(new RunBoundary(local, RunBoundaryKind.Prestige, mode));
            Store(all);
        }

        /// <summary>Forgets every boundary of the current mode.</summary>
        public static void ClearPrestige()
        {
            var mode = CurrentMode;
            Store(Boundaries.Where(item => mode != EftSessionMode.Unknown && item.Mode != mode).ToList());
            Properties.Settings.Default.prestigeLevel = 0;
            Properties.Settings.Default.Save();
        }

        private static void Store(List<RunBoundary> all)
        {
            all.Sort((a, b) => a.Time.CompareTo(b.Time));
            var joined = string.Join(";", all.Select(item => $"{item.Time.ToString(Format, CultureInfo.InvariantCulture)}|{item.Kind}|{item.Mode}"));
            if (joined == (Properties.Settings.Default.runBoundaries ?? ""))
            {
                return;
            }
            Properties.Settings.Default.runBoundaries = joined;
            Properties.Settings.Default.prestigeEvents = "";
            Properties.Settings.Default.Save();
            Changed?.Invoke(null, EventArgs.Empty);
        }

        // ---------------- prestige view of the current mode ----------------

        /// <summary>Prestiges of the current run (since the last reset), in time order.</summary>
        public static IReadOnlyList<DateTime> PrestigeEvents
        {
            get
            {
                var boundaries = CurrentBoundaries;
                var lastReset = boundaries.LastOrDefault(item => item.Kind == RunBoundaryKind.Reset);
                return boundaries
                    .Where(item => item.Kind == RunBoundaryKind.Prestige && (lastReset == null || item.Time > lastReset.Time))
                    .Select(item => item.Time)
                    .ToList();
            }
        }

        /// <summary>When the character last prestiged in the current run, or null.</summary>
        public static DateTime? PrestigeStart => PrestigeEvents.Count > 0 ? PrestigeEvents[^1] : null;

        public static DateTime? PrestigeStartUtc => PrestigeStart?.ToUniversalTime();

        /// <summary>Prestige level of the current run: what the game reported, at least the prestiges seen.</summary>
        public static int PrestigeLevel => Math.Max(Properties.Settings.Default.prestigeLevel, PrestigeEvents.Count);

        /// <summary>Stores the prestige level the game reported (reset to 0 by a new character).</summary>
        public static void SetReportedPrestigeLevel(int level)
        {
            if (level == Properties.Settings.Default.prestigeLevel)
            {
                return;
            }
            Properties.Settings.Default.prestigeLevel = level;
            Properties.Settings.Default.Save();
            Changed?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>Latest boundary of the current mode of any kind.</summary>
        public static DateTime? LatestBoundary => CurrentBoundaries.Count > 0 ? CurrentBoundaries[^1].Time : null;

        /// <summary>Start of the task history: the later of history start and the latest boundary.</summary>
        public static DateTime? QuestStart
        {
            get
            {
                var start = Value;
                var boundary = LatestBoundary;
                if (start == null) return boundary;
                if (boundary == null) return start;
                return boundary > start ? boundary : start;
            }
        }

        public static DateTime ClampQuest(DateTime local) => QuestStart is { } start && start > local ? start : local;

        // ---------------- periods for the dashboard ----------------

        /// <summary>Periods of the current mode in order; empty while no boundary is known.</summary>
        public static IReadOnlyList<PrestigePeriod> Periods
        {
            get
            {
                var boundaries = CurrentBoundaries;
                var result = new List<PrestigePeriod>();
                if (boundaries.Count == 0)
                {
                    return result;
                }
                // Prestiges the game reports beyond the ones seen in the logs happened before the logs begin.
                var seenInRun = PrestigeEvents.Count;
                var offset = Math.Max(0, Properties.Settings.Default.prestigeLevel - seenInRun);
                var run = 1;
                var level = boundaries.Any(item => item.Kind == RunBoundaryKind.Reset) ? 0 : offset;
                result.Add(new PrestigePeriod(0, run, level, level > 0 ? RunBoundaryKind.Prestige : null, null, boundaries[0].Time));
                for (var i = 0; i < boundaries.Count; i++)
                {
                    var boundary = boundaries[i];
                    if (boundary.Kind == RunBoundaryKind.Reset)
                    {
                        run++;
                        level = 0;
                        // offset applies to the current run only
                        if (!boundaries.Skip(i + 1).Any(item => item.Kind == RunBoundaryKind.Reset))
                        {
                            level = offset;
                        }
                    }
                    else
                    {
                        level++;
                    }
                    var next = i + 1 < boundaries.Count ? boundaries[i + 1].Time : (DateTime?)null;
                    result.Add(new PrestigePeriod(i + 1, run, level, boundary.Kind, boundary.Time, next));
                }
                return result;
            }
        }

        /// <summary>Dashboard filter: -2 follows the current period, -1 is everything, otherwise a period index.</summary>
        public static int DashboardPeriod
        {
            get => Properties.Settings.Default.dashboardPeriod;
            set
            {
                if (value == Properties.Settings.Default.dashboardPeriod)
                {
                    return;
                }
                Properties.Settings.Default.dashboardPeriod = value;
                Properties.Settings.Default.Save();
            }
        }

        /// <summary>The period the dashboard shows, or null for everything.</summary>
        public static PrestigePeriod? SelectedPeriod
        {
            get
            {
                var periods = Periods;
                var selection = DashboardPeriod;
                if (selection == -1 || periods.Count == 0)
                {
                    return null;
                }
                if (selection == -2 || selection >= periods.Count)
                {
                    return periods[^1];
                }
                return periods[selection];
            }
        }
    }
}
