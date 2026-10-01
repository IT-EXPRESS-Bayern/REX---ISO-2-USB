// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Writing.Windows;

/// <summary>
/// One running byte count for several targets that are written one after the other: the first target fills
/// the first share of the bar, the second the next, and so on. Progress of a target never leaves its share,
/// even when an estimate was a little off.
/// </summary>
public sealed class TargetSequenceProgress
{
    private readonly long _bytesPerTarget;
    private readonly int _targets;

    public TargetSequenceProgress(int targets, long bytesPerTarget)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(targets, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(bytesPerTarget);
        _targets = targets;
        _bytesPerTarget = bytesPerTarget;
    }

    public long TotalBytes => _targets * _bytesPerTarget;

    public long Overall(int targetIndex, long bytesOnTarget)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(targetIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(targetIndex, _targets);
        return (targetIndex * _bytesPerTarget) + Math.Clamp(bytesOnTarget, 0, _bytesPerTarget);
    }
}
