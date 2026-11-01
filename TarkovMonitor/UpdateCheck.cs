using Refit;

namespace TarkovMonitor
{
    public enum UpdateCheckResult
    {
        UpToDate,
        UpdateAvailable,
        Failed,
    }

    internal class UpdateCheck
    {
        internal interface IGitHubAPI
        {
            [Get("/releases/latest")]
            [Headers("user-agent: tarkov-monitor")]
            Task<ReleaseData> GetLatestRelease();

            [Get("/releases?per_page=30")]
            [Headers("user-agent: tarkov-monitor")]
            Task<List<ReleaseData>> GetReleases();
        }

        // Releases of this fork, not of the upstream project it was forked from.
        private static readonly string repo = "Scholle1309/TarkovMonitor";
        private static readonly System.Timers.Timer updateCheckTimer;

        private static readonly IGitHubAPI api = RestService.For<IGitHubAPI>($"https://api.github.com/repos/{repo}");

        public static event EventHandler<NewVersionEventArgs>? NewVersion;
        public static event EventHandler<ExceptionEventArgs>? Error;

        static UpdateCheck()
        {
            updateCheckTimer = new(TimeSpan.FromDays(1).TotalMilliseconds)
            {
                AutoReset = true,
                Enabled = true
            };
            updateCheckTimer.Elapsed += UpdateCheckTimer_Elapsed;
        }

        private static void UpdateCheckTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            CheckForNewVersion();
        }

        public static async void CheckForNewVersion()
        {
            await CheckForNewVersionAsync();
        }

        /// <summary>
        /// Awaitable form of the check, so a manual "check for updates" action
        /// can tell the user that the installed version is already current.
        /// Errors are still reported through <see cref="Error"/>.
        /// </summary>
        public static async Task<UpdateCheckResult> CheckForNewVersionAsync()
        {
            try
            {
                var release = await api.GetLatestRelease();
                if (!Version.TryParse(release.tag_name, out var remoteVersion))
                {
                    throw new Exception($"Could not read a version number from the release tag \"{release.tag_name}\".");
                }
                Version localVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? throw new Exception("Could not retrieve version from assembly");

                if (localVersion.CompareTo(remoteVersion) != -1)
                {
                    return UpdateCheckResult.UpToDate;
                }

                NewVersion?.Invoke(null, new()
                {
                    Version = remoteVersion,
                    InstalledVersion = localVersion,
                    Uri = new(release.html_url),
                    Asset = SelectArchiveAsset(release),
                    Notes = await CollectNotesAsync(release, remoteVersion, localVersion),
                });
                return UpdateCheckResult.UpdateAvailable;
            }
            catch (ApiException ex)
            {
                Error?.Invoke(null, new(new Exception($"Invalid GitHub API response code: {ex.StatusCode}.", ex), "checking for new version"));
                return UpdateCheckResult.Failed;
            }
            catch (Exception ex)
            {
                Error?.Invoke(null, new(new Exception("GitHub API error.", ex), "checking for new version"));
                return UpdateCheckResult.Failed;
            }
        }

        /// <summary>
        /// The notes of every published release newer than the installed one,
        /// newest first, so someone who skipped versions sees all changes.
        /// Falls back to the latest release alone when the list is unavailable.
        /// </summary>
        private static async Task<List<ReleaseNote>> CollectNotesAsync(ReleaseData latest, Version latestVersion, Version localVersion)
        {
            var notes = new List<ReleaseNote>();
            try
            {
                foreach (var release in await api.GetReleases())
                {
                    if (release.draft || release.prerelease || !Version.TryParse(release.tag_name, out var version))
                    {
                        continue;
                    }
                    if (version.CompareTo(localVersion) > 0 && version.CompareTo(latestVersion) <= 0)
                    {
                        notes.Add(new ReleaseNote(version, release.body ?? "", release.published_at));
                    }
                }
            }
            catch
            {
                // The notes are a convenience; the update itself does not need them.
                notes.Clear();
            }

            if (notes.Count == 0)
            {
                notes.Add(new ReleaseNote(latestVersion, latest.body ?? "", latest.published_at));
            }
            return notes.OrderByDescending(note => note.Version).ToList();
        }

        /// <summary>
        /// Finds the release archive the in-app updater can install. Releases
        /// built before the updater existed, or with an unexpected set of
        /// attachments, simply return null and fall back to the download page.
        /// </summary>
        private static ReleaseAsset? SelectArchiveAsset(ReleaseData release)
        {
            var archives = release.assets
                .Where(asset => !string.IsNullOrEmpty(asset.browser_download_url)
                    && asset.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                .ToList();

            return archives.FirstOrDefault(asset => string.Equals(
                    asset.name,
                    "TarkovMonitor.zip",
                    StringComparison.OrdinalIgnoreCase))
                ?? archives.FirstOrDefault();
        }

        public class ReleaseData
        {
            public string tag_name { get; set; } = "";
            public string html_url { get; set; } = "";
            public string? body { get; set; }
            public DateTime? published_at { get; set; }
            public bool draft { get; set; }
            public bool prerelease { get; set; }
            public List<ReleaseAsset> assets { get; set; } = new();
        }

        public class ReleaseAsset
        {
            public string name { get; set; } = "";
            public string browser_download_url { get; set; } = "";
            public long size { get; set; }
        }
    }

    public sealed record ReleaseNote(Version Version, string Body, DateTime? PublishedUtc);

    public class NewVersionEventArgs : EventArgs
    {
        public Version Version { get; set; }
        public Version? InstalledVersion { get; set; }
        public Uri Uri { get; set; }
        public List<ReleaseNote> Notes { get; set; } = new();

        /// <summary>
        /// The archive the update can be installed from, or null when the
        /// release has none and the user has to download it themselves.
        /// </summary>
        internal UpdateCheck.ReleaseAsset? Asset { get; set; }
    }
}

// to release a new version:
// bump AssemblyVersion in TarkovMonitor.csproj to match the tag you are about to
// create; the in-app update check compares the release tag against that value.
// Tag the commit, push the tag, and attach the publish folder as TarkovMonitor.zip
// (TarkovMonitor.exe at the archive root) to the GitHub release. The release
// description is shown as the changelog in the update popup.
