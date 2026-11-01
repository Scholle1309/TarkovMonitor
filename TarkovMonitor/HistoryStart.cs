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
