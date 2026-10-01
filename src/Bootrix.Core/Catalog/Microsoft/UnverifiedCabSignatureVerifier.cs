// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Catalog.Microsoft;

/// <summary>The default where no platform verifier exists: reports honestly that nothing was checked.</summary>
public sealed class UnverifiedCabSignatureVerifier : ICabSignatureVerifier
{
    public CabSignatureResult Verify(ReadOnlyMemory<byte> cabinet) => new(CabSignatureStatus.NotChecked);
}
