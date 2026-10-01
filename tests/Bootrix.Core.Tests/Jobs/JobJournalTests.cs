// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Jobs;

namespace Bootrix.Core.Tests.Jobs;

public sealed class JobJournalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bootrix-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task WritesAndReadsBack()
    {
        var journal = new JobJournal(_dir);
        var entry = new JournalEntry
        {
            JobId = "abc",
            Kind = "WriteImage",
            DiskIdentity = "usb:1234:16000000000",
            Phase = "copy",
            LastFlushedOffset = 4096,
            StartedUtc = DateTimeOffset.UtcNow,
        };

        await journal.WriteAsync(entry);
        var loaded = await journal.ReadAsync("abc");

        Assert.NotNull(loaded);
        Assert.Equal("copy", loaded.Phase);
        Assert.Equal(4096, loaded.LastFlushedOffset);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task FindsOnlyUnfinishedJobs()
    {
        var journal = new JobJournal(_dir);
        await journal.WriteAsync(new JournalEntry { JobId = "run", Kind = "x", State = JournalState.Running });
        var done = new JournalEntry { JobId = "done", Kind = "x" };
        await journal.WriteAsync(done);
        await journal.MarkCompletedAsync(done);

        var unfinished = await journal.FindUnfinishedAsync();

        Assert.Equal(["run"], unfinished.Select(e => e.JobId));
    }

    [Fact]
    public async Task CorruptFileThrowsButDoesNotBreakListing()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "bad.json"), "{ not json");
        var journal = new JobJournal(_dir);

        var ex = await Assert.ThrowsAsync<BootrixException>(() => journal.ReadAsync("bad"));
        Assert.Equal(ErrorCode.JournalCorrupt, ex.Code);
        Assert.Empty(await journal.FindUnfinishedAsync());
    }

    [Fact]
    public async Task MissingJournalReturnsNull()
    {
        Assert.Null(await new JobJournal(_dir).ReadAsync("nothing"));
    }
}
