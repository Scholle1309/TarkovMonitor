using System.Globalization;

namespace TarkovMonitor
{
    /// <summary>
    /// The point from which game history counts: set when past logs are read
    /// from a chosen breakpoint, on the first start of the tool, or by hand in
    /// the settings. Quest history, raids and sales before it are ignored.
    /// </summary>
    public static class HistoryStart
    {
        private const string Format = "yyyy-MM-dd'T'HH:mm:ss";

        public static event EventHandler? Changed;

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

        /// <summary>
        /// When the profile last prestiged (local time). Quests, hideout and level start
        /// over at that moment while raids and sales stay with the character; the task
        /// history therefore counts from the later of this and the history start.
        /// </summary>
        public static DateTime? PrestigeStart
        {
            get
            {
                var stored = Properties.Settings.Default.prestigeStart;
                return DateTime.TryParseExact(stored, Format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
                    ? DateTime.SpecifyKind(parsed, DateTimeKind.Local)
                    : null;
            }
        }

        public static DateTime? PrestigeStartUtc => PrestigeStart?.ToUniversalTime();

        /// <summary>Every prestige of the profile in order (local time), from the logs or set by hand.</summary>
        public static IReadOnlyList<DateTime> PrestigeEvents
        {
            get
            {
                var list = new List<DateTime>();
                foreach (var part in (Properties.Settings.Default.prestigeEvents ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (DateTime.TryParseExact(part, Format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
                    {
                        list.Add(DateTime.SpecifyKind(parsed, DateTimeKind.Local));
                    }
                }
                var latest = PrestigeStart;
                if (latest != null && !list.Any(item => Math.Abs((item - latest.Value).TotalMinutes) < 1))
                {
                    list.Add(latest.Value);
                }
                list.Sort();
                return list;
            }
        }

        /// <summary>Adds a prestige moment (ignored when one within a minute is known); true when added.</summary>
        public static bool AddPrestige(DateTime local, int? level = null)
        {
            var events = PrestigeEvents.ToList();
            var added = false;
            if (!events.Any(item => Math.Abs((item - local).TotalMinutes) < 1))
            {
                events.Add(local);
                events.Sort();
                added = true;
            }
            var newLevel = Math.Max(level ?? 0, Math.Max(events.Count, PrestigeLevel));
            StorePrestige(events, newLevel);
            return added;
        }

        /// <summary>Replaces the latest prestige moment (or adds the first one).</summary>
        public static void ReplaceLatestPrestige(DateTime local)
        {
            var events = PrestigeEvents.ToList();
            if (events.Count > 0)
            {
                events[events.Count - 1] = local;
            }
            else
            {
                events.Add(local);
            }
            events.Sort();
            StorePrestige(events, Math.Max(events.Count, Math.Max(1, PrestigeLevel)));
        }

        public static void ClearPrestige() => StorePrestige(new List<DateTime>(), 0);

        private static void StorePrestige(List<DateTime> events, int level)
        {
            var joined = string.Join(";", events.Select(item => item.ToString(Format, CultureInfo.InvariantCulture)));
            var latest = events.Count > 0 ? events[^1].ToString(Format, CultureInfo.InvariantCulture) : "";
            var changed = joined != (Properties.Settings.Default.prestigeEvents ?? "")
                || latest != Properties.Settings.Default.prestigeStart
                || level != Properties.Settings.Default.prestigeLevel;
            if (!changed)
            {
                return;
            }
            Properties.Settings.Default.prestigeEvents = joined;
            Properties.Settings.Default.prestigeStart = latest;
            Properties.Settings.Default.prestigeLevel = level;
            Properties.Settings.Default.Save();
            Changed?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>
        /// A span of the character's history: before the first prestige, or from one
        /// prestige to the next. The label level accounts for prestiges that happened
        /// before the logs begin (level reported by the game, events known from the logs).
        /// </summary>
        public record PrestigePeriod(int Index, int Level, DateTime? FromLocal, DateTime? ToLocal)
        {
            public DateTime? FromUtc => FromLocal?.ToUniversalTime();
            public DateTime? ToUtc => ToLocal?.ToUniversalTime();
        }

        /// <summary>Periods in order: index 0 is before the first known prestige, then one per prestige.</summary>
        public static IReadOnlyList<PrestigePeriod> Periods
        {
            get
            {
                var events = PrestigeEvents;
                var result = new List<PrestigePeriod>();
                if (events.Count == 0)
                {
                    return result;
                }
                var firstLevel = Math.Max(1, PrestigeLevel - events.Count + 1);
                result.Add(new PrestigePeriod(0, firstLevel - 1, null, events[0]));
                for (var i = 0; i < events.Count; i++)
                {
                    result.Add(new PrestigePeriod(i + 1, firstLevel + i, events[i], i + 1 < events.Count ? events[i + 1] : null));
                }
                return result;
            }
        }

        /// <summary>Dashboard filter: -2 follows the current prestige, -1 is everything, otherwise a period index.</summary>
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

        /// <summary>Prestige level of the profile as read from the game logs (0 = none).</summary>
        public static int PrestigeLevel => Properties.Settings.Default.prestigeLevel;

        /// <summary>Records a prestige; true when it was newer than the stored one.</summary>
        public static bool SetPrestige(DateTime? local, int? level = null)
        {
            var stored = local?.ToString(Format, CultureInfo.InvariantCulture) ?? "";
            var changed = stored != Properties.Settings.Default.prestigeStart;
            if (level.HasValue && level.Value != Properties.Settings.Default.prestigeLevel)
            {
                Properties.Settings.Default.prestigeLevel = level.Value;
                changed = true;
            }
            if (!changed)
            {
                return false;
            }
            Properties.Settings.Default.prestigeStart = stored;
            Properties.Settings.Default.Save();
            Changed?.Invoke(null, EventArgs.Empty);
            return true;
        }

        /// <summary>Start of the task history: the later of history start and prestige.</summary>
        public static DateTime? QuestStart
        {
            get
            {
                var start = Value;
                var prestige = PrestigeStart;
                if (start == null) return prestige;
                if (prestige == null) return start;
                return prestige > start ? prestige : start;
            }
        }

        public static DateTime ClampQuest(DateTime local) => QuestStart is { } start && start > local ? start : local;
    }
}
