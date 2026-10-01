// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Windows.Broker;

namespace Bootrix.Windows.Tests.Broker;

public sealed class BrokerSecretFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bootrix-secret-test-" + Guid.NewGuid().ToString("N"));

    public BrokerSecretFileTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string NewPath() => Path.Combine(_directory, $"bootrix-broker-{Guid.NewGuid():N}.key");

    [Theory]
    [InlineData(@"C:\Users\Max\AppData\Local\Temp\bootrix-broker-0123456789abcdef0123456789abcdef.key")]
    [InlineData("D:/temp/bootrix-broker-0123456789abcdef0123456789abcdef.key")]
    [InlineData(@"C:\a b\c d\bootrix-broker-0123456789abcdef0123456789abcdef.key")]
    public void PathsOfTheGeneratedForm_AreAcceptable(string path)
    {
        Assert.True(BrokerSecretFile.IsAcceptablePath(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bootrix-broker-0123456789abcdef0123456789abcdef.key")]
    [InlineData(@"temp\bootrix-broker-0123456789abcdef0123456789abcdef.key")]
    [InlineData(@"\\server\share\bootrix-broker-0123456789abcdef0123456789abcdef.key")]
    [InlineData(@"\\?\C:\temp\bootrix-broker-0123456789abcdef0123456789abcdef.key")]
    [InlineData(@"C:\temp\..\bootrix-broker-0123456789abcdef0123456789abcdef.key")]
    [InlineData(@"C:\temp\.\bootrix-broker-0123456789abcdef0123456789abcdef.key")]
    [InlineData(@"C:\temp\\bootrix-broker-0123456789abcdef0123456789abcdef.key")]
    [InlineData(@"C:\temp\bootrix-broker-0123456789abcdef0123456789abcdef.key.txt")]
    [InlineData(@"C:\temp\bootrix-broker-0123456789ABCDEF0123456789abcdef.key")]
    [InlineData(@"C:\temp\bootrix-broker-0123456789abcdef.key")]
    [InlineData(@"C:\Windows\System32\config\SAM")]
    [InlineData(@"C:\temp\bootrix-broker-0123456789abcdef0123456789abcdef.key:stream")]
    [InlineData("C:\\temp\\bootrix-broker-0123456789abcdef0123456789abcdef.key\0")]
    [InlineData("C:\\temp\\bootrix-broker-0123456789abcdef0123456789abcdef.key\n")]
    public void AnythingElse_IsNotAcceptable(string? path)
    {
        Assert.False(BrokerSecretFile.IsAcceptablePath(path));
    }

    [Fact]
    public void ReadAndDelete_ReturnsTheSecretAndRemovesTheFile()
    {
        var secret = Enumerable.Range(1, BrokerSecretFile.SecretBytes).Select(i => (byte)i).ToArray();
        var path = NewPath();
        File.WriteAllBytes(path, secret);

        var read = BrokerSecretFile.ReadAndDelete(path);

        Assert.Equal(secret, read);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(4096)]
    public void ReadAndDelete_WrongSize_ThrowsButStillRemovesTheFile(int size)
    {
        var path = NewPath();
        File.WriteAllBytes(path, new byte[size]);

        Assert.Throws<InvalidDataException>(() => BrokerSecretFile.ReadAndDelete(path));

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ReadAndDelete_MissingFile_Throws()
    {
        Assert.ThrowsAny<IOException>(() => BrokerSecretFile.ReadAndDelete(NewPath()));
    }

    [Fact]
    public void ReadAndDelete_FileThatIsLockedElsewhere_Fails()
    {
        // Another process holding the file means somebody else is reading it; the broker must not use it.
        var path = NewPath();
        File.WriteAllBytes(path, new byte[BrokerSecretFile.SecretBytes]);
        using var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.ThrowsAny<IOException>(() => BrokerSecretFile.ReadAndDelete(path));
    }
}
