// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;

namespace Bootrix.Core.Net;

/// <summary>A failure worth another attempt: dropped connection, timeout, 429/5xx.</summary>
internal sealed class TransientDownloadException(string message, TimeSpan? retryAfter = null, Exception? inner = null)
    : Exception(message, inner)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>The server refuses the address with 403/410 or sends us somewhere else mid-download: time to renew the link.</summary>
internal sealed class LinkExpiredException(HttpStatusCode status)
    : Exception($"HTTP {(int)status}")
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>The file on the server is no longer the one the already downloaded parts belong to.</summary>
internal sealed class ContentChangedException(string message) : Exception(message);
