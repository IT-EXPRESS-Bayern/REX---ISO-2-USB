// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Engine;
using Bootrix.Core.Jobs;

namespace Bootrix.Windows.Tiny;

public sealed class TinyBuildHandler(TinyBuildRunner runner) : EngineJobHandler<TinyBuildJobRequest>
{
    protected override async Task<EngineJobResult> RunAsync(
        TinyBuildJobRequest request,
        IProgress<ProgressReport> progress,
        CancellationToken cancellationToken,
        CancellationToken abortToken)
    {
        var result = await runner.RunAsync(request, new DelegateProgressSink(progress.Report), cancellationToken, abortToken).ConfigureAwait(false);
        return EngineJobResult.From(result);
    }
}
