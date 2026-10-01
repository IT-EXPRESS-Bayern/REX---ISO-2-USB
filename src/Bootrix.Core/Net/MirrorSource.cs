// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Net;

/// <summary>One place a file can be fetched from.</summary>
/// <param name="Url">Absolute http or https address.</param>
/// <param name="Priority">Lower values are preferred, as in Metalink 4.</param>
/// <param name="Location">Two-letter country code announced by the mirror list, if any.</param>
/// <param name="MaxConnections">Parallel connections the mirror tolerates; 0 means no limit.</param>
public sealed record MirrorSource(Uri Url, int Priority = 1, string? Location = null, int MaxConnections = 0);
