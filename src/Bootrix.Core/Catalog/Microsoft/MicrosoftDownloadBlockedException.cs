// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;

namespace Bootrix.Core.Catalog.Microsoft;

/// <summary>
/// Microsoft's download service turned the request down. It does that for traffic it takes for automated or
/// anonymised, and it is not something retrying helps with; the user can still get the file in a browser.
/// </summary>
public sealed class MicrosoftDownloadBlockedException : BootrixException
{
    /// <summary>Message code Microsoft shows on its page, or the HTTP status when no such code was given.</summary>
    public const string SentinelCode = "715-123130";

    public MicrosoftDownloadBlockedException(string messageCode, Uri manualUrl, string detail)
        : base(ErrorCode.DownloadBlocked, detail)
    {
        MessageCode = messageCode;
        ManualUrl = manualUrl;
        Arguments = [messageCode];
    }

    public string MessageCode { get; }

    /// <summary>The download page to open in a browser; the user picks the ISO from disk afterwards.</summary>
    public Uri ManualUrl { get; }
}
