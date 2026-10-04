using System;
using System.Threading;
using System.Threading.Tasks;
using Basis.Scripts.BasisSdk.Players;

namespace Basis.Social.UI
{
    /// <summary>Adapts a live public catalog manifest to the existing platform-aware Basis avatar loader.</summary>
    public static class BasisSocialAvatarLoader
    {
        public static async Task WearAsync(BasisSocialPackageLoad manifest, Action<float> progress, CancellationToken cancellationToken)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            cancellationToken.ThrowIfCancellationRequested();
            // Decryption reports from a worker. Capture the caller's Unity context before awaiting.
            using var progressDispatch = new BasisSocialProgressDispatcher(SynchronizationContext.Current, progress, cancellationToken);
            // The generic Basis loader permits loopback in the Editor. Social content obeys the
            // explicit connection opt-in even there, including public names resolving to loopback.
            try { await BasisSocialPreviewImage.ValidateAddressAsync(manifest.DownloadUrl, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { throw new BasisSocialApiException(409, "asset_unavailable", "The package address is not permitted."); }
            var player = BasisLocalPlayer.Instance;
            if (player == null)
                throw new BasisSocialApiException(409, "player_not_ready", "The local player is not ready.");
            var bundle = new BasisLoadableBundle
            {
                UnlockPassword = manifest.UnlockPassword,
                BasisRemoteBundleEncrypted = new BasisRemoteEncyptedBundle
                {
                    RemoteBeeFileLocation = manifest.DownloadUrl,
                    RemoteVersionTag = manifest.RemoteVersionTag
                }
            };
            void OnProgress(string id, float value, string description) => progressDispatch.Report(value);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            using var developmentPermit = BasisSocialRuntime.AllowLoopbackHttp &&
                Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var address) && address.IsLoopback
                ? BasisDevelopmentCatalogPermit.Acquire(manifest.DownloadUrl) : null;
#endif
            player.ProgressReportAvatarLoad.OnProgressReport += OnProgress;
            try
            {
                // Catalog access is checked for each selection. Do not save a URL as an offline
                // authorization or add it to the legacy library, which has no catalog access check.
                await player.CreateAvatar(0, bundle, cancellationToken, rememberAvatar: false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReferenceEquals(player.AvatarMetaData, bundle))
                    throw new BasisSocialApiException(409, "avatar_load_failed", "The avatar could not be loaded on this platform.");
            }
            finally
            {
                player.ProgressReportAvatarLoad.OnProgressReport -= OnProgress;
            }
        }
    }
}
