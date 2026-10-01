// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Workshop;
using Microsoft.Extensions.Logging;

namespace Bootrix.Windows.Workshop;

/// <summary>
/// Collects what could not be read while the sources run side by side. Each source is guarded on its own: a failure leaves
/// that part empty, which the advisor reports as unknown, and the rest of the check carries on.
/// </summary>
internal sealed class IssueLog(ILogger logger)
{
    private readonly Lock _gate = new();
    private readonly List<CollectionIssue> _issues = [];

    public IReadOnlyList<CollectionIssue> Issues
    {
        get
        {
            lock (_gate)
            {
                return [.. _issues];
            }
        }
    }

    public void Add(string source, Exception exception)
    {
        logger.LogWarning(exception, "Source {Source} could not be read", source);
        lock (_gate)
        {
            _issues.Add(new CollectionIssue(source, exception.Message));
        }
    }

    /// <summary>Runs a reader and turns any failure except cancellation into an issue.</summary>
    public T? Guard<T>(string source, Func<T> read, CancellationToken cancellationToken)
        where T : class?
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Add(source, ex);
            return null;
        }
    }
}
