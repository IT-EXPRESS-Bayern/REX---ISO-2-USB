// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Bootrix.Core.Errors;
using Bootrix.Core.Model;
using Bootrix.Core.Tests.Writing.Windows.Support;
using Bootrix.Core.Writing.Windows.Customization;

namespace Bootrix.Core.Tests.Writing.Windows;

public sealed class AnswerFileWriterTests : IDisposable
{
    private static readonly byte[] New = Encoding.UTF8.GetBytes("<unattend>new</unattend>");

    private readonly ScratchFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Write_NoExistingFile_CreatesIt()
    {
        var result = AnswerFileWriter.Write(_folder.Media, New, ExistingAnswerFilePolicy.ReplaceAndKeepOriginal);

        Assert.Equal(AnswerFileOutcome.Written, result.Outcome);
        Assert.Equal(_folder.Full("autounattend.xml"), result.Path);
        Assert.Null(result.OriginalBackup);
        Assert.Equal(New, File.ReadAllBytes(_folder.Full("autounattend.xml")));
    }

    [Fact]
    public void Write_LeavesNoTemporaryFileBehind()
    {
        AnswerFileWriter.Write(_folder.Media, New, ExistingAnswerFilePolicy.Replace);

        Assert.Equal(["autounattend.xml"], Directory.EnumerateFiles(_folder.Media).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("autounattend.xml")]
    [InlineData("Autounattend.xml")]
    [InlineData("AUTOUNATTEND.XML")]
    public void Write_ReplaceAndKeepOriginal_MovesTheOldFileAside(string existingName)
    {
        _folder.Write(existingName, "old");

        var result = AnswerFileWriter.Write(_folder.Media, New, ExistingAnswerFilePolicy.ReplaceAndKeepOriginal);

        Assert.Equal(_folder.Full("autounattend.xml.original"), result.OriginalBackup);
        Assert.Equal("old", _folder.Read("autounattend.xml.original"));
        Assert.Equal(New, File.ReadAllBytes(_folder.Full("autounattend.xml")));
        Assert.Equal(2, Directory.EnumerateFiles(_folder.Media).Count());
    }

    [Fact]
    public void Write_ReplaceAndKeepOriginal_ReplacesAnEarlierBackup()
    {
        _folder.Write("autounattend.xml", "old");
        _folder.Write("autounattend.xml.original", "older");

        AnswerFileWriter.Write(_folder.Media, New, ExistingAnswerFilePolicy.ReplaceAndKeepOriginal);

        Assert.Equal("old", _folder.Read("autounattend.xml.original"));
    }

    [Fact]
    public void Write_Replace_DropsTheOldFile()
    {
        _folder.Write("Autounattend.xml", "old");

        var result = AnswerFileWriter.Write(_folder.Media, New, ExistingAnswerFilePolicy.Replace);

        Assert.Null(result.OriginalBackup);
        Assert.Equal(["autounattend.xml"], Directory.EnumerateFiles(_folder.Media).Select(Path.GetFileName));
        Assert.Equal(New, File.ReadAllBytes(_folder.Full("autounattend.xml")));
    }

    [Fact]
    public void Write_Keep_LeavesTheImagesFileAlone()
    {
        _folder.Write("autounattend.xml", "old");

        var result = AnswerFileWriter.Write(_folder.Media, New, ExistingAnswerFilePolicy.Keep);

        Assert.Equal(AnswerFileOutcome.LeftAlone, result.Outcome);
        Assert.Null(result.Path);
        Assert.Equal("old", _folder.Read("autounattend.xml"));
        Assert.Single(Directory.EnumerateFiles(_folder.Media));
    }

    [Fact]
    public void Write_Keep_WithoutAnExistingFile_WritesOurs()
    {
        var result = AnswerFileWriter.Write(_folder.Media, New, ExistingAnswerFilePolicy.Keep);

        Assert.Equal(AnswerFileOutcome.Written, result.Outcome);
    }

    [Fact]
    public void Write_Fail_StopsAndTouchesNothing()
    {
        _folder.Write("autounattend.xml", "old");

        var ex = Assert.Throws<BootrixException>(() => AnswerFileWriter.Write(_folder.Media, New, ExistingAnswerFilePolicy.Fail));

        Assert.Equal(ErrorCode.AnswerFileExists, ex.Code);
        Assert.Equal("old", _folder.Read("autounattend.xml"));
        Assert.Single(Directory.EnumerateFiles(_folder.Media));
    }

    [Fact]
    public void Write_ReadOnlyOriginalFromAnIso_CanBeReplaced()
    {
        var path = _folder.Write("autounattend.xml", "old");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        AnswerFileWriter.Write(_folder.Media, New, ExistingAnswerFilePolicy.ReplaceAndKeepOriginal);

        Assert.Equal(New, File.ReadAllBytes(_folder.Full("autounattend.xml")));
        Assert.Equal("old", _folder.Read("autounattend.xml.original"));
    }

    [Fact]
    public void Write_OnlyLooksAtTheRoot()
    {
        _folder.Write("sources/autounattend.xml", "in sources");

        var result = AnswerFileWriter.Write(_folder.Media, New, ExistingAnswerFilePolicy.Fail);

        Assert.Equal(AnswerFileOutcome.Written, result.Outcome);
        Assert.Equal("in sources", _folder.Read("sources/autounattend.xml"));
    }

    [Fact]
    public void Write_FailureToWrite_KeepsTheOriginalInPlace()
    {
        _folder.Write("autounattend.xml", "old");

        // A directory in the way of the temporary file makes the write fail before anything is moved.
        Directory.CreateDirectory(_folder.Full("autounattend.xml.bootrix-new"));

        Assert.ThrowsAny<Exception>(() => AnswerFileWriter.Write(_folder.Media, New, ExistingAnswerFilePolicy.ReplaceAndKeepOriginal));

        Assert.Equal("old", _folder.Read("autounattend.xml"));
        Assert.False(_folder.Exists("autounattend.xml.original"));
    }

    [Fact]
    public void Write_ContentWithNonAsciiAndCrLf_IsWrittenByteForByte()
    {
        var content = Encoding.UTF8.GetBytes("<a>Käse ü \U0001F600</a>\r\n");

        AnswerFileWriter.Write(_folder.Media, content, ExistingAnswerFilePolicy.Replace);

        Assert.Equal(content, File.ReadAllBytes(_folder.Full("autounattend.xml")));
    }
}
