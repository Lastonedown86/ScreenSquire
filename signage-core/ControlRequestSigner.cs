using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PiSignage.Signage;

public static class ControlRequestSigner
{
    const string ControllerHeader = "X-PiSignage-Controller";
    const string CounterHeader = "X-PiSignage-Counter";
    const string EntityHashHeader = "X-PiSignage-Entity-SHA256";
    const string SignatureHeader = "X-PiSignage-Signature";

    public static void Sign(
        HttpRequestMessage request,
        string controllerId,
        byte[] secret,
        long counter,
        string entityHash)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(controllerId);
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityHash);

        var requestUri = request.RequestUri
            ?? throw new ArgumentException("The request must have a URI.", nameof(request));
        var pathAndQuery = requestUri.IsAbsoluteUri
            ? requestUri.PathAndQuery
            : WithoutFragment(requestUri.OriginalString);
        var counterText = counter.ToString(CultureInfo.InvariantCulture);
        var canonical = string.Join(
            "\n",
            controllerId,
            counterText,
            request.Method.Method.ToUpperInvariant(),
            pathAndQuery,
            entityHash);
        var signature = Convert.ToHexString(
            HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();

        SetHeader(request, ControllerHeader, controllerId);
        SetHeader(request, CounterHeader, counterText);
        SetHeader(request, EntityHashHeader, entityHash);
        SetHeader(request, SignatureHeader, signature);
    }

    static void SetHeader(HttpRequestMessage request, string name, string value)
    {
        request.Headers.Remove(name);
        if (!request.Headers.TryAddWithoutValidation(name, value))
            throw new InvalidOperationException($"Could not set request header '{name}'.");
    }

    static string WithoutFragment(string requestTarget)
    {
        var fragmentIndex = requestTarget.IndexOf('#');
        return fragmentIndex < 0 ? requestTarget : requestTarget[..fragmentIndex];
    }
}
