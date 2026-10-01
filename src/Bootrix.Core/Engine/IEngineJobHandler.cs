// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Jobs;

namespace Bootrix.Core.Engine;

/// <summary>
/// One kind of job the local engine can run. A new kind of job brings its request type (derived from
/// <see cref="EngineJobRequest"/>) and a handler; the engine itself does not change.
/// </summary>
public interface IEngineJobHandler
{
    Type RequestType { get; }

    Task<EngineJobResult> RunAsync(
        EngineJobRequest request,
        IProgress<ProgressReport> progress,
        CancellationToken cancellationToken,
        CancellationToken abortToken);
}

public abstract class EngineJobHandler<TRequest> : IEngineJobHandler
    where TRequest : EngineJobRequest
{
    public Type RequestType => typeof(TRequest);

    Task<EngineJobResult> IEngineJobHandler.RunAsync(
        EngineJobRequest request,
        IProgress<ProgressReport> progress,
        CancellationToken cancellationToken,
        CancellationToken abortToken) =>
        RunAsync((TRequest)request, progress, cancellationToken, abortToken);

    protected abstract Task<EngineJobResult> RunAsync(
        TRequest request,
        IProgress<ProgressReport> progress,
        CancellationToken cancellationToken,
        CancellationToken abortToken);
}
