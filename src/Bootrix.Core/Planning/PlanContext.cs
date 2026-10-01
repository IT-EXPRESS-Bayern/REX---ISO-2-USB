// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Images;
using Bootrix.Core.Model;
using Bootrix.Core.Profiles;

namespace Bootrix.Core.Planning;

internal enum MediaPurpose
{
    Windows,
    Linux,
    Dos,
    Data,
}

/// <summary>The inputs of one planning run plus the warnings collected on the way.</summary>
internal sealed class PlanContext
{
    private readonly List<PlanWarning> _warnings = [];

    public PlanContext(ImageProfile image, TargetOptions target, DeviceCaps device)
    {
        Image = image;
        Target = target;
        Device = device;
        Purpose = PurposeOf(image.Kind);
    }

    public ImageProfile Image { get; }

    public TargetOptions Target { get; }

    public DeviceCaps Device { get; }

    public MediaPurpose Purpose { get; }

    public int SectorSize => Device.LogicalSectorSize;

    /// <summary>The device size rounded down to whole sectors.</summary>
    public long DeviceBytes => Device.SizeBytes / SectorSize * SectorSize;

    public IReadOnlyList<PlanWarning> Warnings => _warnings;

    public void Warn(string code, params object?[] args)
    {
        if (!_warnings.Any(existing => existing.Code == code))
        {
            _warnings.Add(new PlanWarning(code, args));
        }
    }

    public BootrixException DeviceTooSmall(long requiredBytes) =>
        new(ErrorCode.DeviceTooSmall, $"needs {requiredBytes} bytes, device has {DeviceBytes}")
        {
            Arguments = [SizeText.Format(requiredBytes), SizeText.Format(DeviceBytes)],
        };

    public static BootrixException Unsupported(ErrorCode code, params object?[] args) =>
        new(code, string.Join(", ", args)) { Arguments = args };

    private static MediaPurpose PurposeOf(ImageKind kind) => kind switch
    {
        ImageKind.WindowsSetup or ImageKind.WindowsPe => MediaPurpose.Windows,
        ImageKind.LinuxHybrid or ImageKind.LinuxIsoOnly => MediaPurpose.Linux,
        ImageKind.Dos => MediaPurpose.Dos,
        _ => MediaPurpose.Data,
    };
}
