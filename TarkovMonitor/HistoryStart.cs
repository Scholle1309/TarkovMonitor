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
    }
}
