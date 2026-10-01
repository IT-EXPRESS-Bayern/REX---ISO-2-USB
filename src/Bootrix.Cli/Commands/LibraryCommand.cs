// SPDX-License-Identifier: GPL-3.0-or-later
using System.CommandLine;
using Bootrix.Cli.Output;
using Bootrix.Core.Library;
using Bootrix.Core.Text;

namespace Bootrix.Cli.Commands;

internal static class LibraryCommand
{
    public static Command Create(ImageLibrary library)
    {
        return new Command("library", "The images downloaded earlier.")
        {
            CreateList(library),
            CreateCleanup(library),
        };
    }

    private static Command CreateList(ImageLibrary library)
    {
        var query = new Argument<string>("query") { Description = "Words to look for in name, version, product or language.", Arity = ArgumentArity.ZeroOrOne };
        var json = new Option<bool>("--json") { Description = "Machine readable output (one JSON object per line)." };

        var command = new Command("list", "List the images in the library.") { query, json };
        command.SetAction(async (parse, cancellationToken) =>
        {
            var writer = new ConsoleWriter(parse.GetValue(json));
            try
            {
                var words = parse.GetValue(query);
                var entries = string.IsNullOrWhiteSpace(words)
                    ? (await library.ListAsync(cancellationToken).ConfigureAwait(false)).Entries
                    : await library.SearchAsync(words, cancellationToken).ConfigureAwait(false);

                if (writer.Json)
                {
                    foreach (var entry in entries)
                    {
                        writer.WriteObject(new { sha256 = entry.Sha256, path = entry.Path, sizeBytes = entry.Size, name = entry.Info.Name, version = entry.Info.Version, location = entry.Location.ToString(), lastUsed = entry.LastUsedUtc });
                    }
                }
                else
                {
                    writer.WriteTable(
                        ["Name", "Version", "Size", "Where", "SHA-256"],
                        [.. entries.Select(e => (IReadOnlyList<string>)[e.Info.Name ?? Path.GetFileName(e.Path), e.Info.Version ?? "", ByteSize.Format(e.Size), e.Location.ToString(), e.Sha256[..12]])]);
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

    private static Command CreateCleanup(ImageLibrary library)
    {
        var maxGb = new Option<double?>("--max-size-gb") { Description = "Keep the library below this size; the least recently used images go first." };
        var idleDays = new Option<int?>("--older-than-days") { Description = "Remove images that were not used for this many days." };
        var keep = new Option<int?>("--keep-versions") { Description = "Keep this many versions of each product." };
        var apply = new Option<bool>("--apply") { Description = "Really delete. Without it only the plan is shown." };
        var json = new Option<bool>("--json") { Description = "Machine readable output (one JSON object per line)." };

        var command = new Command("cleanup", "Free disk space by removing old images.") { maxGb, idleDays, keep, apply, json };
        command.SetAction(async (parse, cancellationToken) =>
        {
            var writer = new ConsoleWriter(parse.GetValue(json));
            try
            {
                var policy = new LibraryCleanupPolicy
                {
                    MaxTotalBytes = parse.GetValue(maxGb) is { } gb ? (long)(gb * (1L << 30)) : null,
                    MaxIdle = parse.GetValue(idleDays) is { } days ? TimeSpan.FromDays(days) : null,
                    KeepVersionsPerProduct = parse.GetValue(keep),
                };

                var plan = await library.PlanCleanupAsync(policy, cancellationToken).ConfigureAwait(false);
                foreach (var item in plan.Items)
                {
                    if (writer.Json)
                    {
                        writer.WriteObject(new { type = "remove", path = item.Entry.Path, sizeBytes = item.Entry.Size, reason = item.Reason.ToString() });
                    }
                    else
                    {
                        writer.WriteLine($"{(parse.GetValue(apply) ? "Remove" : "Would remove")} {item.Entry.Path} ({ByteSize.Format(item.Entry.Size)}, {item.Reason})");
                    }
                }

                if (parse.GetValue(apply))
                {
                    var result = await library.CleanupAsync(plan, cancellationToken).ConfigureAwait(false);
                    writer.WriteLine(writer.Json ? string.Empty : $"Freed {ByteSize.Format(result.BytesFreed)}.");
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
}
