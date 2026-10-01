// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Tests.Net.Support;

/// <summary>
/// An in-process HTTP/1.1 server (Kestrel on a loopback port) that serves one file with hand-written range, If-Range and
/// ETag handling so that tests can inject faults per request and inspect exactly what the downloader asked for.
/// </summary>
/// <remarks>
/// Routes: <c>/file</c> serves the content, <c>/signed?token=..</c> does the same but answers 403 for tokens that are
/// not in <see cref="ValidTokens"/>, and every key of <see cref="Redirects"/> answers 302 to its value.
/// </remarks>
internal sealed class FileServer : IAsyncDisposable
{
    private const int ChunkSize = 16 * 1024;

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<RequestRecord> _requests = new();
    private readonly ConcurrentDictionary<string, int> _hits = new();
    private int _counter;
    private int _concurrent;
    private int _peak;
    private long _bytesSent;

    private FileServer(WebApplication app)
    {
        _app = app;
    }

    public Uri BaseAddress { get; private set; } = new("http://localhost/");

    public Uri FileUri => new(BaseAddress, "/file");

    public byte[] Content { get; set; } = [];

    public string? ETag { get; set; } = "\"v1\"";

    public DateTimeOffset LastModified { get; set; } = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    /// <summary>When false the server ignores Range headers, like a proxy in front of a file server often does.</summary>
    public bool SupportsRanges { get; set; } = true;

    /// <summary>Advertise <c>Accept-Ranges: bytes</c> but answer every request with 200 and the full body.</summary>
    public bool LieAboutRanges { get; set; }

    public bool SendLastModified { get; set; } = true;

    /// <summary>Leave out Content-Length on full responses, so the body is sent chunked.</summary>
    public bool OmitContentLength { get; set; }

    public ISet<string> ValidTokens { get; } = new HashSet<string>(StringComparer.Ordinal);

    public IDictionary<string, string> Redirects { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Called for each content request; returns what to do to the response.</summary>
    public Func<RequestInfo, Fault>? Script { get; set; }

    public IReadOnlyCollection<RequestRecord> Requests => _requests;

    /// <summary>Highest number of content requests that were being served at the same moment.</summary>
    public int PeakConcurrency => Volatile.Read(ref _peak);

    public long BytesSent => Interlocked.Read(ref _bytesSent);

    /// <summary>Number of content requests started so far; the index of the most recent one.</summary>
    public int RequestCount => Volatile.Read(ref _counter);

    public int Hits(string path) => _hits.TryGetValue(path, out var count) ? count : 0;

    public static async Task<FileServer> StartAsync(byte[] content)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1);
            options.Limits.MinResponseDataRate = null;
        });

        var app = builder.Build();
        var server = new FileServer(app) { Content = content };

        app.Run(server.DispatchAsync);
        await app.StartAsync();

        server.BaseAddress = new Uri(app.Urls.First());
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task DispatchAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";
        _hits.AddOrUpdate(path, 1, (_, count) => count + 1);

        if (Redirects.TryGetValue(path, out var target))
        {
            context.Response.StatusCode = StatusCodes.Status302Found;
            context.Response.Headers.Location = target;
            return;
        }

        if (path == "/signed" && !ValidTokens.Contains(context.Request.Query["token"].ToString()))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (path is "/file" or "/signed")
        {
            await ServeAsync(context, path);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    private async Task ServeAsync(HttpContext context, string path)
    {
        var (rangeStart, rangeEnd) = ParseRange(context.Request.Headers.Range.ToString());

        // The one-byte probe is over before any segment starts; counting it would make the server's own
        // handler teardown look like a second connection.
        var counted = !(rangeStart == 0 && rangeEnd == 0);
        if (counted)
        {
            var current = Interlocked.Increment(ref _concurrent);
            int peak;
            while (current > (peak = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, current, peak) != peak)
            {
            }
        }

        try
        {
            var ifRange = context.Request.Headers.IfRange.ToString();
            var info = new RequestInfo(Interlocked.Increment(ref _counter), path, rangeStart, rangeEnd, ifRange.Length > 0);
            var fault = Script?.Invoke(info) ?? Fault.None;

            // Read after the script ran, so that a script which swaps the content affects the request that triggered it.
            var content = Content;
            var etag = ETag;

            var response = context.Response;
            if (fault.StatusCode is { } code)
            {
                response.StatusCode = code;
                if (fault.RetryAfterSeconds is { } seconds)
                {
                    response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                }

                Record(info, code, 0);
                return;
            }

            var ranged = SupportsRanges && rangeStart is not null && IfRangeAllows(ifRange, etag);
            long start = 0;
            long end = content.Length - 1;

            if (ranged)
            {
                start = rangeStart!.Value;
                end = Math.Min(rangeEnd ?? content.Length - 1, content.Length - 1);
                if (start >= content.Length)
                {
                    response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                    response.Headers.ContentRange = $"bytes */{content.Length}";
                    Record(info, 416, 0);
                    return;
                }

                response.StatusCode = StatusCodes.Status206PartialContent;
                response.Headers.ContentRange = $"bytes {start}-{end}/{content.Length}";
            }

            if (SupportsRanges || LieAboutRanges)
            {
                response.Headers.AcceptRanges = "bytes";
            }

            if (etag is not null)
            {
                response.Headers.ETag = etag;
            }

            if (SendLastModified)
            {
                response.Headers.LastModified = LastModified.ToString("R", CultureInfo.InvariantCulture);
            }

            var length = end - start + 1;
            if (ranged || !OmitContentLength)
            {
                response.ContentLength = length;
            }

            await response.StartAsync();
            var sent = await WriteBodyAsync(context, content, start, length, fault);
            Record(info, response.StatusCode, sent);
        }
        finally
        {
            if (counted)
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }
    }

    private async Task<long> WriteBodyAsync(HttpContext context, byte[] content, long start, long length, Fault fault)
    {
        long sent = 0;
        var limit = fault.AbortAfterBytes ?? length;

        while (sent < length)
        {
            if (sent >= limit)
            {
                context.Abort();
                break;
            }

            var count = (int)Math.Min(ChunkSize, Math.Min(length - sent, limit - sent));
            var chunk = content.AsMemory((int)(start + sent), count);

            if (fault.Corrupt)
            {
                var copy = chunk.ToArray();
                copy[0] ^= 0x01;
                chunk = copy;
            }

            try
            {
                await context.Response.Body.WriteAsync(chunk, context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                break;
            }

            sent += count;
            Interlocked.Add(ref _bytesSent, count);

            // No pause after the last chunk: the client already has everything and may start the next request,
            // which the server must not still count as part of this one.
            if (fault.BytesPerSecond is { } rate && sent < length)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds((double)count / rate), context.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        return sent;
    }

    private void Record(RequestInfo info, int status, long sent) =>
        _requests.Enqueue(new RequestRecord(info.Index, info.Path, info.RangeStart, info.RangeEnd, info.HasIfRange, status, sent));

    private bool IfRangeAllows(string ifRange, string? etag)
    {
        if (ifRange.Length == 0)
        {
            return true;
        }

        return ifRange == etag || ifRange == LastModified.ToString("R", CultureInfo.InvariantCulture);
    }

    private static (long? Start, long? End) ParseRange(string header)
    {
        if (!header.StartsWith("bytes=", StringComparison.Ordinal))
        {
            return (null, null);
        }

        var parts = header["bytes=".Length..].Split('-');
        if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var start))
        {
            return (null, null);
        }

        long? end = long.TryParse(parts.ElementAtOrDefault(1), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        return (start, end);
    }
}
