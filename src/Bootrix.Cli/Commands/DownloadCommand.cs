// SPDX-License-Identifier: GPL-3.0-or-later
using System.CommandLine;
using Bootrix.Cli.Output;
using Bootrix.Core.Catalog;
using Bootrix.Core.Errors;
using Bootrix.Core.Library;
using Bootrix.Core.Net;

namespace Bootrix.Cli.Commands;

internal static class DownloadCommand
{
    public static Command Create(CatalogService catalog, SegmentedDownloader downloader, ImageLibrary library)
    {
        var product = new Argument<string>("product") { Description = "Product id from 'catalog products', or an https address." };
        var variant = new Option<string>("--variant", "-v") { Description = "Variant id from 'catalog variants'. Without it the recommended one is used." };
        var arch = new Option<string>("--arch", "-a") { Description = "Architecture: x64, arm64, x86 ..." };
        var language = new Option<string>("--language", "-l") { Description = "Language, e.g. de-de (for products that come in several)." };
        var output = new Option<DirectoryInfo>("--output", "-o") { Description = "Folder for the finished file. Without it the image goes into the image library." };
        var sha256 = new Option<string>("--sha256") { Description = "Expected SHA-256 when downloading from an address." };
        var segments = new Option<int>("--segments") { Description = "Parallel connections (1-32).", DefaultValueFactory = _ => 8 };
        var json = new Option<bool>("--json") { Description = "Machine readable output (one JSON object per line)." };

        var command = new Command("download", "Download an image with parallel connections, resume and verification.")
        {
            product, variant, arch, language, output, sha256, segments, json,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var writer = new ConsoleWriter(parse.GetValue(json));
            try
            {
                var target = parse.GetValue(product)!;
                var request = Uri.TryCreate(target, UriKind.Absolute, out var address) && address.Scheme is "http" or "https"
                    ? FromAddress(address, parse.GetValue(sha256), parse.GetValue(segments))
                    : await FromCatalogAsync(catalog, target, parse.GetValue(variant), parse.GetValue(arch), parse.GetValue(language), parse.GetValue(segments), cancellationToken).ConfigureAwait(false);

                var folder = parse.GetValue(output)?.FullName ?? Path.Combine(Path.GetTempPath(), "bootrix-downloads");
                var fileName = FileNameFor(request.Request.Url);
                var destination = Path.Combine(folder, fileName);

                var result = await downloader.DownloadAsync(
                    request.Request,
                    destination,
                    new Progress<DownloadProgress>(writer.WriteDownloadProgress),
                    cancellationToken).ConfigureAwait(false);
                writer.EndProgress();

                var finalPath = result.Path;
                if (parse.GetValue(output) is null)
                {
                    var added = await library.AddAsync(
                        result.Path,
                        request.Info with { FileName = fileName, Source = request.Request.Url.GetLeftPart(UriPartial.Path) },
                        LibraryAddMode.Move,
                        result.Sha256,
                        cancellationToken).ConfigureAwait(false);
                    finalPath = added.Entry.Path;
                }

                if (writer.Json)
                {
                    writer.WriteObject(new { type = "done", path = finalPath, sizeBytes = result.Length, sha256 = result.Sha256, seconds = Math.Round(result.Elapsed.TotalSeconds, 1) });
                }
                else
                {
                    writer.WriteLine($"Saved {finalPath}");
                    writer.WriteLine($"SHA-256 {result.Sha256}");
                }

                return ExitCodes.Success;
            }
            catch (Exception ex)
            {
                writer.EndProgress();
                writer.WriteError(ex);
                return ExitCodes.For(ex);
            }
        });
        return command;
    }

    private sealed record Prepared(DownloadRequest Request, LibraryImageInfo Info);

    private static Prepared FromAddress(Uri address, string? sha256, int segments)
    {
        var request = new DownloadRequest(address)
        {
            ExpectedHashes = sha256 is null ? [] : [new FileHash(HashKind.Sha256, sha256)],
            Options = new DownloadOptions { MaxSegments = segments },
        };
        return new Prepared(request, new LibraryImageInfo { Name = FileNameFor(address) });
    }

    private static async Task<Prepared> FromCatalogAsync(
        CatalogService catalog,
        string productId,
        string? variantId,
        string? architecture,
        string? language,
        int segments,
        CancellationToken cancellationToken)
    {
        var product = await CatalogCommand.FindProductAsync(catalog, productId, cancellationToken).ConfigureAwait(false);
        var variants = await catalog.ListVariantsAsync(product, cancellationToken).ConfigureAwait(false);

        var candidates = variants
            .Where(v => variantId is null || v.Id.Equals(variantId, StringComparison.OrdinalIgnoreCase))
            .Where(v => language is null || string.Equals(v.Language, language, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var chosen = candidates.FirstOrDefault()
            ?? throw new BootrixException(ErrorCode.CatalogUnavailable, "no matching variant")
            {
                Arguments = [$"No variant of '{product.Name}' matches; see 'catalog variants {product.Id}'."],
            };

        if (chosen.ManualUrl is not null)
        {
            throw new BootrixException(ErrorCode.DownloadBlocked, $"manual download: {chosen.ManualUrl}") { Arguments = [chosen.ManualUrl] };
        }

        var chosenArch = architecture ?? (chosen.Architectures.Count == 1 ? chosen.Architectures[0] : chosen.Architectures.Contains("x64") ? "x64" : null);
        var resolved = await catalog.ResolveAsync(chosen, chosenArch, cancellationToken).ConfigureAwait(false);
        var request = resolved with { Options = resolved.Options with { MaxSegments = segments } };

        return new Prepared(
            request,
            new LibraryImageInfo
            {
                Name = chosen.Name,
                Version = chosen.Version,
                CatalogId = product.Id,
                VariantId = chosen.Id,
                Architecture = chosenArch,
                Language = chosen.Language,
            });
    }

    private static string FileNameFor(Uri url)
    {
        var name = Path.GetFileName(Uri.UnescapeDataString(url.AbsolutePath));
        return string.IsNullOrWhiteSpace(name) ? "download.bin" : name;
    }
}
