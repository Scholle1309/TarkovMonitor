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

        /// <summary>Everything the dashboard shows from the local database.</summary>
        public static DashboardStats GetDashboardStats(string? profileId, DateTime? mapsSinceUtc) =>
            Database.Value.GetDashboardStats(profileId, mapsSinceUtc);
    }

    internal sealed record RaidRecord(string? MapNameId, RaidType Type, double QueueSeconds, DateTime TimeUtc, DateTime? EndedUtc)
    {
        public TimeSpan? Duration => EndedUtc.HasValue && EndedUtc > TimeUtc ? EndedUtc - TimeUtc : null;
    }

    internal sealed record FleaSaleRecord(string ItemId, string? Buyer, int Count, string Currency, int Price, DateTime TimeUtc);

    internal sealed record MapRaidCount(string MapNameId, Dictionary<RaidType, int> ByType)
    {
        public int Total => ByType.Values.Sum();
    }

    internal sealed record MapQueueStats(string MapNameId, int Raids, double AverageSeconds, double BestSeconds, double WorstSeconds);

    internal sealed class DashboardStats
    {
        public int TotalRaids { get; init; }
        public Dictionary<RaidType, int> RaidsByType { get; init; } = new();
        public double AverageQueueSeconds { get; init; }
        public List<MapRaidCount> RaidsPerMap { get; init; } = new();
        public List<MapQueueStats> QueuePerMap { get; init; } = new();
        /// <summary>Raid counts for the last 14 local days, oldest first.</summary>
        public List<(DateTime Day, int Count)> RaidsPerDay { get; init; } = new();
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
                    + " AND ((@raid_id <> '' AND raid_id = @raid_id) OR (time >= @since AND COALESCE(map, '') = COALESCE(@map, '')));";
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

        internal DashboardStats GetDashboardStats(string? profileId, DateTime? mapsSinceUtc)
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
                var mapRaids = mapsSinceUtc.HasValue ? raids.Where(raid => raid.TimeUtc >= mapsSinceUtc.Value).ToList() : raids;

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

                var today = DateTime.Now.Date;
                var perDay = new List<(DateTime, int)>();
                for (var offset = 13; offset >= 0; offset--)
                {
                    var day = today.AddDays(-offset);
                    perDay.Add((day, raids.Count(raid => raid.TimeUtc.ToLocalTime().Date == day)));
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

        private void CreateTables()
        {
            var commands = new[]
            {
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
            // An older release wrote the map object's type name instead of its id; those rows have no usable map.
            ExecuteNonQuery("UPDATE raids SET map = NULL WHERE map LIKE 'TarkovMonitor.%';");
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
