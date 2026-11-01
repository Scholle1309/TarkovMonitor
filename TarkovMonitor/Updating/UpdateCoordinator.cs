using MudBlazor;

namespace TarkovMonitor.Updating
{
    /// <summary>
    /// The state of one available update, shared by the update popup and the
    /// message in the message list so both show the same progress.
    /// </summary>
    public sealed class UpdateOffer
    {
        internal UpdateOffer(NewVersionEventArgs release, bool canInstall)
        {
            Release = release;
            CanInstall = canInstall;
        }

        public NewVersionEventArgs Release { get; }
        public Version Version => Release.Version;
        public bool CanInstall { get; }
        public bool Busy { get; internal set; }
        public string? Status { get; internal set; }
        public int? Percent { get; internal set; }

        public event EventHandler? Changed;

        internal void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Turns a new-version notice into an actionable popup and message: the
    /// user sees the changelog and can install the update without leaving the
    /// application. Nothing is downloaded until the user asks for it.
    /// </summary>
    public sealed class UpdateCoordinator
    {
        private const int CleanupAttempts = 6;
        private static readonly TimeSpan CleanupRetryDelay = TimeSpan.FromSeconds(5);

        private readonly MessageLog messageLog;
        private readonly Action requestExit;
        private readonly Func<string, string> text;
        private readonly Func<bool> isInRaid;
        private readonly object gate = new();
        private Version? announcedVersion;
        private bool updateStarted;
        private UpdateOffer? pendingPopup;
        private MonitorMessage? currentMessage;
        private MonitorMessageButton? installButton;
        private CancellationTokenSource? cancellation;

        internal UpdateCoordinator(MessageLog messageLog, Action requestExit, Func<string, string> text, Func<bool> isInRaid)
        {
            this.messageLog = messageLog;
            this.requestExit = requestExit;
            this.text = text;
            this.isInRaid = isInRaid;
        }

        /// <summary>The update that is currently on offer, if any.</summary>
        internal UpdateOffer? Current { get; private set; }

        /// <summary>
        /// Raised when the update popup should be shown. The popup host takes
        /// the offer with <see cref="TakePendingPopup"/>, which also covers an
        /// offer announced before the host was rendered.
        /// </summary>
        internal event EventHandler? PopupRequested;

        internal UpdateOffer? TakePendingPopup()
        {
            lock (gate)
            {
                var offer = pendingPopup;
                pendingPopup = null;
                return offer;
            }
        }

        private string Text(string key, params object[] args) => string.Format(text(key), args);

        /// <summary>
        /// Discards staging folders from previous updates, which hold a full
        /// copy of the application and are worth reclaiming promptly. The
        /// launcher that applied the last update exits moments after starting
        /// this process, so the first attempt usually cannot delete it.
        /// </summary>
        internal void CleanStaleStaging()
        {
            _ = Task.Run(async () =>
            {
                for (var attempt = 0; attempt < CleanupAttempts; attempt++)
                {
                    lock (gate)
                    {
                        // A download started in the meantime owns a staging
                        // folder that must not be deleted underneath it.
                        if (updateStarted)
                        {
                            return;
                        }
                    }

                    UpdateInstaller.CleanStaleStaging();
                    if (!UpdateInstaller.HasStaging)
                    {
                        return;
                    }
                    await Task.Delay(CleanupRetryDelay);
                }
            });
        }

        internal void Announce(NewVersionEventArgs newVersion)
        {
            UpdateOffer offer;
            lock (gate)
            {
                // The check runs again every day, and a user who is not ready
                // to restart should not collect a message or popup per day.
                if (announcedVersion == newVersion.Version)
                {
                    return;
                }
                announcedVersion = newVersion.Version;
                offer = new UpdateOffer(newVersion, newVersion.Asset != null && UpdateInstaller.IsSupportedInstallation);
                Current = offer;
            }

            var message = new MonitorMessage(
                offer.CanInstall
                    ? Text("UpdateAvailableInstall", newVersion.Version)
                    : Text("UpdateAvailableDownload", newVersion.Version),
                "update",
                newVersion.Uri.ToString(),
                Text("UpdateViewRelease"));

            message.Buttons.Add(new MonitorMessageButton(Text("UpdateWhatsNew"), () => ShowPopup(), Icons.Material.Filled.Article));
            if (offer.CanInstall)
            {
                installButton = CreateInstallButton(newVersion);
                message.Buttons.Add(installButton);
            }
            currentMessage = message;
            messageLog.AddMessage(message);

            // A popup in the middle of a raid would be in the way; the message
            // stays and the popup can be opened from it later.
            if (!isInRaid())
            {
                ShowPopup();
            }
        }

