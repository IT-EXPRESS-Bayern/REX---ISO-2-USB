// SPDX-License-Identifier: GPL-3.0-or-later
using System.CommandLine;
using Bootrix.Cli.Output;
using Bootrix.Core.Catalog;
using Bootrix.Core.Text;

namespace Bootrix.Cli.Commands;

internal static class CatalogCommand
{
    public static Command Create(CatalogService catalog)
    {
        return new Command("catalog", "Browse the images Bootrix can download.")
        {
            CreateProducts(catalog),
            CreateVariants(catalog),
        };
    }

    private static Command CreateProducts(CatalogService catalog)
    {
        var family = new Option<CatalogFamily?>("--family", "-f") { Description = "Only one family: windows, linux, bsd, dos, rescue or utility." };
        var json = new Option<bool>("--json") { Description = "Machine readable output (one JSON object per line)." };

        var command = new Command("products", "List the products (Windows 11, Ubuntu, SystemRescue ...).") { family, json };
        command.SetAction(async (parse, cancellationToken) =>
        {
            var writer = new ConsoleWriter(parse.GetValue(json));
            try
            {
                var listing = await catalog.ListProductsAsync(cancellationToken).ConfigureAwait(false);
                var wanted = parse.GetValue(family);
                var products = listing.Items.Where(p => wanted is null || p.Family == wanted).ToList();

                foreach (var product in products)
                {
                    if (writer.Json)
                    {
                        writer.WriteObject(new { id = product.Id, provider = product.Provider, family = product.Family.ToString(), name = product.Name, license = product.License, description = product.Description });
                    }
                }

                if (!writer.Json)
                {
                    writer.WriteTable(
                        ["Id", "Family", "Name", "License"],
                        [.. products.Select(p => (IReadOnlyList<string>)[p.Id, p.Family.ToString(), p.Name, p.License ?? ""])]);
                }

                foreach (var failure in listing.Failures)
                {
                    // One vendor being down is a note, not a failure of the whole command.
                    if (writer.Json)
                    {
                        writer.WriteObject(new { type = "provider-failed", provider = failure.Provider, error = failure.Error.Message });
                    }
                    else
                    {
                        writer.WriteLine($"Note: provider '{failure.Provider}' is unavailable ({failure.Error.Message}).");
                    }
                }

                return ExitCodes.Success;
            }
            catch (Exception ex)
            {
                writer.WriteError(ex);
                return ExitCodes.For(ex);
            }
        });
        return command;
    }

    private static Command CreateVariants(CatalogService catalog)
    {
        var product = new Argument<string>("product") { Description = "Product id from 'catalog products'." };
        var json = new Option<bool>("--json") { Description = "Machine readable output (one JSON object per line)." };

        var command = new Command("variants", "List the releases, editions and languages of a product.") { product, json };
        command.SetAction(async (parse, cancellationToken) =>
        {
            var writer = new ConsoleWriter(parse.GetValue(json));
            try
            {
                var found = await FindProductAsync(catalog, parse.GetValue(product)!, cancellationToken).ConfigureAwait(false);
                var variants = await catalog.ListVariantsAsync(found, cancellationToken).ConfigureAwait(false);

                if (writer.Json)
                {
                    foreach (var variant in variants)
                    {
                        writer.WriteObject(new
                        {
                            id = variant.Id,
                            name = variant.Name,
                            version = variant.Version,
                            language = variant.Language,
                            architectures = variant.Architectures,
                            sizeBytes = variant.SizeBytes,
                            recommended = variant.IsRecommended,
                            endOfSupport = variant.EndOfSupport,
                            manualUrl = variant.ManualUrl,
                        });
                    }
                }
                else
                {
                    writer.WriteTable(
                        ["Id", "Name", "Language", "Arch", "Size", "Notes"],
                        [.. variants.Select(v => (IReadOnlyList<string>)
                        [
                            v.Id,
                            v.Name,
                            v.Language ?? "",
                            string.Join(',', v.Architectures),
                            v.SizeBytes is { } size ? ByteSize.Format(size) : "",
                            Notes(v),
                        ])]);
                }

                return ExitCodes.Success;
            }
            catch (Exception ex)
            {
                writer.WriteError(ex);
                return ExitCodes.For(ex);
            }
        });
        return command;
    }

    internal static async Task<CatalogProduct> FindProductAsync(CatalogService catalog, string id, CancellationToken cancellationToken)
    {
        var listing = await catalog.ListProductsAsync(cancellationToken).ConfigureAwait(false);
        return listing.Items.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new Core.Errors.BootrixException(Core.Errors.ErrorCode.CatalogUnavailable, $"unknown product '{id}'");
    }

    private static string Notes(CatalogVariant variant)
    {
        var notes = new List<string>();
        if (variant.IsRecommended)
        {
            notes.Add("recommended");
        }

        if (variant.ManualUrl is not null)
        {
            notes.Add("manual download");
        }

        if (variant.EndOfSupport is { } end)
        {
            notes.Add($"support ends {end:yyyy-MM-dd}");
        }

        return string.Join(", ", notes);
    }
}
