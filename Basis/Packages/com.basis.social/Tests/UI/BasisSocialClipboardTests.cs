using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Basis.Social.UI;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Social.Tests
{
    public sealed class BasisSocialClipboardTests
    {
        private sealed class Transport : IBasisSocialHttpTransport
        {
            public Task<BasisSocialHttpResponse> SendAsync(BasisSocialHttpRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new BasisSocialHttpResponse { StatusCode = 201, Body =
                    "{\"deviceCode\":\"never-copy-device-secret\",\"userCode\":\"ABCD-EFGH\",\"verificationUri\":\"https://beeba.example/connect\",\"expiresIn\":600,\"interval\":5}" });
        }

        [Test]
        public async Task CopyCopiesOnlyTheLiveDisplayedCodeAndPreservesClipboardAfterCancellation()
        {
            string previous = GUIUtility.systemCopyBuffer;
            using var connection = new BasisSocialConnectionController(new BasisSocialApiClient(new Transport()));
            var provider = new BasisSocialProvider();
            typeof(BasisSocialProvider).GetField("connection", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(provider, connection);
            var copy = typeof(BasisSocialProvider).GetMethod("CopyApprovalCode", BindingFlags.Instance | BindingFlags.NonPublic);
            Task attempt = null;
            try
            {
                GUIUtility.systemCopyBuffer = "prior clipboard";
                copy.Invoke(provider, null);
                Assert.That(GUIUtility.systemCopyBuffer, Is.EqualTo("prior clipboard"));
                attempt = connection.StartAsync();
                Assert.That(connection.State, Is.EqualTo(BasisSocialConnectionState.AwaitingApproval));
                copy.Invoke(provider, null);
                Assert.That(GUIUtility.systemCopyBuffer, Is.EqualTo("ABCD-EFGH"));
                connection.Cancel();
                await attempt;
                GUIUtility.systemCopyBuffer = "new clipboard";
                copy.Invoke(provider, null);
                Assert.That(GUIUtility.systemCopyBuffer, Is.EqualTo("new clipboard"));
            }
            finally
            {
                connection.Cancel();
                if (attempt != null) await attempt;
                GUIUtility.systemCopyBuffer = previous;
            }
        }
    }
}
