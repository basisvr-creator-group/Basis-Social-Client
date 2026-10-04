using Basis.BasisUI;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using System;
using System.Globalization;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Basis.Social.UI
{
    public enum BasisSocialJoinState { Idle, Preparing, Connecting, Connected, Failed, Cancelled }

    /// <summary>Native instance lifecycle. The HTTP ticket alone never changes the connected UI state.</summary>
    public static class BasisSocialJoinController
    {
        public static event Action Changed;
        public static BasisSocialJoinState State { get; private set; }
        public static Exception Error { get; private set; }
        public static string CurrentInstanceId { get; private set; }
        public static string PresenceVisibility { get; set; } = "nobody";
        public static string PresenceStatus { get; set; } = "online";
        public static bool ShowExactInstance { get; set; }
        private static CancellationTokenSource operation;
        private static TaskCompletionSource<bool> connected;
        private static BasisSocialApiClient owner;
        private static string actor;
        private static bool initialized;
        private static long generation;
        private static BasisSocialWorldLoader worldLoader;
        private static IDisposable resourceGate;
        private static readonly SemaphoreSlim flow = new SemaphoreSlim(1, 1);
        public static bool UsingBuiltInWorld => worldLoader?.IsBuiltIn == true;

        private static void Initialize()
        {
            if (initialized) return;
            initialized = true;
            BasisNetworkManagement.OnIstanceCreated += BindNetworkEvents;
            BindNetworkEvents();
            BasisNetworkConnection.OnRebootComplete += OnReboot;
            BasisSocialRuntime.ClientChanged += OnClientChanged;
            Application.quitting += Cancel;
        }

        private static void BindNetworkEvents()
        {
            BasisNetworkPlayer.OnLocalPlayerJoined -= OnJoined;
            BasisNetworkPlayer.OnLocalPlayerJoined += OnJoined;
            BasisNetworkPlayer.OnLocalPlayerLeft -= OnLeft;
            BasisNetworkPlayer.OnLocalPlayerLeft += OnLeft;
        }

        public static async Task JoinAsync(BasisSocialInstance instance, CancellationToken cancellationToken = default)
        {
            if (!flow.Wait(0)) return;
            try { await JoinCoreAsync(instance, cancellationToken); }
            finally { flow.Release(); }
        }

        private static async Task JoinCoreAsync(BasisSocialInstance instance, CancellationToken cancellationToken)
        {
            Initialize();
            if (State == BasisSocialJoinState.Preparing || State == BasisSocialJoinState.Connecting || State == BasisSocialJoinState.Connected) return;
            var client = BasisSocialRuntime.Client;
            if (client?.HasSession != true || client.CurrentUser == null)
            {
                SetState(BasisSocialJoinState.Failed, new BasisSocialApiException(401, "unauthorized", "Sign in to join."));
                return;
            }
            if (instance == null || !Guid.TryParse(instance.id, out _)) throw new ArgumentException("An instance is required.", nameof(instance));
            operation?.Dispose();
            operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            operation.CancelAfter(TimeSpan.FromSeconds(90));
            CancellationToken token = operation.Token;
            long epoch = ++generation;
            if (owner != null) owner.SessionChanged -= OnSessionChanged;
            owner = client;
            actor = client.CurrentUser.actorId;
            owner.SessionChanged += OnSessionChanged;
            CurrentInstanceId = instance.id;
            SetState(BasisSocialJoinState.Preparing);
            resourceGate = BasisNetworkResourceGate.BeginVerifiedSession(() =>
                SetState(State, new BasisSocialApiException(409, "unverified_resource", "The server requested an unverified resource. It was not loaded.")));
            try
            {
                if (worldLoader != null || BasisNetworkConnection.LocalPlayerPeer != null && !BasisNetworkConnection.LocalPlayerIsConnected) await DisconnectAsync(releaseGate: false);
                var live = await client.GetInstanceAsync(instance.id, token);
                if (live.status != "active") throw new BasisSocialApiException(409, "instance_not_active", "The instance is not active.");
                ServerDirectoryEntry entry = await ParseTargetAsync(live, token);
                string did = BasisPlayerIdentityRegistry.ResolveActive()?.Uuid;
                string name = client.CurrentUser.EffectiveProfile?.displayName;
                if (string.IsNullOrWhiteSpace(name)) name = client.CurrentUser.username;
                worldLoader = new BasisSocialWorldLoader();
                var handshake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                connected = handshake;
                using var registration = token.Register(() => handshake.TrySetCanceled());
                await BasisConnectionService.ConnectAsync(entry, name, socialTicketFactory: async ct =>
                {
                    token.ThrowIfCancellationRequested();
                    var issued = await client.IssueJoinTicketAsync(live.id, did, PresenceVisibility, ShowExactInstance, ct);
                    // The endpoint can change between review and issuance; never silently dial a replacement.
                    if (issued.instance.launchUrl != live.launchUrl) throw new BasisSocialApiException(409, "instance_changed", "The instance changed. Please review it again.");
                    SetState(BasisSocialJoinState.Connecting);
                    return issued.ticket;
                }, cancellationToken: token, prepareWorld: ct => worldLoader.LoadAsync(client, live.worldId, ct));
                await handshake.Task;
                token.ThrowIfCancellationRequested();
                await client.SetPresenceAsync(PresenceStatus, PresenceVisibility, token);
                if (epoch == generation) SetState(BasisSocialJoinState.Connected, (Error as BasisSocialApiException)?.ErrorCode == "unverified_resource" ? Error : null);
            }
            catch (OperationCanceledException)
            {
                if (epoch == generation)
                {
                    await DisconnectAsync();
                    CurrentInstanceId = null;
                    SetState(BasisSocialJoinState.Cancelled);
                }
            }
            catch (Exception ex)
            {
                if (epoch == generation)
                {
                    await DisconnectAsync();
                    CurrentInstanceId = null;
                    SetState(BasisSocialJoinState.Failed, ex is BasisSocialApiException ? ex : new BasisSocialApiException(0, "join_failed", "The instance could not be joined."));
                }
            }
            finally
            {
                if (epoch == generation)
                {
                    operation?.Dispose();
                    operation = null;
                    connected = null;
                }
            }
        }

        internal static async Task<ServerDirectoryEntry> ParseTargetAsync(BasisSocialInstance instance, CancellationToken token)
        {
            if (!Uri.TryCreate(instance.launchUrl, UriKind.Absolute, out Uri uri) || uri.Scheme != "basisdemo" ||
                string.IsNullOrEmpty(uri.Host) || uri.Port < 1 || uri.Port > 65535 || uri.AbsolutePath != "/" && uri.AbsolutePath != "" ||
                !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
                throw new BasisSocialApiException(409, "invalid_instance_endpoint", "The instance has no supported game endpoint.");
            bool local = IPAddress.TryParse(uri.Host, out IPAddress address) && IPAddress.IsLoopback(address);
            await BasisSocialPreviewImage.ValidateAddressAsync((local ? "http" : "https") + "://" + uri.Authority + "/", token);
            string pinnedAddress = uri.Host;
            if (!IPAddress.TryParse(uri.Host, out _))
            {
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(uri.Host);
                token.ThrowIfCancellationRequested();
                if (addresses.Length == 0) throw new BasisSocialApiException(409, "invalid_instance_endpoint", "The game host is unavailable.");
                foreach (IPAddress candidate in addresses)
                    if (BasisUrlSecurity.IsBlockedHost(candidate.ToString(), false, out _))
                        throw new BasisSocialApiException(409, "invalid_instance_endpoint", "The game host is unavailable.");
                pinnedAddress = addresses[0].ToString();
            }
            var target = new ConnectionTarget(BasisNetworkStackRegistry.DefaultId, pinnedAddress);
            target.Set(ConnectionTarget.Keys.Address, pinnedAddress);
            target.Set(ConnectionTarget.Keys.Port, uri.Port.ToString(CultureInfo.InvariantCulture));
            return new ServerDirectoryEntry { Id = "social:" + instance.id, DisplayName = instance.name, Target = target, HasPassword = false, Password = "", CanEdit = false, CanRemove = false };
        }

        public static void Cancel() => operation?.Cancel();

        public static async Task LeaveAsync(CancellationToken cancellationToken = default)
        {
            Initialize();
            Cancel();
            await flow.WaitAsync(cancellationToken);
            try
            {
                ++generation;
                connected?.TrySetCanceled();
                SetState(BasisSocialJoinState.Cancelled);
                await DisconnectAsync();
                CurrentInstanceId = null;
                SetState(BasisSocialJoinState.Idle);
            }
            finally { flow.Release(); }
        }

        private static async Task DisconnectAsync(bool releaseGate = true)
        {
            if (BasisNetworkConnection.LocalPlayerPeer == null)
            {
                if (worldLoader != null) { await worldLoader.UnloadAsync(); worldLoader = null; }
                if (releaseGate) { resourceGate?.Dispose(); resourceGate = null; }
                return;
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Task reboot = BasisNetworkConnection.WaitForRebootCompleteAsync(timeout.Token);
            await BasisNetworkLifeCycle.Destroy();
            try { await reboot; } catch (OperationCanceledException) { }
            if (!BasisNetworkManagement.IsInitialized) BasisNetworkLifeCycle.Initialize();
            if (worldLoader != null) { await worldLoader.UnloadAsync(); worldLoader = null; }
            if (releaseGate) { resourceGate?.Dispose(); resourceGate = null; }
        }

        private static void OnJoined(BasisNetworkPlayer networkPlayer, BasisLocalPlayer player)
        {
            if (State == BasisSocialJoinState.Connecting && owner == BasisSocialRuntime.Client && owner?.CurrentUser?.actorId == actor) connected?.TrySetResult(true);
        }

        private static void OnLeft(BasisNetworkPlayer networkPlayer, BasisLocalPlayer player)
        {
            if (State == BasisSocialJoinState.Connecting) connected?.TrySetException(new BasisSocialApiException(409, "join_failed", "The server declined the connection."));
            else if (State == BasisSocialJoinState.Connected)
            {
                CurrentInstanceId = null;
                SetState(BasisSocialJoinState.Failed, new BasisSocialApiException(409, "instance_disconnected", "The connection ended. Please join again."));
            }
        }

        private static void OnReboot()
        {
            if (State == BasisSocialJoinState.Connecting) connected?.TrySetException(new BasisSocialApiException(409, "join_failed", "The connection ended before admission."));
            else if (State == BasisSocialJoinState.Failed) _ = CleanupAfterRebootAsync();
        }
        private static async Task CleanupAfterRebootAsync()
        {
            await flow.WaitAsync();
            try
            {
                if (State == BasisSocialJoinState.Failed && BasisNetworkConnection.LocalPlayerPeer == null) await DisconnectAsync();
            }
            catch (Exception) { Debug.LogWarning("Social world cleanup failed."); }
            finally { flow.Release(); }
        }
        private static void OnClientChanged(BasisSocialApiClient client) { if (owner != BasisSocialRuntime.Client) _ = LeaveAsync(); }
        private static void OnSessionChanged(BasisSocialUser user) { if (user?.actorId != actor) _ = LeaveAsync(); }
        private static void SetState(BasisSocialJoinState state, Exception error = null) { State = state; Error = error; Changed?.Invoke(); }
    }
}
