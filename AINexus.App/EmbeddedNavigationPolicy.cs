using System.Net;

namespace AINexus.App;

internal static class EmbeddedNavigationPolicy
{
    public static Uri ValidateEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!IsLoopbackHttp(endpoint))
            throw new ArgumentException("Choose an HTTP or HTTPS address on localhost or a loopback IP, without credentials.", nameof(endpoint));
        return endpoint;
    }

    public static bool IsAllowed(Uri candidate, Uri origin) => IsLoopbackHttp(candidate) && IsLoopbackHttp(origin)
        && candidate.Scheme.Equals(origin.Scheme, StringComparison.OrdinalIgnoreCase)
        && candidate.IdnHost.Equals(origin.IdnHost, StringComparison.OrdinalIgnoreCase)
        && candidate.Port == origin.Port;

    public static bool IsResourceAllowed(Uri candidate, Uri origin)
    {
        if (!candidate.IsAbsoluteUri) return false;
        if (IsAllowed(candidate, origin)) return true;
        // Data URLs contain inline bytes and make no network request. Blob URLs
        // must have been created by the selected origin; opaque blobs are denied.
        if (candidate.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase)) return true;
        return candidate.Scheme.Equals("blob", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(candidate.OriginalString[5..], UriKind.Absolute, out var blobOrigin)
            && IsAllowed(blobOrigin, origin);
    }

    private static bool IsLoopbackHttp(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0) return false;
        if (uri.IdnHost.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
    }
}
