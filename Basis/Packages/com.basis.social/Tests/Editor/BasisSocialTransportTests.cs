using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Basis.Social.Tests
{
    public sealed class BasisSocialTransportTests
    {
        [TestCase("https://elsewhere.example/api")]
        [TestCase("//elsewhere.example/api")]
        [TestCase("/\\elsewhere.example/api")]
        [TestCase("/api#fragment")]
        public void RejectsPathsOutsideConfiguredOrigin(string path)
        {
            var transport = new BasisSocialUnityWebRequestTransport("http://127.0.0.1:1", allowLoopbackHttp: true);
            Assert.ThrowsAsync<ArgumentException>(async () => await transport.SendAsync(new BasisSocialHttpRequest { Path = path }));
        }

        [UnityTest]
        public IEnumerator RealTransportAcceptsOriginRelativeCatalogPath()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Task<string> received = Serve(listener);
                var transport = new BasisSocialUnityWebRequestTransport("http://127.0.0.1:" + port, allowLoopbackHttp: true);
                Task<BasisSocialHttpResponse> request = transport.SendAsync(new BasisSocialHttpRequest { Path = "/api/v1/assets/catalogs" }, cancellation.Token);
                while (!request.IsCompleted) yield return null;
                var response = request.GetAwaiter().GetResult();
                Assert.That(response.StatusCode, Is.EqualTo(200));
                Assert.That(response.Body, Is.EqualTo("[]"));
                while (!received.IsCompleted) yield return null;
                Assert.That(received.GetAwaiter().GetResult(), Is.EqualTo("GET /api/v1/assets/catalogs HTTP/1.1"));
            }
            finally { listener.Stop(); }
        }

        private static async Task<string> Serve(TcpListener listener)
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync();
            using NetworkStream stream = peer.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
            string first = await reader.ReadLineAsync();
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
            byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n[]");
            await stream.WriteAsync(response, 0, response.Length);
            return first;
        }
    }
}