        /// <summary>Asks the popup host to show the current offer.</summary>
        internal void ShowPopup()
        {
            lock (gate)
            {
                if (Current == null)
                {
                    return;
                }
                pendingPopup = Current;
            }
            PopupRequested?.Invoke(this, EventArgs.Empty);
        }

        private MonitorMessageButton CreateInstallButton(NewVersionEventArgs newVersion)
        {
            return new MonitorMessageButton(Text("UpdateInstallRestart"), icon: Icons.Material.Filled.Update)
            {
                Color = MudBlazor.Color.Info,
                OnClick = () => Install(),
                Confirm = new MonitorMessageButtonConfirm(
                    Text("UpdateConfirmTitle"),
                    Text("UpdateConfirmText", newVersion.Version),
                    Text("UpdateInstall"),
                    Text("UpdateNotNow")),
            };
        }

        /// <summary>
        /// Downloads the offered release and restarts into it. Safe to call
        /// from the popup and the message alike; a second call is ignored.
        /// </summary>
        internal void Install()
        {
            var offer = Current;
            var asset = offer?.Release.Asset;
            if (offer == null || asset == null || !offer.CanInstall)
            {
                return;
            }

            CancellationTokenSource tokenSource;
            lock (gate)
            {
                if (updateStarted)
                {
                    return;
                }
                updateStarted = true;
                tokenSource = new CancellationTokenSource();
                cancellation = tokenSource;
            }

            if (currentMessage != null && installButton != null)
            {
                currentMessage.Buttons.Remove(installButton);
            }
            offer.Busy = true;
            SetStatus(offer, Text("UpdatePreparing", offer.Version), null);

            _ = Task.Run(() => RunUpdateAsync(offer, asset, tokenSource));
        }

        internal void CancelInstall()
        {
            lock (gate)
            {
                cancellation?.Cancel();
            }
        }

        private async Task RunUpdateAsync(UpdateOffer offer, UpdateCheck.ReleaseAsset asset, CancellationTokenSource tokenSource)
        {
            var startedUtc = DateTime.UtcNow;
            try
            {
                var progress = new Progress<UpdateProgress>(update => SetStatus(
                    offer,
                    DescribeProgress(offer.Version, update),
                    update.Stage == UpdateStage.Downloading ? update.Percent : null));

                var staged = await UpdateInstaller.StageAsync(offer.Version, asset, progress, tokenSource.Token);

                SetStatus(offer, Text("UpdateReady", offer.Version), 100);
                UpdateInstaller.StartApply(staged);
                requestExit();
            }
            catch (OperationCanceledException)
            {
                Reset(offer, Text("UpdateCancelled"));
            }
            catch (Exception exception)
            {
                Reset(offer, Text("UpdateFailed", offer.Version));
                messageLog.AddException(
                    "Installing the update failed; copy diagnostics for details.",
                    "TM-UPDATE-003",
                    "InstallUpdate",
                    exception,
                    "UpdateCheck",
                    "Install",
                    asset.browser_download_url,
                    DiagnosticsService.ElapsedMilliseconds(startedUtc));
            }
            finally
            {
                lock (gate)
                {
                    cancellation = null;
                }
                tokenSource.Dispose();
            }
        }

        private void Reset(UpdateOffer offer, string reason)
        {
            lock (gate)
            {
                updateStarted = false;
            }
            if (currentMessage != null && installButton != null && !currentMessage.Buttons.GetSnapshot().Contains(installButton))
            {
                currentMessage.Buttons.Add(installButton);
            }
            offer.Busy = false;
            SetStatus(offer, Text("UpdateStillAvailable", reason, offer.Version), null);
        }

        private void SetStatus(UpdateOffer offer, string status, int? percent)
        {
            offer.Status = status;
            offer.Percent = percent;
            offer.NotifyChanged();
            if (currentMessage != null)
            {
                currentMessage.Message = status;
                currentMessage.NotifyChanged();
            }
        }

        private string DescribeProgress(Version version, UpdateProgress update)
        {
            return update.Stage switch
            {
                UpdateStage.Downloading when update.Percent is int percent
                    => Text("UpdateDownloadingPercent", version, percent, DescribeSize(update.BytesReceived), DescribeSize(update.TotalBytes)),
                UpdateStage.Downloading
                    => Text("UpdateDownloading", version, DescribeSize(update.BytesReceived)),
                UpdateStage.Verifying => Text("UpdateVerifying", version),
                UpdateStage.Extracting => Text("UpdateExtracting", version),
                _ => Text("UpdateReadyToInstall", version),
            };
        }

        private static string DescribeSize(long bytes)
        {
            return $"{bytes / 1024d / 1024d:0.#} MB";
        }
    }
}
