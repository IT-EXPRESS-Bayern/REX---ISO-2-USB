// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json.Nodes;

namespace Bootrix.Core.Profiles;

/// <summary>
/// A named profile. <see cref="Spec"/> is a JSON merge patch on top of the parent profile (or on
/// top of the defaults when there is no parent), so "Customer Müller" only has to list what
/// differs from "Standard customer".
/// </summary>
public sealed record ProfileFile
{
    public int SchemaVersion { get; init; } = 1;

    public string Name { get; init; } = "";

    public string? Description { get; init; }

    public string? Extends { get; init; }

    /// <summary>Property paths (for example "windows.bypassTpm") that child profiles may not change.</summary>
    public IReadOnlyList<string> Locked { get; init; } = [];

    public JsonObject? Spec { get; init; }
}
