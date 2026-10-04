// Real external UDP/DID admission check. Credentials and tickets are never logged.
using Basis.Contrib.Auth.DecentralizedIds;
using Basis.Contrib.Auth.DecentralizedIds.Newtypes;
using Basis.Contrib.Crypto;
using Basis.Network.Core;
using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using static Basis.Network.Core.Serializable.SerializableBasis;
using static Basis.Network.Core.Compression.BasisAvatarBitPacking;
using static SerializableBasis;

if (args.Length != 2 || !ushort.TryParse(args[1], out ushort port))
    throw new ArgumentException("Supply the audited game server hostname and UDP port.");
byte[] keyBytes = new byte[Ed25519.PrivkeySize];
RandomNumberGenerator.Fill(keyBytes);
var key = new PrivKey(keyBytes);
var publicKey = Ed25519.ConvertPrivkeyToPubkey(key) ?? throw new InvalidOperationException("Invalid key.");
string did = DidKeyResolver.EncodePubkeyAsDid(publicKey).V;
Console.WriteLine("PLAYTEST_KEYS " + JsonSerializer.Serialize(new { did }));
string input;
while ((input = Console.ReadLine()) != null)
{
    using var json = JsonDocument.Parse(input);
    string ticket = json.RootElement.GetProperty("ticket").GetString();
    var ready = new ReadyMessage
    {
        playerMetaDataMessage = new ClientMetaDataMessage { playerUUID = did, playerDisplayName = "Desktop admission check", playerPlatform = "Mac protocol check" },
        clientAvatarChangeMessage = new ClientAvatarChangeMessage { byteArray = new byte[] { 1 }, LocalAvatarIndex = 0, loadMode = 0 },
        localAvatarSyncMessage = new LocalAvatarSyncMessage { DataQualityLevel = (byte)BitQuality.Low, array = new byte[ConvertToSize(BitQuality.Low)] }
    };
    var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var client = new NetworkClient();
    try
    {
        client.StartClient(args[0], port, ready, Array.Empty<byte>(),
            new Configuration { UseAuthIdentity = true, UseAuth = false, EnableStatistics = false },
            manualMode: true, socialJoinTicket: ticket);
        client.listener.PeerDisconnectedEvent += (_, _) => result.TrySetResult(false);
        client.listener.NetworkReceiveEvent += (peer, reader, channel, _) =>
        {
            try
            {
                if (channel == BasisNetworkCommons.AuthIdentityChannel)
                {
                    if (!new BytesMessage().Deserialize(reader, out byte[] nonce)) { result.TrySetResult(false); return; }
                    if (!Ed25519.Sign(key, new Payload(nonce), out Signature signature)) throw new InvalidOperationException("Cannot sign challenge.");
                    var writer = new NetDataWriter();
                    new BytesMessage().Serialize(writer, signature.V);
                    new BytesMessage().Serialize(writer, Encoding.ASCII.GetBytes("N/A"));
                    peer.Send(writer, BasisNetworkCommons.AuthIdentityChannel, DeliveryMethod.ReliableOrdered);
                }
                else if (channel == BasisNetworkCommons.metaDataChannel) result.TrySetResult(true);
            }
            finally { reader.Recycle(); }
        };
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!result.Task.IsCompleted && started.Elapsed < TimeSpan.FromSeconds(15))
        {
            client.Update(15); client.Poll(); await Task.Delay(15);
        }
        bool accepted = result.Task.IsCompleted && await result.Task;
        bool timedOut = !result.Task.IsCompleted;
        client.Disconnect();
        Console.WriteLine("PLAYTEST_RESULT " + JsonSerializer.Serialize(new { accepted, timedOut }));
    }
    finally { client.Shutdown(); }
}
