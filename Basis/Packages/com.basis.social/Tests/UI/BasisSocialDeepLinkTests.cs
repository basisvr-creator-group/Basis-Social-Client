using Basis.Social.UI;
using NUnit.Framework;

namespace Basis.Social.Tests
{
    public sealed class BasisSocialDeepLinkTests
    {
        [Test]
        public void CanonicalLinkCarriesOnlyReviewTarget()
        {
            Assert.That(BasisSocialDeepLink.TryParse("basis://join/88888888-8888-4888-8888-888888888888", out string id), Is.True);
            Assert.That(id, Is.EqualTo("88888888-8888-4888-8888-888888888888"));
        }

        [TestCase("basis://join/88888888-8888-4888-8888-888888888888?service=https://attacker.example")]
        [TestCase("basis://join/88888888-8888-4888-8888-888888888888#token")]
        [TestCase("basis://secret@join/88888888-8888-4888-8888-888888888888")]
        [TestCase("basis://join:8080/88888888-8888-4888-8888-888888888888")]
        [TestCase("basis://join/00000000-0000-0000-0000-000000000000")]
        [TestCase("basis://join/not-a-guid")]
        [TestCase("basisdemo://127.0.0.1:4296")]
        public void LinkCannotSupplyEndpointCredentialOrAction(string link)
        {
            Assert.That(BasisSocialDeepLink.TryParse(link, out _), Is.False);
        }
    }
}
