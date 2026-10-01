// SPDX-License-Identifier: GPL-3.0-or-later
using System.CommandLine;
using System.Security.Cryptography;
using Bootrix.Cli.Output;

namespace Bootrix.Cli.Commands;

internal static class HashCommand
{
    public static Command Create()
    {
        var file = new Argument<FileInfo>("file") { Description = "File to hash." };
        var algorithm = new Option<string>("--algorithm", "-a") { Description = "md5, sha1, sha256 or sha512.", DefaultValueFactory = _ => "sha256" };
        var json = new Option<bool>("--json") { Description = "Machine readable output." };

        var command = new Command("hash", "Calculate the checksum of a file.") { file, algorithm, json };
        command.SetAction(async (parse, cancellationToken) =>
        {
            var writer = new ConsoleWriter(parse.GetValue(json));
            try
            {
                var info = parse.GetValue(file)!;
                var name = parse.GetValue(algorithm)!.ToUpperInvariant().Replace("-", string.Empty, StringComparison.Ordinal);
                var hashName = name switch
                {
                    "MD5" => HashAlgorithmName.MD5,
                    "SHA1" => HashAlgorithmName.SHA1,
                    "SHA256" => HashAlgorithmName.SHA256,
                    "SHA512" => HashAlgorithmName.SHA512,
                    _ => throw new ArgumentException($"Unknown algorithm '{name}'."),
                };

                await using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
                using var hash = IncrementalHash.CreateHash(hashName);
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                }

                var result = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (writer.Json)
                {
                    writer.WriteObject(new { file = info.FullName, algorithm = name.ToLowerInvariant(), hash = result, bytes = stream.Length });
                }
                else
                {
                    writer.WriteLine($"{result}  {info.Name}");
                }

                return ExitCodes.Success;
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return ExitCodes.Usage;
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
