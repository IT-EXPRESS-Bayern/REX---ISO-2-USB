// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Windows.Tests;

public class PlaceholderTests
{
    [Fact]
    public void AssemblyLoads()
    {
        Assert.NotNull(typeof(PlaceholderTests).Assembly);
    }
}
