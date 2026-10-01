// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Jobs;
using Bootrix.Core.Storage;
using Bootrix.Windows.Broker;
using Bootrix.Windows.Storage;
using Microsoft.Extensions.Logging;

namespace Bootrix.Windows.Writing;

/// <summary>The platform services every writer needs, bundled so a writer takes one constructor argument.</summary>
public sealed class WriteServices(
    IDiskService disks,
    DiskPreparer preparer,
    JobJournal journal,
    ILoggerFactory loggers,
    IClientImpersonator? clientImpersonator = null)
{
    public IDiskService Disks { get; } = disks;

    public DiskPreparer Preparer { get; } = preparer;

    public JobJournal Journal { get; } = journal;

    /// <summary>
    /// Runs code with the rights of the user the elevated process works for. Set in the broker, where files the
    /// user names (driver folders) must only be read if the user could read them; null where the job already runs as the user.
    /// </summary>
    public IClientImpersonator? ClientImpersonator { get; } = clientImpersonator;

    public ILogger<T> LoggerFor<T>() => loggers.CreateLogger<T>();
}
