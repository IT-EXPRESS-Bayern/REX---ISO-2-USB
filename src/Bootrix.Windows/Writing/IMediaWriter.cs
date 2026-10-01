// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images;
using Bootrix.Core.Jobs;
using Bootrix.Core.Planning;

namespace Bootrix.Windows.Writing;

/// <summary>
/// One way of getting an image onto a device: byte for byte, as Windows setup media, as a Linux live stick,
/// as a DOS stick. A writer owns its steps from the first check of the target to the last flush; the
/// job only picks the writer and joins the steps.
/// </summary>
public interface IMediaWriter
{
    /// <summary>Short id for logs and journal entries, e.g. "raw-copy" or "windows-setup".</summary>
    string Id { get; }

    /// <summary>Whether this writer carries out <paramref name="plan"/> for an image of this kind. The first writer that says yes is used.</summary>
    bool CanWrite(MediaPlan plan, ImageProfile image);

    /// <summary>
    /// The steps for all targets of the job. Steps that touch a disk must check the identity first, take the
    /// disk mutex and register everything they open with <see cref="JobContext.OnCleanup(IDisposable)"/>.
    /// </summary>
    IReadOnlyList<IJobStep> CreateSteps(MediaWriteContext context);
}
