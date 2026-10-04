using System;
using System.Globalization;

/// <summary>Validates one exact BEE byte range and fences ranges to the first representation.</summary>
public static class BasisBeeRangePolicy
{
    public static bool IsStrongETag(string value) => !string.IsNullOrEmpty(value) && value.Length >= 2 &&
        value[0] == '"' && value[value.Length - 1] == '"' && value.IndexOf('\r') < 0 && value.IndexOf('\n') < 0;

    public static bool Validate(string contentRange, long start, long end, string etag, string expectedETag,
        long expectedTotal, out long total, out string error)
    {
        total = 0;
        error = "The server did not return the requested byte range.";
        if (start < 0 || end < start || string.IsNullOrEmpty(contentRange) || !contentRange.StartsWith("bytes ", StringComparison.OrdinalIgnoreCase)) return false;
        string value = contentRange.Substring(6);
        int dash = value.IndexOf('-');
        int slash = value.IndexOf('/');
        if (dash <= 0 || slash <= dash + 1 || slash == value.Length - 1) return false;
        if (!long.TryParse(value.Substring(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out long actualStart) ||
            !long.TryParse(value.Substring(dash + 1, slash - dash - 1), NumberStyles.None, CultureInfo.InvariantCulture, out long actualEnd) ||
            !long.TryParse(value.Substring(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out total)) return false;
        if (actualStart != start || actualEnd != end || total <= end) return false;
        if ((expectedTotal >= 0 && total != expectedTotal) ||
            (!string.IsNullOrEmpty(expectedETag) && !string.Equals(etag, expectedETag, StringComparison.Ordinal)))
        {
            error = "The remote package changed during download. Resolve its current version and retry.";
            return false;
        }
        error = null;
        return true;
    }
}
