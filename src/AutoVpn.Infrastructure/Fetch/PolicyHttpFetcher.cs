using System.Net;
using System.Security.Cryptography;
using AutoVpn.Domain;

namespace AutoVpn.Infrastructure.Fetch;

public sealed record FetchResult(
    int StatusCode,
    bool NotModified,
    string? Body,
    string? Etag,
    string? ContentHash,
    string? ReasonCode);

public sealed class PolicyHttpFetcher
{
    private readonly HttpMessageHandler _handler;
    private readonly IReadOnlySet<string> _allowedHosts;

    public PolicyHttpFetcher(HttpMessageHandler handler, IReadOnlySet<string> allowedHosts)
    {
        _handler = handler;
        _allowedHosts = new HashSet<string>(allowedHosts, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<FetchResult> GetAsync(Uri url, string? etag, int maxBytes, CancellationToken cancellationToken)
    {
        if (!IsAllowed(url))
        {
            return new FetchResult(0, false, null, null, null, ReasonCodes.OffRegistryRedirect);
        }

        using var client = new HttpClient(_handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(ProductLimits.FetchAttemptTimeoutSeconds),
        };
        var current = url;
        for (var hop = 0; hop <= ProductLimits.MaxRedirects; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.TryAddWithoutValidation("User-Agent", "AutoVPN/0.1");
            request.Headers.TryAddWithoutValidation("Accept", "text/plain, application/yaml, application/json, */*");
            if (!string.IsNullOrWhiteSpace(etag))
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            }

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (IsRedirect(response.StatusCode))
            {
                var location = response.Headers.Location;
                if (location is null)
                {
                    return new FetchResult((int)response.StatusCode, false, null, null, null, ReasonCodes.FetchFailed);
                }

                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (hop == ProductLimits.MaxRedirects || !IsAllowed(next))
                {
                    return new FetchResult((int)response.StatusCode, false, null, null, null, ReasonCodes.OffRegistryRedirect);
                }

                current = next;
                continue;
            }

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return new FetchResult(304, true, null, etag, null, ReasonCodes.NotModified);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new FetchResult((int)response.StatusCode, false, null, null, null, ReasonCodes.FetchFailed);
            }

            var media = response.Content.Headers.ContentType?.MediaType;
            if (media is not null && media.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                return new FetchResult((int)response.StatusCode, false, null, null, null, ReasonCodes.HtmlContent);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[maxBytes + 1];
            var read = 0;
            while (read < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                read += count;
            }

            if (read > maxBytes)
            {
                return new FetchResult((int)response.StatusCode, false, null, null, null, ReasonCodes.SizeLimit);
            }

            var body = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
            var head = body.TrimStart();
            if (head.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase) || head.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
            {
                return new FetchResult((int)response.StatusCode, false, null, null, null, ReasonCodes.HtmlContent);
            }

            var hash = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read))).ToLowerInvariant();
            var responseTag = response.Headers.ETag?.Tag;
            return new FetchResult((int)response.StatusCode, false, body, responseTag, hash, null);
        }

        return new FetchResult(0, false, null, null, null, ReasonCodes.FetchFailed);
    }

    private bool IsAllowed(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        return _allowedHosts.Contains(uri.IdnHost);
    }

    private static bool IsRedirect(HttpStatusCode status)
    {
        return status is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
    }
}
