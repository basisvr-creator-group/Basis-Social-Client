using System;
using System.Security.Cryptography;
using System.Text;

namespace Basis.Social
{
    /// <summary>Per-attempt S256 proof. Keep this object and deviceCode in memory; never log, serialize, or place the verifier in a browser URL.</summary>
    public sealed class BasisSocialDeviceProof
    {
        public string CodeVerifier { get; }
        public string CodeChallenge { get; }

        private BasisSocialDeviceProof(string verifier)
        {
            CodeVerifier = verifier;
            using var sha = SHA256.Create();
            CodeChallenge = Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
        }

        public static BasisSocialDeviceProof Create()
        {
            var bytes = new byte[32];
            using var random = RandomNumberGenerator.Create();
            random.GetBytes(bytes);
            return new BasisSocialDeviceProof(Base64Url(bytes));
        }

        private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
