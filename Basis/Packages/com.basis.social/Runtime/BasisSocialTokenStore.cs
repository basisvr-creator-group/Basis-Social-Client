namespace Basis.Social
{
    public interface IBasisSocialTokenStore
    {
        string AccessToken { get; }
        string RefreshToken { get; }
        void Save(string accessToken, string refreshToken);
        void Clear();
    }

    /// <summary>
    /// Deliberately keeps credentials in process memory only. A platform keychain-backed
    /// implementation can be supplied later; tokens must never be stored in PlayerPrefs,
    /// world metadata, logs or serialized scene content.
    /// </summary>
    public sealed class BasisSocialMemoryTokenStore : IBasisSocialTokenStore
    {
        public string AccessToken { get; private set; }
        public string RefreshToken { get; private set; }

        public void Save(string accessToken, string refreshToken)
        {
            AccessToken = accessToken;
            RefreshToken = refreshToken;
        }

        public void Clear()
        {
            AccessToken = null;
            RefreshToken = null;
        }
    }
}
