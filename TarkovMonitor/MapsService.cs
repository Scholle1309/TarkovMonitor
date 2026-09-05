using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace TarkovMonitor
{
    /// <summary>
    /// State for the embedded Tarkov.dev map view (the "Maps" tab).
    /// The map page is loaded in an iframe with its own remote-control
    /// session id, so the same socket messages that drive the Tarkov.dev
    /// website remote also drive the embedded map.
    /// </summary>
    public class MapsService
    {
        private const string DefaultMap = "customs";
        private const string SessionAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        private const int SessionLength = 8;

        private readonly object gate = new();
        private JsonObject? lastPositionMessage;
        private int frameGeneration;

        public event EventHandler? Changed;

        /// <summary>Raised when the UI should switch to another tab because of a raid event.</summary>
        public event EventHandler<MapsNavigationRequestedEventArgs>? NavigationRequested;

        public const string MapsRoute = "/maps";
        public const string DashboardRoute = "/";

        public MapsService()
        {
            SessionId = LoadOrCreateSessionId();
        }

        /// <summary>Session id the embedded Tarkov.dev page connects with.</summary>
        public string SessionId { get; }

        /// <summary>Map currently shown (or requested) in the embedded view.</summary>
        public TarkovDev.Map? CurrentMap { get; private set; }

        /// <summary>True once the iframe has been asked to load Tarkov.dev.</summary>
        public bool FrameRequested { get; private set; }

        /// <summary>True once the iframe reported that the Tarkov.dev document loaded.</summary>
        public bool FrameLoaded { get; private set; }

        /// <summary>Changes whenever a full reload of the iframe is wanted.</summary>
        public int FrameGeneration => frameGeneration;

        /// <summary>True while the Tarkov.dev settings page is shown instead of the map.</summary>
        public bool SettingsOpen { get; private set; }

        public string CurrentMapName => CurrentMap?.normalizedName ?? DefaultMap;

        public string FrameUrl => $"https://tarkov.dev/map/{Uri.EscapeDataString(CurrentMapName)}?connection={Uri.EscapeDataString(SessionId)}";

        public string ExternalUrl => $"https://tarkov.dev/map/{Uri.EscapeDataString(CurrentMapName)}";

        /// <summary>Ask the view to load Tarkov.dev if it has not been loaded yet.</summary>
        public void RequestFrame()
        {
            lock (gate)
            {
                if (FrameRequested)
                {
                    return;
                }
                FrameRequested = true;
            }
            RaiseChanged();
        }

        /// <summary>
        /// Toggle between the Tarkov.dev settings page (language, map options)
        /// and the current map. The site header is hidden in the frame, so this
        /// replaces the gear icon of the header.
        /// </summary>
        public async Task ToggleSettingsAsync()
        {
            if (!FrameLoaded)
            {
                return;
            }
            var open = !SettingsOpen;
            var message = open
                ? SocketClient.GetNavigateToPageMessage("settings", "")
                : SocketClient.GetNavigateToPageMessage("map", CurrentMapName);
            try
            {
                await SocketClient.Send(new List<JsonObject> { message }, SocketTargets.MapView).ConfigureAwait(false);
                lock (gate)
                {
                    SettingsOpen = open;
                }
                RaiseChanged();
            }
            catch
            {
                ReloadFrame();
            }
        }

        /// <summary>Force a full reload of the embedded page.</summary>
        public void ReloadFrame()
        {
            lock (gate)
            {
                FrameRequested = true;
                FrameLoaded = false;
                SettingsOpen = false;
                SocketClient.MapViewSessionId = null;
                Interlocked.Increment(ref frameGeneration);
            }
            RaiseChanged();
        }

        /// <summary>Called by the view when the iframe finished loading the document.</summary>
        public async Task FrameLoadedAsync()
        {
            lock (gate)
            {
                FrameLoaded = true;
                SocketClient.MapViewSessionId = SessionId;
            }
            RaiseChanged();

            // The page connects to the socket server after it has rendered;
            // give it a moment before replaying the last known position.
            await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
            await ReplayLastPositionAsync().ConfigureAwait(false);
        }

        /// <summary>Record the map the game is loading so the view follows it.</summary>
        public void SetMap(TarkovDev.Map? map)
        {
            if (map == null)
            {
                return;
            }
            bool changed;
            lock (gate)
            {
                var mapChanged = CurrentMap?.normalizedName != map.normalizedName;
                changed = mapChanged || SettingsOpen;
                CurrentMap = map;
                // Callers send a map command right after this, which leaves the settings page.
                SettingsOpen = false;
                if (mapChanged)
                {
                    // A position from another map is meaningless on the new one.
                    lastPositionMessage = null;
                }
            }
            if (changed)
            {
                RaiseChanged();
            }
        }

        /// <summary>Keep the newest position so it can be replayed after a (re)load.</summary>
        public void RememberPosition(JsonObject positionMessage)
        {
            lock (gate)
            {
                lastPositionMessage = positionMessage;
            }
        }

        /// <summary>
        /// Navigate the embedded page to a map chosen by the user. Uses the
        /// socket command when the page is connected (no reload), otherwise
        /// reloads the frame with the new map in the URL.
        /// </summary>
        public async Task NavigateAsync(TarkovDev.Map map)
        {
            SetMap(map);
            if (!FrameLoaded)
            {
                ReloadFrame();
                return;
            }
            try
            {
                await SocketClient.Send(new List<JsonObject> { SocketClient.GetNavigateToMapMessage(map) }, SocketTargets.MapView).ConfigureAwait(false);
            }
            catch
            {
                ReloadFrame();
            }
        }

        public async Task ReplayLastPositionAsync()
        {
            JsonObject? position;
            TarkovDev.Map? map;
            lock (gate)
            {
                position = lastPositionMessage;
                map = CurrentMap;
            }
            if (position == null || map == null || !FrameLoaded)
            {
                return;
            }
            try
            {
                var messages = new List<JsonObject>
                {
                    SocketClient.GetNavigateToMapMessage(map),
                    position,
                };
                await SocketClient.Send(messages, SocketTargets.MapView).ConfigureAwait(false);
            }
            catch
            {
                // Replay is best effort; the next screenshot sends a fresh position.
            }
        }

        /// <summary>A raid is loading: show the Maps tab (if the setting is on).</summary>
        public void RequestShowMaps()
        {
            if (!Properties.Settings.Default.autoShowMapTab)
            {
                return;
            }
            RequestFrame();
            NavigationRequested?.Invoke(this, new MapsNavigationRequestedEventArgs(MapsRoute, onlyFromMaps: false));
        }

        /// <summary>The raid ended: go back to the dashboard, but only if the Maps tab is showing.</summary>
        public void RequestShowDashboard()
        {
            if (!Properties.Settings.Default.autoShowMapTab)
            {
                return;
            }
            NavigationRequested?.Invoke(this, new MapsNavigationRequestedEventArgs(DashboardRoute, onlyFromMaps: true));
        }

        private void RaiseChanged()
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private static string LoadOrCreateSessionId()
        {
            var stored = Properties.Settings.Default.mapSessionId;
            if (!string.IsNullOrWhiteSpace(stored))
            {
                return stored;
            }
            var chars = new char[SessionLength];
            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = SessionAlphabet[RandomNumberGenerator.GetInt32(SessionAlphabet.Length)];
            }
            var created = new string(chars);
            Properties.Settings.Default.mapSessionId = created;
            Properties.Settings.Default.Save();
            return created;
        }
    }

    public class MapsNavigationRequestedEventArgs : EventArgs
    {
        public MapsNavigationRequestedEventArgs(string route, bool onlyFromMaps)
        {
            Route = route;
            OnlyFromMaps = onlyFromMaps;
        }

        /// <summary>Route to navigate to.</summary>
        public string Route { get; }

        /// <summary>When true, only navigate if the Maps tab is currently shown.</summary>
        public bool OnlyFromMaps { get; }
    }
}
