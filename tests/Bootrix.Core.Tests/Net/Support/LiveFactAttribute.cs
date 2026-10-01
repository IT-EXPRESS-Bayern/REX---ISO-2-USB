// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Tests.Net.Support;

/// <summary>
/// A test that needs the real internet. It is skipped unless the environment variable <c>BOOTRIX_LIVE_TESTS</c> is
/// set to 1, so a normal test run never depends on a network.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("BOOTRIX_LIVE_TESTS") != "1")
        {
            Skip = "Live network test; set BOOTRIX_LIVE_TESTS=1 to run it.";
        }
    }
}
