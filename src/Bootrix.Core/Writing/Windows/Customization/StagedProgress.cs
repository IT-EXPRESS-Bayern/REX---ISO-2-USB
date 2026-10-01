// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Writing.Windows.Customization;

/// <summary>
/// Maps the stages of one customization onto a single 0..1 range. Callbacks of native libraries arrive on
/// arbitrary threads and sometimes step back (a mount reports per phase), so the combined value only ever grows.
/// </summary>
public sealed class StagedProgress
{
    private readonly IProgress<double> _target;
    private readonly double[] _starts;
    private readonly double[] _shares;
    private readonly Lock _gate = new();
    private double _last;

    public StagedProgress(IProgress<double> target, params double[] weights)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (weights.Length == 0 || weights.Any(w => w <= 0 || double.IsNaN(w)))
        {
            throw new ArgumentException("Every stage needs a positive weight.", nameof(weights));
        }

        _target = target;
        var total = weights.Sum();
        _shares = [.. weights.Select(w => w / total)];
        _starts = new double[weights.Length];
        for (var i = 1; i < weights.Length; i++)
        {
            _starts[i] = _starts[i - 1] + _shares[i - 1];
        }
    }

    /// <summary>A reporter for one stage; its values are fractions of that stage.</summary>
    public IProgress<double> Stage(int index)
    {
        if ((uint)index >= (uint)_shares.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return new StageReporter(this, index);
    }

    /// <summary>A reporter for the part of a stage between <paramref name="from"/> and <paramref name="to"/>, for a step that has several phases.</summary>
    public IProgress<double> Stage(int index, double from, double to) => new ScaledReporter(Stage(index), from, to);

    /// <summary>Marks the stage as done, whatever it reported before.</summary>
    public void Complete(int index) => Report(index, 1);

    private void Report(int index, double fraction)
    {
        var inside = double.IsNaN(fraction) ? 0 : Math.Clamp(fraction, 0, 1);
        var value = Math.Min(1, _starts[index] + inside * _shares[index]);
        lock (_gate)
        {
            if (value <= _last)
            {
                return;
            }

            _last = value;
            _target.Report(value);
        }
    }

    private sealed class StageReporter(StagedProgress owner, int index) : IProgress<double>
    {
        public void Report(double value) => owner.Report(index, value);
    }

    private sealed class ScaledReporter(IProgress<double> inner, double from, double to) : IProgress<double>
    {
        public void Report(double value) =>
            inner.Report(from + (double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1)) * (to - from));
    }
}
