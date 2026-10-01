// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json.Nodes;
using Bootrix.Core.Profiles;

namespace Bootrix.Core.Tests.Profiles;

public class MergePatchTests
{
    // Test vectors from RFC 7396, appendix A.
    [Theory]
    [InlineData("""{"a":"b"}""", """{"a":"c"}""", """{"a":"c"}""")]
    [InlineData("""{"a":"b"}""", """{"b":"c"}""", """{"a":"b","b":"c"}""")]
    [InlineData("""{"a":"b"}""", """{"a":null}""", """{}""")]
    [InlineData("""{"a":"b","b":"c"}""", """{"a":null}""", """{"b":"c"}""")]
    [InlineData("""{"a":["b"]}""", """{"a":"c"}""", """{"a":"c"}""")]
    [InlineData("""{"a":"c"}""", """{"a":["b"]}""", """{"a":["b"]}""")]
    [InlineData("""{"a":{"b":"c"}}""", """{"a":{"b":"d","c":null}}""", """{"a":{"b":"d"}}""")]
    [InlineData("""{"a":[{"b":"c"}]}""", """{"a":[1]}""", """{"a":[1]}""")]
    [InlineData("""["a","b"]""", """["c","d"]""", """["c","d"]""")]
    [InlineData("""{"a":"b"}""", """["c"]""", """["c"]""")]
    [InlineData("""{"a":"foo"}""", "null", "null")]
    [InlineData("""{"a":"foo"}""", "\"bar\"", "\"bar\"")]
    [InlineData("""{"e":null}""", """{"a":1}""", """{"e":null,"a":1}""")]
    [InlineData("""[1,2]""", """{"a":"b","c":null}""", """{"a":"b"}""")]
    [InlineData("""{}""", """{"a":{"bb":{"ccc":null}}}""", """{"a":{"bb":{}}}""")]
    public void FollowsRfc7396(string target, string patch, string expected)
    {
        var result = MergePatch.Apply(JsonNode.Parse(target), JsonNode.Parse(patch));

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), result), $"got {result?.ToJsonString()}");
    }

    [Fact]
    public void ListsChangedPaths()
    {
        var patch = JsonNode.Parse("""{"windows":{"bypassTpm":true,"driverFolders":["a"]},"name":"x"}""");

        var paths = MergePatch.ChangedPaths(patch).ToList();

        Assert.Equal(["windows.bypassTpm", "windows.driverFolders", "name"], paths);
    }
}
