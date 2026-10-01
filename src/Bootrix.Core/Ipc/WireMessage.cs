// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bootrix.Core.Json;

namespace Bootrix.Core.Ipc;

internal enum WireKind
{
    Request,
    Response,
    Notification,
}

/// <summary>
/// One JSON document per frame. A request carries an id the answer refers to, a notification has no
/// answer. A response without error and without result is the answer of a method that returns nothing.
/// </summary>
internal sealed record WireMessage
{
    public required WireKind Kind { get; init; }

    public long Id { get; init; }

    public string? Method { get; init; }

    public JsonElement? Params { get; init; }

    public JsonElement? Result { get; init; }

    public WireError? Error { get; init; }
}

/// <summary>An error as it crosses the wire: the numeric <see cref="Errors.ErrorCode"/> plus what the localized text needs.</summary>
internal sealed record WireError(int Code, IReadOnlyList<string>? Arguments, string? Detail);

internal sealed record CancelParams(long Id);

internal static class IpcJson
{
    /// <summary>The shared serializer settings, compact because nobody reads the frames by eye.</summary>
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(CoreJson.Options) { WriteIndented = false };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
