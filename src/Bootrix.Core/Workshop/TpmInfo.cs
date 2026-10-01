// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Workshop;

public sealed record TpmInfo
{
    /// <summary>False when Windows sees no TPM; a firmware TPM (Intel PTT, AMD fTPM) that is switched off in the setup looks the same.</summary>
    public bool? Present { get; init; }

    /// <summary>Specification version as "1.2" or "2.0".</summary>
    public string? SpecVersion { get; init; }

    public string? Manufacturer { get; init; }

    public bool? IsEnabled { get; init; }

    public bool? IsActivated { get; init; }

    public bool? IsVersion20 => SpecVersion is null ? null : SpecVersion.StartsWith("2.", StringComparison.Ordinal);
}
