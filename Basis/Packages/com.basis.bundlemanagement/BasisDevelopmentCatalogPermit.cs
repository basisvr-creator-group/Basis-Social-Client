#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Net;

/// <summary>
/// Development-only, temporary permission for one immutable local catalog package.
/// No host wildcard, inheritance through redirects, or persistent permission is supported.
/// </summary>
public sealed class BasisDevelopmentCatalogPermit : IDisposable
{
    private static readonly Dictionary<string, int> Active = new Dictionary<string, int>(StringComparer.Ordinal);
    private readonly string url;
    private bool disposed;
    private BasisDevelopmentCatalogPermit(string value) { url = value; }

    public static IDisposable Acquire(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp ||
            !IPAddress.TryParse(uri.Host, out var address) || !IPAddress.IsLoopback(address) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("An immutable loopback catalog URL is required.", nameof(value));
        string[] parts = uri.AbsolutePath.Split('/');
        if (parts.Length != 6 || parts[1] != "api" || parts[2] != "v1" || parts[3] != "content" ||
            !Guid.TryParseExact(parts[4], "D", out _) || parts[5] != "download" ||
            !uri.Query.StartsWith("?version=", StringComparison.Ordinal) || !Guid.TryParseExact(uri.Query.Substring(9), "D", out _))
            throw new ArgumentException("An immutable loopback catalog URL is required.", nameof(value));
        string key = uri.AbsoluteUri;
        lock (Active) { Active.TryGetValue(key, out int count); Active[key] = count + 1; }
        return new BasisDevelopmentCatalogPermit(key);
    }

    public static bool Allows(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        lock (Active) return Active.ContainsKey(uri.AbsoluteUri);
    }

    public void Dispose()
    {
        lock (Active)
        {
            if (disposed) return;
            disposed = true;
            if (Active[url] == 1) Active.Remove(url); else Active[url]--;
        }
    }
}
#endif
