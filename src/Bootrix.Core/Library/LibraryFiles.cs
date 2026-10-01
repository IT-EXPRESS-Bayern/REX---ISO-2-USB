// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;

namespace Bootrix.Core.Library;

/// <summary>
/// The naming rules of a library folder. Every name that is ever opened, written or deleted is built here from a
/// validated SHA-256 and a sanitized extension; no path or file name from metadata or from the caller is used as is.
/// That is what keeps a hostile share or a hand-edited metadata file from steering the library out of its folder.
/// </summary>
internal static partial class LibraryFiles
{
    public const string MetadataExtension = ".json";
    public const string TemporaryExtension = ".tmp";
    public const string DefaultImageExtension = "bin";
    private const string IncomingPrefix = "incoming-";

    private const int HashLength = 64;
    private const int MaxExtensionLength = 8;
    private const int MaxTextLength = 256;
    private const int MaxSourceLength = 512;

    [GeneratedRegex(@"\A[0-9a-f]{64}\z")]
    private static partial Regex HashPattern();

    [GeneratedRegex(@"\A[0-9a-f]{64}\.[a-z0-9]{1,8}\z")]
    private static partial Regex ImageNamePattern();

    public static bool IsSha256(string? text) => text is not null && HashPattern().IsMatch(text);

    public static string MetadataName(string sha256) => sha256 + MetadataExtension;

    public static bool IsMetadataName(string name) =>
        name.EndsWith(MetadataExtension, StringComparison.Ordinal) && IsSha256(name[..^MetadataExtension.Length]);

    /// <summary>"&lt;sha256&gt;.&lt;ext&gt;", with the extension taken from the file name the image came with.</summary>
    public static string ImageName(string sha256, string? originalFileName) =>
        $"{sha256}.{Extension(originalFileName)}";

    /// <summary>True for the one name an image with this hash may have in a library folder.</summary>
    public static bool IsImageNameFor(string? name, string sha256) =>
        name is not null && IsImageName(name) && name.StartsWith(sha256 + ".", StringComparison.Ordinal);

    /// <summary>A stored image: "&lt;sha256&gt;.&lt;ext&gt;", where the extension is never the one of metadata or temporary files.</summary>
    public static bool IsImageName(string name) =>
        ImageNamePattern().IsMatch(name) && !IsReservedExtension(name[(HashLength + 1)..]);

    /// <summary>A leftover of an interrupted write. Only names that start like a library file are ever treated as such.</summary>
    public static bool IsTemporaryName(string name) =>
        name.EndsWith(TemporaryExtension, StringComparison.Ordinal)
        && (name.StartsWith(IncomingPrefix, StringComparison.Ordinal) || (name.Length > HashLength && IsSha256(name[..HashLength])));

    public static string TemporaryName(string name) => $"{name}.{Guid.NewGuid():N}{TemporaryExtension}";

    /// <summary>The name of a copy whose hash is not known yet, which is why it cannot start with it.</summary>
    public static string IncomingName() => $"{IncomingPrefix}{Guid.NewGuid():N}{TemporaryExtension}";

    /// <summary>
    /// Lower-case letters and digits only, at most eight, otherwise "bin". Both separators are looked at because a
    /// name that came from another platform may use the one this platform does not treat as a separator. "json" and
    /// "tmp" are replaced as well, since an image must never take the name of its own metadata or of a temporary file.
    /// </summary>
    public static string Extension(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return DefaultImageExtension;
        }

        var name = fileName[(fileName.AsSpan().LastIndexOfAny('/', '\\') + 1)..];
        var dot = name.LastIndexOf('.');
        if (dot < 0)
        {
            return DefaultImageExtension;
        }

        var extension = name[(dot + 1)..].ToLowerInvariant();
        var plain = extension.Length is > 0 and <= MaxExtensionLength && extension.All(char.IsAsciiLetterOrDigit);
        return plain && !IsReservedExtension(extension) ? extension : DefaultImageExtension;
    }

    /// <summary>Metadata can come from a share anyone may write to; what is displayed later is limited and stripped of control characters.</summary>
    public static string? CleanText(string? text) =>
        StripControl(text) is { } cleaned ? Truncate(cleaned, MaxTextLength) : null;

    public static string? CleanSource(string? source)
    {
        if (StripControl(source) is not { } text)
        {
            return null;
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            // No query string, fragment or credentials: download links often carry tokens, and the metadata may be shared.
            var bare = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty, Query = string.Empty, Fragment = string.Empty };
            return Truncate(bare.Uri.AbsoluteUri, MaxSourceLength);
        }

        return Truncate(text, MaxSourceLength);
    }

    private static string? StripControl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var cleaned = string.Concat(text.Where(c => !char.IsControl(c))).Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static bool IsReservedExtension(string extension) => extension is "json" or "tmp";

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];
}
