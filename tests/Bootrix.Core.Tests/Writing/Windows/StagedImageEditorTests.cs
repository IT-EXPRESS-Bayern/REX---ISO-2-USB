// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Bootrix.Core.Errors;
using Bootrix.Core.Tests.Writing.Windows.Support;
using Bootrix.Core.Writing.Windows.Customization;

namespace Bootrix.Core.Tests.Writing.Windows;

public sealed class StagedImageEditorTests : IDisposable
{
    private readonly ScratchFolder _folder = new();
    private readonly FakeImageTools _tools = new();
    private readonly byte[] _original = FakeWims.BootWim();

    public StagedImageEditorTests() => _folder.Write("sources/boot.wim", _original);

    public void Dispose() => _folder.Dispose();

    private string BootWim => _folder.Full("sources/boot.wim");

    private StagedImageEditor Editor() => new(_tools, _tools);

    private Task EditAsync(IReadOnlyList<int> indexes, Func<int, string, CancellationToken, Task>? edit = null, ProgressLog? progress = null, CancellationToken cancellationToken = default) =>
        Editor().EditAsync(BootWim, indexes, _folder.Work, edit ?? ((_, _, _) => Task.CompletedTask), progress ?? new ProgressLog(), cancellationToken);

    private string Content() => Encoding.Latin1.GetString(File.ReadAllBytes(BootWim));

    [Fact]
    public async Task Edit_MountsACopyInTheWorkFolderAndWritesTheResultBack()
    {
        await EditAsync([2]);

        Assert.Equal(["mount:boot.wim:2", "unmount:commit:2"], _tools.Calls);
        Assert.StartsWith(_folder.Work, Assert.Single(_tools.ImagePaths), StringComparison.Ordinal);
        Assert.EndsWith("+committed2", Content(), StringComparison.Ordinal);
        Assert.Equal(Encoding.Latin1.GetString(_original) + "+committed2", Content());
    }

    [Fact]
    public async Task Edit_NeverMountsThePathOnTheMedium()
    {
        await EditAsync([1, 2]);

        Assert.All(_tools.ImagePaths, path => Assert.DoesNotContain(_folder.Media, path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Edit_LeavesNoScratchFilesOnTheMediumOrInTheWorkFolder()
    {
        await EditAsync([2]);

        Assert.Equal(["boot.wim"], Directory.EnumerateFiles(Path.GetDirectoryName(BootWim)!).Select(Path.GetFileName));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_folder.Work));
    }

    [Fact]
    public async Task Edit_SeveralIndexes_AreMountedOneAfterTheOtherFromOneCopy()
    {
        await EditAsync([1, 3]);

        Assert.Equal(["mount:boot.wim:1", "unmount:commit:1", "mount:boot.wim:3", "unmount:commit:3"], _tools.Calls);
        Assert.Single(_tools.ImagePaths.Distinct());
        Assert.EndsWith("+committed1+committed3", Content(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edit_PassesTheMountFolderToTheEdit()
    {
        string? seen = null;
        var existed = false;

        await EditAsync([2], (_, mount, _) =>
        {
            seen = mount;
            existed = Directory.Exists(mount);
            return Task.CompletedTask;
        });

        Assert.True(existed);
        Assert.StartsWith(_folder.Work, seen, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edit_FailureInTheEdit_DiscardsTheMountAndKeepsTheOriginal()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EditAsync([2], (_, _, _) => throw new InvalidOperationException("boom")));

        Assert.Equal("boom", ex.Message);
        Assert.Contains("discard:2", _tools.Calls);
        Assert.DoesNotContain("unmount:commit:2", _tools.Calls);
        Assert.Equal(_original, File.ReadAllBytes(BootWim));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_folder.Work));
    }

    [Fact]
    public async Task Edit_FailureOnTheSecondImage_KeepsTheOriginalCompletely()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EditAsync([1, 2], (index, _, _) => index == 2 ? throw new InvalidOperationException("boom") : Task.CompletedTask));

        Assert.Equal(["mount:boot.wim:1", "unmount:commit:1", "mount:boot.wim:2", "discard:2"], _tools.Calls);
        Assert.Equal(_original, File.ReadAllBytes(BootWim));
    }

    [Fact]
    public async Task Edit_CancelledDuringTheEdit_DiscardsAndKeepsTheOriginal()
    {
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            EditAsync([2], async (_, _, _) =>
            {
                await cts.CancelAsync();
            }, cancellationToken: cts.Token));

        Assert.DoesNotContain("unmount:commit:2", _tools.Calls);
        Assert.Contains("discard:2", _tools.Calls);
        Assert.Equal(_original, File.ReadAllBytes(BootWim));
    }

    [Fact]
    public async Task Edit_CancelledBeforeTheStart_TouchesNothing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EditAsync([2], cancellationToken: cts.Token));

        Assert.Empty(_tools.Calls);
        Assert.Equal(_original, File.ReadAllBytes(BootWim));
    }

    [Fact]
    public async Task Edit_NotEnoughRoomInTheWorkFolder_StopsBeforeCopyingAnything()
    {
        _tools.FreeBytes = 1024;

        var ex = await Assert.ThrowsAsync<BootrixException>(() => EditAsync([2]));

        Assert.Equal(ErrorCode.InsufficientSpace, ex.Code);
        Assert.Equal(_folder.Work, ex.Arguments[0]);
        Assert.Empty(_tools.Calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_folder.Work));
    }

    [Fact]
    public async Task Edit_NoIndexes_IsRefused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => EditAsync([]));
    }

    [Fact]
    public async Task Edit_ReportsProgressFromZeroToOneWithoutGoingBack()
    {
        var progress = new ProgressLog();

        await EditAsync([1, 2], progress: progress);

        progress.AssertMonotonicToOne();
        Assert.True(progress.Values.Count > 4);
    }

    [Fact]
    public async Task Edit_ReadOnlyOriginalFromAnIso_IsStillReplaced()
    {
        File.SetAttributes(BootWim, FileAttributes.ReadOnly);

        await EditAsync([2]);

        Assert.EndsWith("+committed2", Content(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edit_RepeatedRuns_DoNotAccumulateScratchFolders()
    {
        await EditAsync([2]);
        await EditAsync([2]);

        Assert.Empty(Directory.EnumerateFileSystemEntries(_folder.Work));
        Assert.EndsWith("+committed2+committed2", Content(), StringComparison.Ordinal);
    }
}
