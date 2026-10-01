// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Jobs;
using Bootrix.Core.Storage;
using Bootrix.Windows.Storage;
using Microsoft.Extensions.Logging;

namespace Bootrix.Windows.Writing;

/// <summary>The platform services every writer needs, bundled so a writer takes one constructor argument.</summary>
public sealed class WriteServices(IDiskService disks, DiskPreparer preparer, JobJournal journal, ILoggerFactory loggers)
{
    public IDiskService Disks { get; } = disks;

    public DiskPreparer Preparer { get; } = preparer;

    public JobJournal Journal { get; } = journal;

    public ILogger<T> LoggerFor<T>() => loggers.CreateLogger<T>();
}
