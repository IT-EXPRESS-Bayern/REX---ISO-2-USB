// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bootrix.Core.Catalog.Distros.Common;

namespace Bootrix.Core.Catalog.Distros;

/// <param name="Name">Update train, "TrueNAS-SCALE-Goldeye"; also the directory on the download server.</param>
/// <param name="Codename">"Goldeye", "ElectricEel".</param>
internal sealed record TrueNasTrain(string Name, string Codename);

/// <param name="Version">"25.10.7"</param>
internal sealed record TrueNasRelease(string Version, DateOnly? Date);

/// <summary>The update trains and manifests of TrueNAS's update server, which the systems themselves use to find a new version.</summary>
internal static partial class TrueNasReleases
{
    private const string TrainPrefix = "TrueNAS-SCALE-";

    /// <summary>
    /// The release trains that are not retired. Beta and release-candidate trains (their name ends in -BETA or -RC)
    /// and trains the server itself calls "end of life" are left out.
    /// </summary>
    public static IReadOnlyList<TrueNasTrain> Trains(string json)
    {
        using var document = DistroJson.Parse(json, "TrueNAS trains");
        if (document.RootElement.Child("trains") is not { ValueKind: JsonValueKind.Object } trains)
        {
            return [];
        }

        var result = new List<TrueNasTrain>();
        foreach (var train in trains.EnumerateObject())
        {
            var name = train.Name;
            var description = train.Value.String("description") ?? string.Empty;
            if (!name.StartsWith(TrainPrefix, StringComparison.Ordinal)
                || PreRelease().IsMatch(name)
                || description.Contains("end of life", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(new TrueNasTrain(name, name[TrainPrefix.Length..]));
        }

        return result;
    }

    /// <summary>The newest release of a train from its <c>manifest.json</c>, or null if the file does not name a version.</summary>
    public static TrueNasRelease? Manifest(string json)
    {
        using var document = DistroJson.Parse(json, "TrueNAS manifest");
        if (document.RootElement.String("version") is not { Length: > 0 } version || !NumericVersion.TryParse(version, out _))
        {
            return null;
        }

        DateOnly? date = DateTimeOffset.TryParse(document.RootElement.String("date"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? DateOnly.FromDateTime(parsed.UtcDateTime)
            : null;
        return new TrueNasRelease(version, date);
    }

    /// <summary>"ElectricEel" reads better as "Electric Eel".</summary>
    public static string Spaced(string codename) => WordBreak().Replace(codename, " ");

    [GeneratedRegex(@"-(BETA|RC)$", RegexOptions.IgnoreCase)]
    private static partial Regex PreRelease();

    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])")]
    private static partial Regex WordBreak();
}
