using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using AutoVpn.Domain;

namespace AutoVpn.Infrastructure.Fetch;

public sealed record FetchResult(
    int StatusCode,
    bool NotModified,
    string? Body,
    string? Etag,
    string? ContentHash,
    string? ReasonCode,
    int? RetryAfterSeconds = null);

public sealed record ApprovedFetchOrigin(string Host, int Port, string PathPrefix)
{
    public bool Matches(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        if (uri.Port != Port || !string.Equals(uri.IdnHost, Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = uri.AbsolutePath;
        if (PathPrefix.EndsWith('/'))
        {
            return path.StartsWith(PathPrefix, StringComparison.Ordinal);
        }

        return string.Equals(path, PathPrefix, StringComparison.Ordinal) ||
               path.StartsWith(PathPrefix + "/", StringComparison.Ordinal);
    }
}

public sealed class PolicyHttpFetcher : IDisposable
{
    private readonly HttpMessageHandler _handler;
    private readonly ApprovedFetchOrigin[] _origins;
    private readonly bool _ownsHandler;
    private bool _disposed;

    public PolicyHttpFetcher(HttpMessageHandler handler, IReadOnlySet<string> allowedHosts)
        : this(handler, HostsToOrigins(allowedHosts), ownsHandler: false)
    {
    }

    public PolicyHttpFetcher(HttpMessageHandler handler, IReadOnlyCollection<ApprovedFetchOrigin> origins, bool ownsHandler = false)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (handler is SocketsHttpHandler sockets && (sockets.AllowAutoRedirect || sockets.UseProxy))
        {
            throw new InvalidOperationException("Production HTTP policy rejects implicit redirects and ambient proxies.");
        }

        _handler = handler;
        _origins = origins.ToArray();
        _ownsHandler = ownsHandler;
    }

    public static SocketsHttpHandler CreateProductionHandler()
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
    }

    public static PolicyHttpFetcher Create(IReadOnlyCollection<ApprovedFetchOrigin> origins)
    {
        return new PolicyHttpFetcher(CreateProductionHandler(), origins, ownsHandler: true);
    }

    public async Task<FetchResult> GetAsync(
        Uri url,
        string? etag,
        int maxBytes,
        CancellationToken cancellationToken,
        TimeSpan? attemptTimeout = null,
        int maxRetries = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsAllowed(url))
        {
            return new FetchResult(0, false, null, null, null, ReasonCodes.OffRegistryRedirect);
        }

        var timeout = attemptTimeout ?? TimeSpan.FromSeconds(ProductLimits.FetchAttemptTimeoutSeconds);
        if (timeout <= TimeSpan.Zero)
        {
            return new FetchResult(0, false, null, null, null, ReasonCodes.FetchTimeout);
        }

        var attempts = Math.Max(0, maxRetries) + 1;
        FetchResult last = new(0, false, null, null, null, ReasonCodes.FetchFailed);
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            last = await AttemptAsync(url, etag, maxBytes, timeout, cancellationToken).ConfigureAwait(false);
            if (last.ReasonCode is null
                || last.NotModified
                || last.ReasonCode is ReasonCodes.HtmlContent
                    or ReasonCodes.SizeLimit
                    or ReasonCodes.OffRegistryRedirect
                    or ReasonCodes.Canceled
                    or ReasonCodes.RateLimited)
            {
                return last;
            }
        }

        return last;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsHandler)
        {
            _handler.Dispose();
        }
    }

    private async Task<FetchResult> AttemptAsync(Uri url, string? etag, int maxBytes, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(timeout);
        using var client = new HttpClient(_handler, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var current = url;
        try
        {
            for (var hop = 0; hop <= ProductLimits.MaxRedirects; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Version = HttpVersion.Version11;
                request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
                request.Headers.TryAddWithoutValidation("User-Agent", "AutoVPN/0.1");
                request.Headers.TryAddWithoutValidation("Accept", "text/plain, application/yaml, application/json, */*");
                if (!string.IsNullOrWhiteSpace(etag))
                {
                    request.Headers.TryAddWithoutValidation("If-None-Match", etag);
                }

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token).ConfigureAwait(false);
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
                    etag = null;
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    return new FetchResult(304, true, null, etag, null, ReasonCodes.NotModified);
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    int? retryAfter = null;
                    if (response.Headers.RetryAfter?.Delta is TimeSpan delta)
                    {
                        retryAfter = (int)Math.Clamp(Math.Ceiling(delta.TotalSeconds), 0, 3_600);
                    }

                    return new FetchResult(429, false, null, null, null, ReasonCodes.RateLimited, retryAfter);
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

                await using var stream = await response.Content.ReadAsStreamAsync(attempt.Token).ConfigureAwait(false);
                var buffer = new byte[maxBytes + 1];
                var read = 0;
                while (read < buffer.Length)
                {
                    var count = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), attempt.Token).ConfigureAwait(false);
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
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new FetchResult(0, false, null, null, null, ReasonCodes.Canceled);
        }
        catch (OperationCanceledException)
        {
            return new FetchResult(0, false, null, null, null, ReasonCodes.FetchTimeout);
        }
        catch (HttpRequestException)
        {
            return new FetchResult(0, false, null, null, null, ReasonCodes.FetchFailed);
        }

        return new FetchResult(0, false, null, null, null, ReasonCodes.FetchFailed);
    }

    private bool IsAllowed(Uri uri)
    {
        return _origins.Any(origin => origin.Matches(uri));
    }

    private static bool IsRedirect(HttpStatusCode status)
    {
        return status is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
    }

    private static ApprovedFetchOrigin[] HostsToOrigins(IReadOnlySet<string> allowedHosts)
    {
        return allowedHosts
            .Select(host => new ApprovedFetchOrigin(host, 443, "/"))
            .ToArray();
    }
}
