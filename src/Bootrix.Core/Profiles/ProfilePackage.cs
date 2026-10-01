// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Bootrix.Core.Errors;
using Bootrix.Core.Json;

namespace Bootrix.Core.Profiles;

public sealed record PackageFile(string Path, string Sha256, long Size);

public sealed record PackageManifest(int SchemaVersion, IReadOnlyList<PackageFile> Files);

/// <summary>
/// The shareable form of a profile: a ZIP with profile.json, optional assets (logo, driver
/// lists, scripts) and a manifest holding the SHA-256 of every file.
/// </summary>
public static class ProfilePackage
{
    public const string Extension = ".bootrixprofile";

    private const string ProfileEntry = "profile.json";
    private const string ManifestEntry = "manifest.json";
    private const long MaxTotalBytes = 512L * 1024 * 1024;

    public static void Write(Stream output, ProfileFile profile, IReadOnlyDictionary<string, byte[]>? assets = null)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var files = new List<PackageFile>();

        var profileBytes = JsonSerializer.SerializeToUtf8Bytes(profile, CoreJson.Options);
        AddEntry(zip, ProfileEntry, profileBytes, files);

        foreach (var (name, content) in assets ?? new Dictionary<string, byte[]>())
        {
            var entryName = "assets/" + NormalizeAssetName(name);
            AddEntry(zip, entryName, content, files);
        }

        var manifest = new PackageManifest(1, files);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, CoreJson.Options);
        var manifestEntry = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
        using var manifestStream = manifestEntry.Open();
        manifestStream.Write(manifestBytes);
    }

    public static OpenedPackage Read(Stream input)
    {
        using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);

        var manifestEntry = zip.GetEntry(ManifestEntry) ?? throw Corrupt("manifest.json is missing");
        var manifest = JsonSerializer.Deserialize<PackageManifest>(ReadEntry(manifestEntry, 1024 * 1024), CoreJson.Options)
            ?? throw Corrupt("manifest.json is empty");

        long total = 0;
        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            ValidateEntryName(file.Path);
            var entry = zip.GetEntry(file.Path) ?? throw Corrupt($"{file.Path} is missing");
            total += file.Size;
            if (total > MaxTotalBytes || file.Size != entry.Length)
            {
                throw Corrupt("unexpected size of " + file.Path);
            }

            var bytes = ReadEntry(entry, file.Size);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw Corrupt("checksum of " + file.Path + " does not match");
            }

            contents[file.Path] = bytes;
        }

        // Anything in the archive that the manifest does not list could be smuggled in after signing.
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName != ManifestEntry && !contents.ContainsKey(entry.FullName))
            {
                throw Corrupt("unlisted file " + entry.FullName);
            }
        }

        if (!contents.TryGetValue(ProfileEntry, out var profileBytes))
        {
            throw Corrupt("profile.json is missing");
        }

        var profile = JsonSerializer.Deserialize<ProfileFile>(profileBytes, CoreJson.Options)
            ?? throw Corrupt("profile.json is empty");

        var assets = contents
            .Where(kv => kv.Key.StartsWith("assets/", StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key["assets/".Length..], kv => kv.Value);

        return new OpenedPackage(profile, assets);
    }

    private static void AddEntry(ZipArchive zip, string name, byte[] content, List<PackageFile> files)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using (var stream = entry.Open())
        {
            stream.Write(content);
        }

        files.Add(new PackageFile(name, Convert.ToHexStringLower(SHA256.HashData(content)), content.Length));
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry, long limit)
    {
        if (entry.Length > limit)
        {
            throw Corrupt(entry.FullName + " is too large");
        }

        using var stream = entry.Open();
        using var buffer = new MemoryStream((int)entry.Length);
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string NormalizeAssetName(string name)
    {
        var normalized = name.Replace('\\', '/').TrimStart('/');
        ValidateEntryName("assets/" + normalized);
        return normalized;
    }

    private static void ValidateEntryName(string name)
    {
        if (name.Contains("..", StringComparison.Ordinal) || name.StartsWith('/') || name.Contains(':') || name.Contains('\\'))
        {
            throw Corrupt("illegal file name " + name);
        }
    }

    private static BootrixException Corrupt(string reason) =>
        new(ErrorCode.ProfilePackageCorrupt, reason) { Arguments = [reason] };
}

public sealed record OpenedPackage(ProfileFile Profile, IReadOnlyDictionary<string, byte[]> Assets);
