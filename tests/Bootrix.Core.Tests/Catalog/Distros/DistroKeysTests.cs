// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Catalog.Distros.Common;

namespace Bootrix.Core.Tests.Catalog.Distros;

public class DistroKeysTests
{
    // Each fingerprint below is copied from the vendor's own page on 2026-10-01 (listed next to it), not from the
    // key file, so a wrong or swapped file in Keys/ is caught here.
    [Theory]
    [InlineData("ubuntu", "8439 38DF 228D 22F7 B374  2BC0 D94A A3F0 EFE2 1092", "https://ubuntu.com/tutorials/how-to-verify-ubuntu")]
    [InlineData("debian", "DF9B 9C49 EAA9 2984 3258  9D76 DA87 E80D 6294 BE9B", "https://www.debian.org/CD/verify")]
    [InlineData("mint", "27DE B156 44C6 B3CF 3BD7  D291 300F 846B A25B AE09", "https://linuxmint-installation-guide.readthedocs.io/en/latest/verify.html")]
    [InlineData("opensuse", "AD48 5664 E901 B867 051A  B15F 35A2 F86E 29B7 00A4", "https://get.opensuse.org/tumbleweed/")]
    [InlineData("kali", "827C 8569 F251 8CC6 77FE  CA1A ED65 462E C8D5 E4C5", "https://www.kali.org/docs/introduction/download-official-kali-linux-images/")]
    [InlineData("clonezilla", "54C0 821A 4871 5DAF D61B  FCAF 6678 57D0 4559 9AFD", "https://clonezilla.org/downloads.php")]
    [InlineData("gparted", "EB1D D5BF 6F88 820B BCF5  356C 8E94 C9CD 163E 3FB0", "https://gparted.org/gpg-verify.php")]
    public void PinnedKey_IsTheOneTheVendorPublishes(string distro, string printed, string source)
    {
        var keys = Keys(distro);
        var expected = printed.Replace(" ", string.Empty, StringComparison.Ordinal);

        Assert.Equal([expected], keys.Fingerprints);
        Assert.True(keys.Keyring.Fingerprints.Contains(expected), $"The {distro} key file does not hold the key printed at {source}.");
    }

    [Fact]
    public void KeyFiles_ContainNothingBeyondThePinnedKeys()
    {
        foreach (var distro in new[] { "ubuntu", "debian", "mint", "opensuse", "kali", "clonezilla", "gparted" })
        {
            var keys = Keys(distro);
            Assert.Equal(keys.Fingerprints.Order(), keys.Keyring.Fingerprints.Order());
        }
    }

    [Fact]
    public void Fedora_PinsOneKeyPerRelease()
    {
        // fedoraproject.org/fedora.gpg and src.fedoraproject.org/rpms/fedora-repos list the same fingerprints.
        var expected = new Dictionary<int, string>
        {
            [43] = "C6E7F081CF80E13146676E88829B606631645531",
            [44] = "36F612DCF27F7D1A48A835E4DBFCF71C6D9F90A6",
            [45] = "4F50A6114CD5C6976A7F1179655A4B02F577861E",
            [46] = "D924B10D3E810DABDD8B56B596E7E91491211FCE",
        };

        Assert.Equal(expected.Keys.Order(), DistroKeys.FedoraReleases.Order());
        foreach (var (release, fingerprint) in expected)
        {
            Assert.Equal([fingerprint], DistroKeys.FedoraFor(release).Fingerprints);
            Assert.True(DistroKeys.HasFedoraKey(release));
        }

        Assert.Equal(expected.Values.Order(), DistroKeys.Fedora.Keyring.Fingerprints.Order());
    }

    [Fact]
    public void Fedora_HasNoKeyForAReleaseItDoesNotKnow()
    {
        Assert.False(DistroKeys.HasFedoraKey(41));
        Assert.Throws<ArgumentOutOfRangeException>(() => DistroKeys.FedoraFor(41));
    }

    [Fact]
    public void KeyFile_ThatLacksThePinnedKey_IsRejectedAtLoad()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            PinnedKeys.FromResource("ubuntu-cd-image.asc", "DF9B9C49EAA9298432589D76DA87E80D6294BE9B"));

        Assert.Contains("DF9B9C49EAA9298432589D76DA87E80D6294BE9B", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingKeyFile_IsAnError()
    {
        Assert.Throws<InvalidOperationException>(() => PinnedKeys.FromResource("nonexistent.asc", "AA"));
    }

    private static PinnedKeys Keys(string distro) => distro switch
    {
        "ubuntu" => DistroKeys.Ubuntu,
        "debian" => DistroKeys.Debian,
        "mint" => DistroKeys.LinuxMint,
        "opensuse" => DistroKeys.OpenSuse,
        "kali" => DistroKeys.Kali,
        "clonezilla" => DistroKeys.Clonezilla,
        "gparted" => DistroKeys.GParted,
        _ => throw new ArgumentOutOfRangeException(nameof(distro)),
    };
}
