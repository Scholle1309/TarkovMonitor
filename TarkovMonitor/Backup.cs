using System.ComponentModel;
using System.Configuration;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TarkovMonitor
{
    /// <summary>
    /// Saves and restores everything Tarkov Monitor keeps on this PC: the database
    /// (raids, flea sales, quest history, map markers), custom sounds and settings.
    /// Tarkov Tracker tokens are left out on purpose: Windows encrypts them for this
    /// user on this PC, so a backup could not use them anywhere else.
    /// </summary>
    internal static class Backup
    {
        private const string DatabaseEntry = "TarkovMonitor.db";
        private const string SettingsEntry = "settings.json";
        private const string ManifestEntry = "manifest.json";
        private const string SoundsFolder = "sounds/";

        private static readonly HashSet<string> ExcludedSettings = new(StringComparer.Ordinal)
        {
            "tarkovTrackerToken",
            "tarkovTrackerTokens",
            "tarkovTrackerModeTokens",
            "tarkovTrackerVerifiedModeTokenHashes",
            "tarkovTrackerOrgTokenStore",
            "upgradeRequired",
            "windowWidth",
            "windowHeight",
            "windowMaximized",
        };

        /// <summary>
        /// A restored database waits here until the next start, because the running
        /// application keeps the live database open.
        /// </summary>
        internal static string PendingDatabasePath => Stats.DatabasePath + ".restore";

        internal static string DefaultFileName => $"TarkovMonitor-backup-{DateTime.Now:yyyy-MM-dd}.zip";

        internal static void Export(string zipPath)
        {
            var workFolder = Path.Combine(Path.GetTempPath(), "TarkovMonitor-backup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workFolder);
            try
            {
                var databaseCopy = Path.Combine(workFolder, DatabaseEntry);
                if (File.Exists(Stats.DatabasePath))
                {
                    // VACUUM INTO writes a consistent copy while the app keeps using the database.
                    using var connection = new SqliteConnection($"Data Source={Stats.DatabasePath};Pooling=False;");
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "VACUUM INTO $path;";
                    command.Parameters.AddWithValue("$path", databaseCopy);
                    command.ExecuteNonQuery();
                }

                if (File.Exists(zipPath))
                {
                    File.Delete(zipPath);
                }
                using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
                if (File.Exists(databaseCopy))
                {
                    zip.CreateEntryFromFile(databaseCopy, DatabaseEntry);
                }
                if (Directory.Exists(Sound.CustomSoundsPath))
                {
                    foreach (var file in Directory.GetFiles(Sound.CustomSoundsPath))
                    {
                        zip.CreateEntryFromFile(file, SoundsFolder + Path.GetFileName(file));
                    }
                }
                WriteJson(zip, SettingsEntry, ReadSettings());
                WriteJson(zip, ManifestEntry, new Dictionary<string, string>
                {
                    ["app"] = "Tarkov Monitor",
                    ["version"] = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "",
                    ["created"] = DateTime.UtcNow.ToString("o"),
                });
            }
            finally
            {
                try
                {
                    Directory.Delete(workFolder, recursive: true);
                }
                catch
                {
                    // A leftover temporary copy is harmless.
                }
            }
        }

        /// <summary>
        /// Applies settings and sounds right away and stages the database for the next
        /// start. The caller restarts the application afterwards.
        /// </summary>
        internal static void Restore(string zipPath)
        {
            using var zip = ZipFile.OpenRead(zipPath);
            if (zip.GetEntry(ManifestEntry) == null)
            {
                throw new InvalidDataException("This file is not a Tarkov Monitor backup.");
            }

            var database = zip.GetEntry(DatabaseEntry);
            database?.ExtractToFile(PendingDatabasePath, overwrite: true);

            foreach (var entry in zip.Entries.Where(entry => entry.FullName.StartsWith(SoundsFolder, StringComparison.Ordinal) && entry.Name.Length > 0))
            {
                Directory.CreateDirectory(Sound.CustomSoundsPath);
                // Only the file name is used, so an entry cannot write outside the sounds folder.
                entry.ExtractToFile(Path.Combine(Sound.CustomSoundsPath, Path.GetFileName(entry.Name)), overwrite: true);
            }

            var settings = zip.GetEntry(SettingsEntry);
            if (settings != null)
            {
                using var stream = settings.Open();
                var values = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new();
                ApplySettings(values);
            }
        }

        /// <summary>
        /// Puts a database restored in the previous session in place. Runs at startup,
        /// before anything opens the database; the previous file is kept next to it.
        /// </summary>
        internal static void ApplyPendingDatabase()
        {
            if (!File.Exists(PendingDatabasePath))
            {
                return;
            }

            // The previous process may still be closing and holding the file.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(Stats.DatabasePath))
                    {
                        File.Copy(Stats.DatabasePath, Stats.DatabasePath + ".before-restore", overwrite: true);
                    }
                    File.Move(PendingDatabasePath, Stats.DatabasePath, overwrite: true);
                    return;
                }
                catch (IOException) when (attempt < 40)
                {
                    Thread.Sleep(250);
                }
            }
        }

        private static Dictionary<string, string> ReadSettings()
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (SettingsProperty property in Properties.Settings.Default.Properties)
            {
                if (ExcludedSettings.Contains(property.Name))
                {
                    continue;
                }
                var value = Properties.Settings.Default[property.Name];
                values[property.Name] = TypeDescriptor.GetConverter(property.PropertyType).ConvertToInvariantString(value) ?? "";
            }
            return values;
        }

        private static void ApplySettings(Dictionary<string, string> values)
        {
            foreach (SettingsProperty property in Properties.Settings.Default.Properties)
            {
                if (ExcludedSettings.Contains(property.Name) || !values.TryGetValue(property.Name, out var text))
                {
                    continue;
                }
                try
                {
                    Properties.Settings.Default[property.Name] = TypeDescriptor.GetConverter(property.PropertyType).ConvertFromInvariantString(text);
                }
                catch
                {
                    // A value this version cannot read keeps its current setting.
                }
            }

            // A logs folder from another PC would point nowhere here.
            if (!string.IsNullOrEmpty(Properties.Settings.Default.customLogsPath) && !Directory.Exists(Properties.Settings.Default.customLogsPath))
            {
                Properties.Settings.Default.customLogsPath = "";
            }
            Properties.Settings.Default.Save();
        }

        private static void WriteJson<T>(ZipArchive zip, string name, T value)
        {
            var entry = zip.CreateEntry(name);
            using var stream = entry.Open();
            JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true });
        }
    }
}
