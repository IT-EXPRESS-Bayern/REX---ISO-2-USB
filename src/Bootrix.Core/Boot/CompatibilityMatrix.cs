// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Boot;

/// <param name="Files">The entry files that produced <paramref name="Verdict"/>.</param>
public sealed record MatrixCell(FirmwareProfileId Profile, EfiMachine Machine, BootVerdict Verdict, IReadOnlyList<string> Files);

/// <summary>
/// Whether the medium's boot entry points are accepted by each class of firmware, per CPU architecture.
/// When a medium carries several entry files for one architecture the worst outcome is shown, because the firmware
/// may pick any of them.
/// </summary>
public sealed class CompatibilityMatrix
{
    private CompatibilityMatrix(IReadOnlyList<MatrixCell> cells)
    {
        Cells = cells;
    }

    public IReadOnlyList<MatrixCell> Cells { get; }

    public static CompatibilityMatrix Build(IEnumerable<EfiFileReport> entryFiles)
    {
        ArgumentNullException.ThrowIfNull(entryFiles);

        var byMachine = entryFiles
            .Where(f => f.IsReadable && f.Machine != EfiMachine.Unknown)
            .GroupBy(f => f.Machine)
            .OrderBy(g => g.Key)
            .ToList();

        var cells = new List<MatrixCell>();
        foreach (var profile in FirmwareProfile.All)
        {
            foreach (var group in byMachine)
            {
                var verdicts = group.Select(f => (File: f, Verdict: profile.Evaluate(f))).ToList();
                var worst = verdicts.Max(v => v.Verdict);
                cells.Add(new MatrixCell(profile.Id, group.Key, worst, verdicts.Where(v => v.Verdict == worst).Select(v => v.File.Path).ToList()));
            }
        }

        return new CompatibilityMatrix(cells);
    }

    public BootVerdict? Get(FirmwareProfileId profile, EfiMachine machine) =>
        Cells.Where(c => c.Profile == profile && c.Machine == machine).Select(c => (BootVerdict?)c.Verdict).FirstOrDefault();

    /// <summary>True when every analysed architecture boots on the profile; false when there is nothing to judge.</summary>
    public bool Boots(FirmwareProfileId profile)
    {
        var cells = Cells.Where(c => c.Profile == profile).ToList();
        return cells.Count > 0 && cells.All(c => c.Verdict == BootVerdict.Boots);
    }

    public bool AnyRevoked(FirmwareProfileId profile) =>
        Cells.Any(c => c.Profile == profile && c.Verdict == BootVerdict.Revoked);
}
