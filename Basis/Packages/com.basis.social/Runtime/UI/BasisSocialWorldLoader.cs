using Basis.Scripts.Drivers;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.SceneManagement;

namespace Basis.Social.UI
{
    /// <summary>Loads the owner's live primary pin, with the normal Basis scene loader and owned-scene rollback.</summary>
    internal sealed class BasisSocialWorldLoader
    {
        private Scene previousScene;
        private Scene loadedScene;
        private bool ownsScene;
        public bool IsBuiltIn { get; private set; }

        public async Task LoadAsync(BasisSocialApiClient client, string worldId, CancellationToken token)
        {
            var assets = await client.ListWorldAssetsAsync(worldId, cancellationToken: token);
            BasisSocialWorldAsset primary = null;
            foreach (var asset in assets)
            {
                if (asset.role != "primary") continue;
                if (primary != null) throw new BasisSocialApiException(409, "asset_unavailable", "The world has multiple primary packages.");
                primary = asset;
            }
            if (primary == null)
            {
                string builtIn = ResolveBuiltInScenePath(Environment.GetCommandLineArgs());
                if (builtIn != null)
                {
                    int index = SceneUtility.GetBuildIndexByScenePath(builtIn);
                    if (index <= 0 || SceneManager.GetSceneByBuildIndex(index).isLoaded)
                        throw new BasisSocialApiException(409, "asset_unavailable", "The configured built-in world is not available in this player.");
                    await LoadOwnedSceneAsync(() => LoadPlayerSceneAsync(index), token);
                    IsBuiltIn = true;
                    return;
                }
                var holder = BundledContentHolder.Instance;
                if (holder == null || !holder.UseSceneProvidedHere ||
                    string.IsNullOrWhiteSpace(holder.DefaultScene?.BasisRemoteBundleEncrypted?.RemoteBeeFileLocation))
                    throw new BasisSocialApiException(409, "asset_unavailable", "No primary package or built-in world is configured.");
                await LoadOwnedSceneAsync(() => holder.UseAddressablesToLoadScene
                    ? BasisSceneLoad.LoadSceneAddressables(holder.DefaultScene.BasisRemoteBundleEncrypted.RemoteBeeFileLocation)
                    : BasisSceneLoad.LoadSceneAssetBundle(holder.DefaultScene, cancellationToken: token), token);
                IsBuiltIn = true;
                return;
            }
            var manifest = await client.PrepareWorldAssetLoadAsync(worldId, primary.asset.id, cancellationToken: token);
            if (manifest.ContentType != "world") throw new BasisSocialApiException(409, "asset_unavailable", "The primary package is not a world.");
            await BasisSocialPreviewImage.ValidateAddressAsync(manifest.DownloadUrl, token);
            var bundle = new BasisLoadableBundle
            {
                UnlockPassword = manifest.UnlockPassword,
                BasisRemoteBundleEncrypted = new BasisRemoteEncyptedBundle
                {
                    RemoteBeeFileLocation = manifest.DownloadUrl,
                    RemoteVersionTag = manifest.RemoteVersionTag
                }
            };
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            using var developmentPermit = BasisSocialRuntime.AllowLoopbackHttp &&
                Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var address) && address.IsLoopback
                ? BasisDevelopmentCatalogPermit.Acquire(manifest.DownloadUrl) : null;
#endif
            await LoadOwnedSceneAsync(() => BasisSceneLoad.LoadSceneAssetBundle(bundle, cancellationToken: token), token);
        }

        internal static string ResolveBuiltInScenePath(string[] arguments)
        {
            const string prefix = "--basis-social-built-in-scene=";
            foreach (string argument in arguments)
            {
                if (!argument.StartsWith(prefix, StringComparison.Ordinal)) continue;
                string path = argument.Substring(prefix.Length);
                if (!(path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal)) ||
                    !path.EndsWith(".unity", StringComparison.Ordinal) || path.Contains("..") || path.Contains('\\'))
                    throw new BasisSocialApiException(409, "asset_unavailable", "Use a packaged scene path for the built-in world.");
                return path;
            }
            return null;
        }

        private static async Task<Scene> LoadPlayerSceneAsync(int index)
        {
            BasisSceneLoad.SetIfPlayerShouldSpawnOnSceneLoad(true);
            var loading = SceneManager.LoadSceneAsync(index, LoadSceneMode.Additive);
            if (loading == null) throw new BasisSocialApiException(409, "asset_unavailable", "The built-in world could not be loaded.");
            while (!loading.isDone) await Task.Yield();
            return SceneManager.GetSceneByBuildIndex(index);
        }

        private async Task LoadOwnedSceneAsync(Func<Task<Scene>> load, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            previousScene = SceneManager.GetActiveScene();
            var previousHandles = new HashSet<Scene>();
            for (int i = 0; i < SceneManager.sceneCount; i++) previousHandles.Add(SceneManager.GetSceneAt(i));
            try
            {
                loadedScene = await load();
                ownsScene = loadedScene.IsValid() && loadedScene.isLoaded && !previousHandles.Contains(loadedScene);
                token.ThrowIfCancellationRequested();
                if (!loadedScene.IsValid() || !loadedScene.isLoaded) throw new BasisSocialApiException(409, "asset_unavailable", "The world package could not be loaded.");
                if (ownsScene) SceneManager.SetActiveScene(loadedScene);
            }
            catch
            {
                await UnloadAsync();
                throw;
            }
        }

        public async Task UnloadAsync()
        {
            if (!ownsScene) return;
            ownsScene = false;
            if (SceneManager.GetActiveScene() == loadedScene && previousScene.IsValid() && previousScene.isLoaded)
                SceneManager.SetActiveScene(previousScene);
            if (!loadedScene.IsValid() || !loadedScene.isLoaded) return;
            var operation = SceneManager.UnloadSceneAsync(loadedScene);
            if (operation != null) while (!operation.isDone) await Task.Yield();
        }
    }
}
