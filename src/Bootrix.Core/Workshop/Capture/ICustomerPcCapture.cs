// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Workshop.Capture;

/// <summary>Collects the inventory of the customer PC the program runs on. Everything stays local: no network access, no telemetry.</summary>
public interface ICustomerPcCapture
{
    /// <summary>A source that fails leaves its part null and adds a <see cref="CollectionIssue"/>; only cancellation throws.</summary>
    Task<CustomerPcCapture> CaptureAsync(CustomerPcCaptureOptions options, CancellationToken cancellationToken = default);
}
