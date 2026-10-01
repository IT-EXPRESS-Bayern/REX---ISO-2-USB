// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Catalog.Distros.Common;

/// <summary>
/// The signing keys Bootrix trusts for distribution checksum files. A new vendor key (they rotate every few years)
/// is rejected until it is added here; that is intended, the catalog never learns keys from the network.
/// </summary>
internal static class DistroKeys
{
    private static readonly Lazy<PinnedKeys> UbuntuKeys = new(() => PinnedKeys.FromResource(
        "ubuntu-cd-image.asc",
        "843938DF228D22F7B3742BC0D94AA3F0EFE21092"));

    private static readonly Lazy<PinnedKeys> DebianKeys = new(() => PinnedKeys.FromResource(
        "debian-cd-signing.asc",
        "DF9B9C49EAA9298432589D76DA87E80D6294BE9B"));

    private static readonly Lazy<PinnedKeys> MintKeys = new(() => PinnedKeys.FromResource(
        "linuxmint-iso-signing.asc",
        "27DEB15644C6B3CF3BD7D291300F846BA25BAE09"));

    private static readonly Lazy<PinnedKeys> OpenSuseKeys = new(() => PinnedKeys.FromResource(
        "opensuse-project-signing.asc",
        "AD485664E901B867051AB15F35A2F86E29B700A4"));

    private static readonly Lazy<PinnedKeys> KaliKeys = new(() => PinnedKeys.FromResource(
        "kali-archive-2025.asc",
        "827C8569F2518CC677FECA1AED65462EC8D5E4C5"));

    private static readonly Lazy<PinnedKeys> ClonezillaKeys = new(() => PinnedKeys.FromResource(
        "clonezilla-drbl.asc",
        "54C0821A48715DAFD61BFCAF667857D045599AFD"));

    private static readonly Lazy<PinnedKeys> GPartedKeys = new(() => PinnedKeys.FromResource(
        "gparted-live.asc",
        "EB1DD5BF6F88820BBCF5356C8E94C9CD163E3FB0"));

    /// <summary>Fedora signs each release with its own key; the checksum files of release N are signed by N's key.</summary>
    private static readonly IReadOnlyDictionary<int, string> FedoraReleaseFingerprints = new Dictionary<int, string>
    {
        [43] = "C6E7F081CF80E13146676E88829B606631645531",
        [44] = "36F612DCF27F7D1A48A835E4DBFCF71C6D9F90A6",
        [45] = "4F50A6114CD5C6976A7F1179655A4B02F577861E",
        [46] = "D924B10D3E810DABDD8B56B596E7E91491211FCE",
    };

    private static readonly Lazy<PinnedKeys> FedoraKeys = new(() => PinnedKeys.FromResource(
        "fedora-release.asc",
        [.. FedoraReleaseFingerprints.Values]));

    /// <summary>Ubuntu CD Image Automatic Signing Key (2012); signs the checksums of Ubuntu and its flavours.</summary>
    public static PinnedKeys Ubuntu => UbuntuKeys.Value;

    /// <summary>Debian CD signing key; signs netinst and live checksums.</summary>
    public static PinnedKeys Debian => DebianKeys.Value;

    public static PinnedKeys LinuxMint => MintKeys.Value;

    public static PinnedKeys OpenSuse => OpenSuseKeys.Value;

    public static PinnedKeys Kali => KaliKeys.Value;

    public static PinnedKeys Clonezilla => ClonezillaKeys.Value;

    public static PinnedKeys GParted => GPartedKeys.Value;

    /// <summary>All pinned Fedora release keys, for tests; verification uses <see cref="FedoraFor"/>.</summary>
    public static PinnedKeys Fedora => FedoraKeys.Value;

    public static IEnumerable<int> FedoraReleases => FedoraReleaseFingerprints.Keys;

    public static bool HasFedoraKey(int release) => FedoraReleaseFingerprints.ContainsKey(release);

    /// <summary>The key of one Fedora release only, so a signature made by another release's key does not count.</summary>
    public static PinnedKeys FedoraFor(int release) =>
        FedoraReleaseFingerprints.TryGetValue(release, out var fingerprint)
            ? FedoraKeys.Value.Restrict(fingerprint)
            : throw new ArgumentOutOfRangeException(nameof(release), release, "No pinned key for this Fedora release.");
}
