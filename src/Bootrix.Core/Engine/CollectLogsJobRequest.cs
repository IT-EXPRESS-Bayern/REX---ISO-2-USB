// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Engine;

/// <summary>Packs the logs of the elevated process into a ZIP file in the user's place; the unprivileged side cannot read them itself.</summary>
public sealed record CollectLogsJobRequest : EngineJobRequest
{
    public required string OutputPath { get; init; }
}
