using Basis.Network.Core;
using BasisNetworkServer.BasisNetworkingReductionSystem;

// The real Basis server library owns networking and ticket admission. This host only
// supplies bounded playtest configuration; it cannot start an unmanaged public server.
string? origin = Environment.GetEnvironmentVariable("BASIS_WORLD_SOCIAL_URL");
string? instance = Environment.GetEnvironmentVariable("BASIS_WORLD_INSTANCE_ID");
string? credential = Environment.GetEnvironmentVariable("BASIS_WORLD_CREDENTIAL");
if (!Uri.TryCreate(origin, UriKind.Absolute, out var service) || service.Scheme != "https" ||
    service.AbsolutePath != "/" || service.UserInfo.Length != 0 || service.Query.Length != 0 || service.Fragment.Length != 0 ||
    !Guid.TryParse(instance, out var instanceId) || instanceId == Guid.Empty ||
    string.IsNullOrEmpty(credential) || !credential.StartsWith("bvr_ws_", StringComparison.Ordinal) || credential.Length > 256)
    throw new InvalidOperationException("A prebound Social instance and scoped server credential are required.");

BNL.LogOutput += Console.WriteLine;
BNL.LogWarningOutput += Console.WriteLine;
BNL.LogErrorOutput += Console.Error.WriteLine;
var configuration = new Configuration
{
    IPv4Address = "0.0.0.0", IPv6Address = "::", OverrideAutoDiscoveryOfIpv = true,
    SetPort = 4297, PeerLimit = 8, UseAuth = false, UseAuthIdentity = true, Password = "",
    EnableConsole = false, EnableStatistics = false, HasFileSupport = false,
    ServerName = "Basis Social Desktop Playtest", ServerMotd = "Mac + Windows social integration test",
    EnableComputeOffload = false, HowManyDuplicateAuthCanExist = 1
};
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) => { args.Cancel = true; stop.Cancel(); };
System.Runtime.InteropServices.PosixSignalRegistration? termination = null;
if (!OperatingSystem.IsWindows())
    termination = System.Runtime.InteropServices.PosixSignalRegistration.Create(
        System.Runtime.InteropServices.PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); });
NetworkServer.StartServer(configuration);
try
{
    int previous = -1;
    while (!stop.IsCancellationRequested)
    {
        int count = NetworkServer.AuthenticatedPeers.Count;
        if (count != previous) { Console.WriteLine("PLAYTEST_AUTHENTICATED_PEERS=" + count); previous = count; }
        await Task.Delay(500, stop.Token);
    }
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
finally
{
    NetworkServer.StopServer();
    BasisServerReductionSystemEvents.Shutdown();
    termination?.Dispose();
}
