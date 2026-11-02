using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TarkovMonitor
{
    internal static class Stats
    {
        public static string DatabasePath => Path.Join(Application.UserAppDataPath, "..", "TarkovMonitor.db");
        private static readonly Lazy<StatsDatabase> Database = new(() => new StatsDatabase(DatabasePath));

        /// <summary>Raised after a raid or sale was recorded, so views can refresh.</summary>
        public static event EventHandler? Changed;

        public static void NotifyChanged() => Changed?.Invoke(null, EventArgs.Empty);

        public static void ClearData()
        {
            Database.Value.ClearData();
            Changed?.Invoke(null, EventArgs.Empty);
        }

        public static void AddFleaSale(FleaSoldMessageLogContent e, Profile profile)
        {
            Database.Value.AddFleaSale(e, profile);
            Changed?.Invoke(null, EventArgs.Empty);
        }

        public static int GetTotalSales(string currency) => Database.Value.GetTotalSales(currency);

        public static void AddRaid(RaidInfoEventArgs e)
        {
            if (Database.Value.AddRaid(e))
            {
                Changed?.Invoke(null, EventArgs.Empty);
            }
        }

        /// <summary>Sets the map of the latest raid of the profile that was stored without one.</summary>
        public static void SetRaidMap(string? profileId, string? raidId, string? mapNameId)
        {
            if (Database.Value.SetRaidMap(profileId, raidId, mapNameId))
            {
                Changed?.Invoke(null, EventArgs.Empty);
            }
        }

        /// <summary>Stores the end time of the latest open raid of the profile (optionally matched by raid id).</summary>
        public static void EndRaid(string? profileId, string? raidId)
        {
            if (Database.Value.EndRaid(profileId, raidId))
            {
                Changed?.Invoke(null, EventArgs.Empty);
            }
        }

        public static int GetTotalRaids(string mapNameId) => Database.Value.GetTotalRaids(mapNameId);

        public static Dictionary<string, int> GetTotalRaidsPerMap(RaidType raidType) =>
            Database.Value.GetTotalRaidsPerMap(raidType, TarkovDev.Maps);

        /// <summary>Raised after a marker was saved or deleted.</summary>
        public static event EventHandler? MarkersChanged;

        public static List<MapMarker> GetMarkers(string map) => Database.Value.GetMarkers(map);

        /// <summary>Every marker on every map, for the Markers tab and share codes.</summary>
        public static List<MapMarker> GetAllMarkers() => Database.Value.GetMarkers(null);

        /// <summary>Marker categories in their order; the "other" category always exists.</summary>
        public static List<MarkerCategory> GetMarkerCategories() => Database.Value.GetCategories();

        public static void SaveMarkerCategory(MarkerCategory category)
        {
            Database.Value.SaveCategory(category);
            MarkersChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>Deletes a category; its markers move to "other", which cannot be deleted.</summary>
        public static void DeleteMarkerCategory(string id)
        {
            if (id == MarkerCategory.OtherId)
            {
                return;
            }
            Database.Value.DeleteCategory(id);
            MarkersChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>Saves several markers at once (an import) with one change notification.</summary>
        public static void SaveMarkers(IEnumerable<MapMarker> markers)
        {
            foreach (var marker in markers)
            {
                Database.Value.SaveMarker(marker);
            }
            MarkersChanged?.Invoke(null, EventArgs.Empty);
        }

        public static void SaveMarker(MapMarker marker)
        {
            Database.Value.SaveMarker(marker);
            MarkersChanged?.Invoke(null, EventArgs.Empty);
        }

        public static void DeleteMarker(string id)
        {
            Database.Value.DeleteMarker(id);
            MarkersChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>Everything the dashboard shows from the local database.</summary>
        public static DashboardStats GetDashboardStats(string? profileId, DateTime? fromUtc, DateTime? toUtc) =>
            Database.Value.GetDashboardStats(profileId, fromUtc, toUtc);
    }

    internal sealed record RaidRecord(string? MapNameId, RaidType Type, double QueueSeconds, DateTime TimeUtc, DateTime? EndedUtc)
    {
        public TimeSpan? Duration => EndedUtc.HasValue && EndedUtc > TimeUtc ? EndedUtc - TimeUtc : null;
    }

    /// <summary>A marker the player placed on a map (Leaflet coordinates of the Tarkov.dev map).</summary>
    /// <summary>Modes is a comma list of EftSessionMode names the marker is shown in (empty = all).</summary>
    public sealed record MapMarker(string Id, string Map, double Lat, double Lng, string Label, string Description, string Icon, string Color, string Modes, string Category = MarkerCategory.OtherId)
    {
        public bool ShownIn(EftSessionMode mode) => mode == EftSessionMode.Unknown || string.IsNullOrEmpty(Modes)
            || Modes.Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(mode.ToString(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A marker category: name and colour, kept in the user's order. Markers take its colour.</summary>
    public sealed record MarkerCategory(string Id, string Name, string Color, int Sort)
    {
        /// <summary>The fallback category; it can be renamed and recoloured but not deleted.</summary>
        public const string OtherId = "other";
    }

    internal sealed record FleaSaleRecord(string ItemId, string? Buyer, int Count, string Currency, int Price, DateTime TimeUtc);

    internal sealed record MapRaidCount(string MapNameId, Dictionary<RaidType, int> ByType)
    {
        public int Total => ByType.Values.Sum();
    }

    /// <summary>One bar of the raids-per-day chart. Days before the period start are placeholders.</summary>
    internal sealed record RaidDay(DateTime Day, int Count, bool InPeriod);

    internal sealed record MapQueueStats(string MapNameId, int Raids, double AverageSeconds, double BestSeconds, double WorstSeconds);

    internal sealed class DashboardStats
    {
        public int TotalRaids { get; init; }
        public Dictionary<RaidType, int> RaidsByType { get; init; } = new();
        public double AverageQueueSeconds { get; init; }
        public List<MapRaidCount> RaidsPerMap { get; init; } = new();
        public List<MapQueueStats> QueuePerMap { get; init; } = new();
        /// <summary>
        /// Raid counts for up to 14 local days, oldest first, ending today or at the end
        /// of a past period. A period that started more recently begins at its first day.
        /// </summary>
        public List<RaidDay> RaidsPerDay { get; init; } = new();
        /// <summary>The first counted day when the period starts inside the 14 days (later than the last day when nothing counts), else null.</summary>
        public DateTime? RaidsPerDaySince { get; init; }
        /// <summary>True when the chart ends today rather than at the end of a past period.</summary>
        public bool RaidsPerDayEndsToday { get; init; } = true;
        public List<RaidRecord> RecentRaids { get; init; } = new();
        public RaidRecord? LastRaid { get; init; }
        public long RoublesAllTime { get; init; }
        public long RoublesLastWeek { get; init; }
        public int SalesCount { get; init; }
        public List<FleaSaleRecord> RecentSales { get; init; } = new();
    }

    internal sealed class StatsDatabase : IDisposable
    {
        private const string Roubles = "5449016a4bdc2d6f028b456f";
        private readonly SqliteConnection connection;
        private readonly object gate = new();

        internal StatsDatabase(string databasePath)
        {
            connection = new SqliteConnection($"Data Source={databasePath};");
            connection.Open();
            CreateTables();
            UpdateDatabase();
        }

        public void Dispose()
        {
            connection.Dispose();
        }

        internal void ClearData()
        {
            lock (gate)
            {
                var tableNames = new List<string>();
                using (var command = CreateCommand("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';"))
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        tableNames.Add(reader.GetString(0));
                    }
                }

                using var transaction = connection.BeginTransaction();
                foreach (var tableName in tableNames)
                {
                    using var command = CreateCommand($"DELETE FROM [{tableName}];", transaction);
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        internal void AddFleaSale(FleaSoldMessageLogContent e, Profile profile)
        {
            const string sql = "INSERT INTO flea_sales(profile_id, item_id, buyer, count, currency, price) VALUES(@profile_id, @item_id, @buyer, @count, @currency, @price);";
            var receivedItem = e.ReceivedItems.First();
            ExecuteNonQuery(sql, new Dictionary<string, object?>
            {
                ["profile_id"] = profile.Id,
                ["item_id"] = e.SoldItemId,
                ["buyer"] = e.Buyer,
                ["count"] = e.SoldItemCount,
                ["currency"] = receivedItem.Key,
                ["price"] = receivedItem.Value,
            });
        }

        internal int GetTotalSales(string currency)
        {
            return ExecuteScalarInt(
                "SELECT COALESCE(SUM(price), 0) FROM flea_sales WHERE currency = @currency;",
                new Dictionary<string, object?> { ["currency"] = currency });
        }

        internal bool AddRaid(RaidInfoEventArgs e)
        {
            lock (gate)
            {
                // A second monitor instance or a repeated log line must not create a second row.
                const string duplicateSql = "SELECT COUNT(id) FROM raids WHERE profile_id = @profile_id AND ended IS NULL"
                    + " AND ((@raid_id <> '' AND raid_id = @raid_id)"
                    + " OR (time >= @since AND (COALESCE(map, '') = COALESCE(@map, '') OR COALESCE(@map, '') = '')));";
                using (var check = CreateCommand(duplicateSql, parameters: new Dictionary<string, object?>
                {
                    ["profile_id"] = e.Profile.Id,
                    ["raid_id"] = e.RaidInfo.RaidId ?? "",
                    ["map"] = e.RaidInfo.Map?.nameId,
                    ["since"] = DateTime.UtcNow.AddSeconds(-90).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                }))
                {
                    if (Convert.ToInt32(check.ExecuteScalar()) > 0)
                    {
                        return false;
                    }
                }
                // An instance that started during matching stores the raid before it
                // knows the map. When the full raid arrives shortly after, that row is
                // completed instead of leaving an "Unknown map" raid next to it.
                if (!string.IsNullOrEmpty(e.RaidInfo.Map?.nameId))
                {
                    const string completeSql = "UPDATE raids SET map = @map, raid_type = @raid_type, queue_time = @queue_time, raid_id = @raid_id"
                        + " WHERE id = (SELECT id FROM raids WHERE profile_id = @profile_id AND ended IS NULL AND time >= @since"
                        + " AND COALESCE(map, '') = '' AND COALESCE(raid_id, '') = '' ORDER BY id DESC LIMIT 1);";
                    using var complete = CreateCommand(completeSql, parameters: new Dictionary<string, object?>
                    {
                        ["profile_id"] = e.Profile.Id,
                        ["map"] = e.RaidInfo.Map?.nameId,
                        ["raid_type"] = (int)e.RaidInfo.RaidType,
                        ["queue_time"] = e.RaidInfo.QueueTime,
                        ["raid_id"] = e.RaidInfo.RaidId ?? "",
                        ["since"] = DateTime.UtcNow.AddSeconds(-90).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    });
                    if (complete.ExecuteNonQuery() > 0)
                    {
                        return true;
                    }
                }
                const string sql = "INSERT INTO raids(profile_id, map, raid_type, queue_time, raid_id) VALUES (@profile_id, @map, @raid_type, @queue_time, @raid_id);";
                using var command = CreateCommand(sql, parameters: new Dictionary<string, object?>
                {
                    ["profile_id"] = e.Profile.Id,
                    ["map"] = e.RaidInfo.Map?.nameId,
                    ["raid_type"] = (int)e.RaidInfo.RaidType,
                    ["queue_time"] = e.RaidInfo.QueueTime,
                    ["raid_id"] = e.RaidInfo.RaidId,
                });
                command.ExecuteNonQuery();
                return true;
            }
        }

        internal bool SetRaidMap(string? profileId, string? raidId, string? mapNameId)
        {
            if (string.IsNullOrEmpty(mapNameId))
            {
                return false;
            }
            const string sql = "UPDATE raids SET map = @map WHERE id = (SELECT id FROM raids WHERE (map IS NULL OR map = '')"
                + " AND (@profile_id IS NULL OR profile_id = @profile_id)"
                + " AND (@raid_id IS NULL OR raid_id = @raid_id OR raid_id = '')"
                + " ORDER BY id DESC LIMIT 1);";
            lock (gate)
            {
                using var command = CreateCommand(sql, parameters: new Dictionary<string, object?>
                {
                    ["map"] = mapNameId,
                    ["profile_id"] = string.IsNullOrEmpty(profileId) ? null : profileId,
                    ["raid_id"] = string.IsNullOrEmpty(raidId) ? null : raidId,
                });
                return command.ExecuteNonQuery() > 0;
            }
        }

        internal bool EndRaid(string? profileId, string? raidId)
        {
            // The latest raid without an end time; a raid id narrows it down when the log had one.
            const string sql = "UPDATE raids SET ended = CURRENT_TIMESTAMP WHERE id = (SELECT id FROM raids WHERE ended IS NULL"
                + " AND (@profile_id IS NULL OR profile_id = @profile_id)"
                + " AND (@raid_id IS NULL OR raid_id = @raid_id OR raid_id = '')"
                + " ORDER BY id DESC LIMIT 1);";
            lock (gate)
            {
                using var command = CreateCommand(sql, parameters: new Dictionary<string, object?>
                {
                    ["profile_id"] = string.IsNullOrEmpty(profileId) ? null : profileId,
                    ["raid_id"] = string.IsNullOrEmpty(raidId) ? null : raidId,
                });
                return command.ExecuteNonQuery() > 0;
            }
        }

        internal int GetTotalRaids(string mapNameId)
        {
            return ExecuteScalarInt(
                "SELECT COUNT(id) FROM raids WHERE map = @map;",
                new Dictionary<string, object?> { ["map"] = mapNameId });
        }

        internal Dictionary<string, int> GetTotalRaidsPerMap(RaidType raidType, IEnumerable<TarkovDev.Map> maps)
        {
            var mapTotals = new Dictionary<string, int>();
            lock (gate)
            {
                using var command = CreateCommand(
                    "SELECT map, COUNT(id) FROM raids WHERE raid_type = @raid_type GROUP BY map;",
                    parameters: new Dictionary<string, object?> { ["raid_type"] = (int)raidType });
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (!reader.IsDBNull(0))
                    {
                        mapTotals[reader.GetString(0)] = reader.GetInt32(1);
                    }
                }
            }

            return maps.ToDictionary(
                map => map.name,
                map => mapTotals.TryGetValue(map.nameId, out var total) ? total : 0);
        }

        internal DashboardStats GetDashboardStats(string? profileId, DateTime? fromUtc, DateTime? toUtc)
        {
            lock (gate)
            {
                var raids = ReadRaids(profileId);
                var sales = ReadSales(profileId);
                if (HistoryStart.ValueUtc is { } startUtc)
                {
                    raids = raids.Where(raid => raid.TimeUtc >= startUtc).ToList();
                    sales = sales.Where(sale => sale.TimeUtc >= startUtc).ToList();
                }
                // The dashboard period (everything, before the first prestige, or one prestige).
                if (fromUtc.HasValue)
                {
                    raids = raids.Where(raid => raid.TimeUtc >= fromUtc.Value).ToList();
                    sales = sales.Where(sale => sale.TimeUtc >= fromUtc.Value).ToList();
                }
                if (toUtc.HasValue)
                {
                    raids = raids.Where(raid => raid.TimeUtc < toUtc.Value).ToList();
                    sales = sales.Where(sale => sale.TimeUtc < toUtc.Value).ToList();
                }
                var mapRaids = raids;

                var byType = raids.GroupBy(raid => raid.Type).ToDictionary(group => group.Key, group => group.Count());
                var queued = raids.Where(raid => raid.QueueSeconds > 0).ToList();

                var perMap = mapRaids
                    .Where(raid => !string.IsNullOrEmpty(raid.MapNameId))
                    .GroupBy(raid => raid.MapNameId!)
                    .Select(group => new MapRaidCount(group.Key, group.GroupBy(raid => raid.Type).ToDictionary(g => g.Key, g => g.Count())))
                    .OrderByDescending(entry => entry.Total)
                    .ToList();

                var queuePerMap = mapRaids
                    .Where(raid => !string.IsNullOrEmpty(raid.MapNameId) && raid.QueueSeconds > 0)
                    .GroupBy(raid => raid.MapNameId!)
                    .Select(group => new MapQueueStats(group.Key, group.Count(), group.Average(r => r.QueueSeconds), group.Min(r => r.QueueSeconds), group.Max(r => r.QueueSeconds)))
                    .OrderBy(entry => entry.AverageSeconds)
                    .ToList();

                // The chart follows the selected period: it ends today or on the day a past
                // period ended, and starts no earlier than the period (or the history start).
                // Days before the start are not zero raids, they are not counted at all.
                const int ChartDays = 14;
                const int MinimumBars = 7;
                var today = DateTime.Now.Date;
                var lastDay = today;
                if (toUtc.HasValue && toUtc.Value.ToLocalTime().Date < today)
                {
                    lastDay = toUtc.Value.ToLocalTime().Date;
                }
                DateTime? periodStart = null;
                foreach (var bound in new[] { HistoryStart.ValueUtc, fromUtc })
                {
                    if (bound is { } utc && (periodStart is null || utc.ToLocalTime().Date > periodStart))
                    {
                        periodStart = utc.ToLocalTime().Date;
                    }
                }
                var firstDay = lastDay.AddDays(-(ChartDays - 1));
                DateTime? since = null;
                if (periodStart is { } start && start > firstDay)
                {
                    // A period that lies wholly before the history start keeps 14 empty days.
                    since = start;
                    if (start <= lastDay)
                    {
                        firstDay = start.AddDays(-Math.Max(0, MinimumBars - 1 - (lastDay - start).Days));
                    }
                }
                // A past period that ended before the history start has nothing counted at all.
                if (toUtc.HasValue && HistoryStart.ValueUtc is { } historyUtc && toUtc.Value <= historyUtc)
                {
                    since = lastDay.AddDays(1);
                    firstDay = lastDay.AddDays(-(ChartDays - 1));
                }
                var perDay = new List<RaidDay>();
                for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
                {
                    var inPeriod = since is null || day >= since.Value;
                    perDay.Add(new RaidDay(day, inPeriod ? raids.Count(raid => raid.TimeUtc.ToLocalTime().Date == day) : 0, inPeriod));
                }

                var weekAgo = DateTime.UtcNow.AddDays(-7);
                return new DashboardStats
                {
                    TotalRaids = raids.Count,
                    RaidsByType = byType,
                    AverageQueueSeconds = queued.Count > 0 ? queued.Average(raid => raid.QueueSeconds) : 0,
                    RaidsPerMap = perMap,
                    QueuePerMap = queuePerMap,
                    RaidsPerDay = perDay,
                    RaidsPerDaySince = since,
                    RaidsPerDayEndsToday = lastDay == today,
                    RecentRaids = raids.OrderByDescending(raid => raid.TimeUtc).Take(6).ToList(),
                    LastRaid = raids.OrderByDescending(raid => raid.TimeUtc).FirstOrDefault(),
                    RoublesAllTime = sales.Where(sale => sale.Currency == Roubles).Sum(sale => (long)sale.Price),
                    RoublesLastWeek = sales.Where(sale => sale.Currency == Roubles && sale.TimeUtc >= weekAgo).Sum(sale => (long)sale.Price),
                    SalesCount = sales.Count,
                    RecentSales = sales.OrderByDescending(sale => sale.TimeUtc).Take(6).ToList(),
                };
            }
        }

        private List<RaidRecord> ReadRaids(string? profileId)
        {
            var result = new List<RaidRecord>();
            using var command = CreateCommand(
                "SELECT map, raid_type, queue_time, time, ended FROM raids WHERE (@profile_id IS NULL OR profile_id = @profile_id) ORDER BY id;",
                parameters: new Dictionary<string, object?> { ["profile_id"] = string.IsNullOrEmpty(profileId) ? null : profileId });
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var time = ParseTimestamp(reader.IsDBNull(3) ? null : reader.GetString(3));
                if (time == null)
                {
                    continue;
                }
                result.Add(new RaidRecord(
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.IsDBNull(1) ? RaidType.Unknown : (RaidType)reader.GetInt32(1),
                    reader.IsDBNull(2) ? 0 : reader.GetDouble(2),
                    time.Value,
                    ParseTimestamp(reader.IsDBNull(4) ? null : reader.GetString(4))));
            }
            return result;
        }

        private List<FleaSaleRecord> ReadSales(string? profileId)
        {
            var result = new List<FleaSaleRecord>();
            using var command = CreateCommand(
                "SELECT item_id, buyer, count, currency, price, time FROM flea_sales WHERE (@profile_id IS NULL OR profile_id = @profile_id) ORDER BY id;",
                parameters: new Dictionary<string, object?> { ["profile_id"] = string.IsNullOrEmpty(profileId) ? null : profileId });
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var time = ParseTimestamp(reader.IsDBNull(5) ? null : reader.GetString(5));
                if (time == null || reader.IsDBNull(0))
                {
                    continue;
                }
                result.Add(new FleaSaleRecord(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                    reader.IsDBNull(3) ? "" : reader.GetString(3),
                    reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                    time.Value));
            }
            return result;
        }

        /// <summary>SQLite CURRENT_TIMESTAMP values are UTC "yyyy-MM-dd HH:mm:ss".</summary>
        private static DateTime? ParseTimestamp(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }
            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : null;
        }

        internal List<MapMarker> GetMarkers(string? map)
        {
            var result = new List<MapMarker>();
            lock (gate)
            {
                using var command = map == null
                    ? CreateCommand("SELECT id, map, lat, lng, label, description, icon, color, modes, category FROM map_markers ORDER BY created;")
                    : CreateCommand("SELECT id, map, lat, lng, label, description, icon, color, modes, category FROM map_markers WHERE map = @map ORDER BY created;",
                        parameters: new Dictionary<string, object?> { ["map"] = map });
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new MapMarker(reader.GetString(0), reader.GetString(1), reader.GetDouble(2), reader.GetDouble(3),
                        reader.IsDBNull(4) ? "" : reader.GetString(4), reader.IsDBNull(5) ? "" : reader.GetString(5),
                        reader.IsDBNull(6) ? "" : reader.GetString(6), reader.IsDBNull(7) ? "#e53935" : reader.GetString(7),
                        reader.IsDBNull(8) ? "" : reader.GetString(8),
                        reader.IsDBNull(9) || string.IsNullOrEmpty(reader.GetString(9)) ? MarkerCategory.OtherId : reader.GetString(9)));
                }
            }
            return result;
        }

        internal List<MarkerCategory> GetCategories()
        {
            var result = new List<MarkerCategory>();
            lock (gate)
            {
                using var command = CreateCommand("SELECT id, name, color, sort FROM marker_categories ORDER BY sort, name;");
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new MarkerCategory(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
                }
            }
            return result;
        }

        internal void SaveCategory(MarkerCategory category)
        {
            ExecuteNonQuery("INSERT INTO marker_categories (id, name, color, sort) VALUES (@id, @name, @color, @sort)"
                + " ON CONFLICT(id) DO UPDATE SET name = excluded.name, color = excluded.color, sort = excluded.sort;",
                new Dictionary<string, object?> { ["id"] = category.Id, ["name"] = category.Name, ["color"] = category.Color, ["sort"] = category.Sort });
        }

        internal void DeleteCategory(string id)
        {
            ExecuteNonQuery("UPDATE map_markers SET category = @other WHERE category = @id;",
                new Dictionary<string, object?> { ["id"] = id, ["other"] = MarkerCategory.OtherId });
            ExecuteNonQuery("DELETE FROM marker_categories WHERE id = @id;", new Dictionary<string, object?> { ["id"] = id });
        }

        internal void SaveMarker(MapMarker marker)
        {
            ExecuteNonQuery("INSERT INTO map_markers (id, map, lat, lng, label, description, icon, color, modes, category) VALUES (@id, @map, @lat, @lng, @label, @description, @icon, @color, @modes, @category)"
                + " ON CONFLICT(id) DO UPDATE SET map = excluded.map, lat = excluded.lat, lng = excluded.lng, label = excluded.label, description = excluded.description, icon = excluded.icon, color = excluded.color, modes = excluded.modes, category = excluded.category;",
                new Dictionary<string, object?>
                {
                    ["id"] = marker.Id, ["map"] = marker.Map, ["lat"] = marker.Lat, ["lng"] = marker.Lng,
                    ["label"] = marker.Label, ["description"] = marker.Description, ["icon"] = marker.Icon, ["color"] = marker.Color, ["modes"] = marker.Modes,
                    ["category"] = string.IsNullOrEmpty(marker.Category) ? MarkerCategory.OtherId : marker.Category,
                });
        }

        internal void DeleteMarker(string id)
        {
            ExecuteNonQuery("DELETE FROM map_markers WHERE id = @id;", new Dictionary<string, object?> { ["id"] = id });
        }

        private void CreateTables()
        {
            var commands = new[]
            {
                "CREATE TABLE IF NOT EXISTS map_markers (id TEXT PRIMARY KEY, map TEXT NOT NULL, lat REAL NOT NULL, lng REAL NOT NULL, label TEXT, description TEXT, icon TEXT, color TEXT, modes TEXT, created TIMESTAMP DEFAULT CURRENT_TIMESTAMP);",
                "CREATE TABLE IF NOT EXISTS marker_categories (id TEXT PRIMARY KEY, name TEXT NOT NULL, color TEXT NOT NULL, sort INTEGER NOT NULL DEFAULT 0);",
                "CREATE TABLE IF NOT EXISTS flea_sales (id INTEGER PRIMARY KEY, profile_id VARCHAR(24), item_id CHAR(24), buyer VARCHAR(14), count INT, currency CHAR(24), price INT, time TIMESTAMP DEFAULT CURRENT_TIMESTAMP);",
                "CREATE TABLE IF NOT EXISTS raids (id INTEGER PRIMARY KEY, profile_id VARCHAR(24), map VARCHAR(24), raid_type INT, queue_time DECIMAL(6,2), raid_id VARCHAR(24), time TIMESTAMP DEFAULT CURRENT_TIMESTAMP, ended TIMESTAMP);",
            };
            foreach (var sql in commands)
            {
                ExecuteNonQuery(sql);
            }
        }

        private void UpdateDatabase()
        {
            EnsureColumn("raids", "profile_id", "VARCHAR(24)");
            EnsureColumn("flea_sales", "profile_id", "VARCHAR(24)");
            EnsureColumn("raids", "ended", "TIMESTAMP");
            EnsureColumn("map_markers", "category", "TEXT");
            SeedMarkerCategories();
            // An older release wrote the map object's type name instead of its id; those rows have no usable map.
            ExecuteNonQuery("UPDATE raids SET map = NULL WHERE map LIKE 'TarkovMonitor.%';");
        }

        // Ready-made categories for a fresh start, in the app language; all of them can be
        // renamed, recoloured or deleted, except that "other" stays. Markers from before
        // categories existed get the category matching their old colour.
        private void SeedMarkerCategories()
        {
            using (var count = CreateCommand("SELECT COUNT(*) FROM marker_categories;"))
            {
                if (Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
                {
                    return;
                }
            }
            var german = (Properties.Settings.Default.language ?? "").StartsWith("de", StringComparison.OrdinalIgnoreCase);
            var defaults = new[]
            {
                ("loot", german ? "Loot" : "Loot", "#fdd835"),
                ("keys", german ? "Schlüssel und Türen" : "Keys and doors", "#1e88e5"),
                ("extracts", german ? "Extracts" : "Extracts", "#43a047"),
                ("danger", german ? "Gefahr" : "Danger", "#e53935"),
                ("quests", german ? "Quests" : "Quests", "#8e24aa"),
                (MarkerCategory.OtherId, german ? "Sonstiges" : "Other", "#9e9e9e"),
            };
            for (var index = 0; index < defaults.Length; index++)
            {
                var (id, name, color) = defaults[index];
                SaveCategory(new MarkerCategory(id, name, color, index));
            }
            ExecuteNonQuery("UPDATE map_markers SET category = CASE lower(color)"
                + " WHEN '#e53935' THEN 'danger' WHEN '#fb8c00' THEN 'loot' WHEN '#fdd835' THEN 'loot'"
                + " WHEN '#43a047' THEN 'extracts' WHEN '#1e88e5' THEN 'keys' WHEN '#8e24aa' THEN 'quests'"
                + " ELSE 'other' END WHERE category IS NULL OR category = '';");
        }

        private void EnsureColumn(string tableName, string columnName, string definition)
        {
            var exists = false;
            using (var command = CreateCommand($"PRAGMA table_info([{tableName}]);"))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (reader.GetString(reader.GetOrdinal("name")) == columnName)
                    {
                        exists = true;
                        break;
                    }
                }
            }
            if (!exists)
            {
                ExecuteNonQuery($"ALTER TABLE [{tableName}] ADD COLUMN {columnName} {definition};");
            }
        }

        private int ExecuteScalarInt(string sql, Dictionary<string, object?>? parameters = null)
        {
            lock (gate)
            {
                using var command = CreateCommand(sql, parameters: parameters);
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        private void ExecuteNonQuery(string sql, Dictionary<string, object?>? parameters = null)
        {
            lock (gate)
            {
                using var command = CreateCommand(sql, parameters: parameters);
                command.ExecuteNonQuery();
            }
        }

        private SqliteCommand CreateCommand(
            string sql,
            SqliteTransaction? transaction = null,
            Dictionary<string, object?>? parameters = null)
        {
            var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = transaction;
            if (parameters != null)
            {
                foreach (var parameter in parameters)
                {
                    command.Parameters.AddWithValue($"@{parameter.Key}", parameter.Value ?? DBNull.Value);
                }
            }
            return command;
        }
    }
}
