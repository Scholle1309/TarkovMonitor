namespace TarkovMonitor
{
    /// <summary>
    /// Remembers the last EFT profile seen in each game mode (PVE, PVP, PVP Season),
    /// so the app can show a mode's data while the game is not running and switch
    /// between modes from the title bar. While the game runs, the game decides.
    /// </summary>
    public static class GameModes
    {
        public static readonly EftSessionMode[] All = { EftSessionMode.PVE, EftSessionMode.Regular, EftSessionMode.Seasonal };

        private static readonly object gate = new();

        /// <summary>Raised when a profile was remembered for a mode.</summary>
        public static event EventHandler? Changed;

        public static string Label(EftSessionMode mode) => mode switch
        {
            EftSessionMode.PVE => "PVE",
            EftSessionMode.Regular => "PVP",
            EftSessionMode.Seasonal => "PVP Season",
            _ => "",
        };

        /// <summary>The mode played most recently, or Unknown before the first game session.</summary>
        public static EftSessionMode LastMode =>
            Enum.TryParse<EftSessionMode>(Properties.Settings.Default.lastTarkovSessionMode, out var mode) ? mode : EftSessionMode.Unknown;

        public static Profile? Stored(EftSessionMode mode)
        {
            lock (gate)
            {
                return Load().TryGetValue(mode, out var profile) ? profile.Snapshot() : null;
            }
        }

        /// <summary>Stores the profile of a recognised game session for its mode.</summary>
        public static void Remember(Profile profile)
        {
            var snapshot = profile.Snapshot();
            if (!snapshot.HasIdentity || !All.Contains(snapshot.SessionMode))
            {
                return;
            }

            bool changed;
            lock (gate)
            {
                var profiles = Load();
                changed = !profiles.TryGetValue(snapshot.SessionMode, out var known)
                    || known.Id != snapshot.Id
                    || known.AccountId != snapshot.AccountId
                    || known.Type != snapshot.Type;
                profiles[snapshot.SessionMode] = snapshot;
                var serialized = string.Join(";", profiles.Values.Select(item => $"{item.SessionMode}|{item.Id}|{item.AccountId}|{item.Type}"));
                var lastMode = snapshot.SessionMode.ToString();
                if (changed || Properties.Settings.Default.lastTarkovSessionMode != lastMode)
                {
                    Properties.Settings.Default.knownProfiles = serialized;
                    Properties.Settings.Default.lastTarkovSessionMode = lastMode;
                    Properties.Settings.Default.Save();
                    changed = true;
                }
            }
            if (changed)
            {
                Changed?.Invoke(null, EventArgs.Empty);
            }
        }

        private static Dictionary<EftSessionMode, Profile> Load()
        {
            var profiles = new Dictionary<EftSessionMode, Profile>();
            foreach (var entry in (Properties.Settings.Default.knownProfiles ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = entry.Split('|');
                if (parts.Length != 4
                    || !Enum.TryParse<EftSessionMode>(parts[0], out var mode)
                    || !Enum.TryParse<ProfileType>(parts[3], out var type))
                {
                    continue;
                }
                profiles[mode] = new Profile { SessionMode = mode, Id = parts[1], AccountId = parts[2], Type = type };
            }
            return profiles;
        }
    }
}
