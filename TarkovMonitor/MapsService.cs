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

        /// <summary>Force a full reload of the embedded page.</summary>
        public void ReloadFrame()
        {
            lock (gate)
            {
                FrameRequested = true;
                FrameLoaded = false;
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
                changed = CurrentMap?.normalizedName != map.normalizedName;
                CurrentMap = map;
                if (changed)
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
}
