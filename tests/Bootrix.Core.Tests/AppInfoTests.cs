// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Tests;

public class AppInfoTests
{
    [Fact]
    public void Version_IsNotEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(AppInfo.Version));
    }
}
