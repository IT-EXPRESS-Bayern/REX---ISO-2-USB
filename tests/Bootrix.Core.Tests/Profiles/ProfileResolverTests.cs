// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json.Nodes;
using Bootrix.Core.Errors;
using Bootrix.Core.Model;
using Bootrix.Core.Profiles;

namespace Bootrix.Core.Tests.Profiles;

public class ProfileResolverTests
{
    private sealed class MemorySource(params ProfileFile[] profiles) : IProfileSource
    {
        private readonly Dictionary<string, ProfileFile> _byName = profiles.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        public ProfileFile? Load(string name) => _byName.GetValueOrDefault(name);

        public IEnumerable<string> List() => _byName.Keys;
    }

    private static ProfileFile Profile(string name, string? extends, string spec, params string[] locked) => new()
    {
        Name = name,
        Extends = extends,
        Locked = locked,
        Spec = JsonNode.Parse(spec)!.AsObject(),
    };

    [Fact]
    public void ChildOverridesParentValues()
    {
        var source = new MemorySource(
            Profile("standard", null, """{"target":{"scheme":"Gpt","label":"INSTALL"},"windows":{"bypassTpm":true}}"""),
            Profile("mueller", "standard", """{"target":{"label":"MUELLER"},"windows":{"localAccountName":"Kunde"}}"""));

        var resolved = new ProfileResolver(source).Resolve("mueller");

        Assert.Equal("mueller", resolved.Spec.Name);
        Assert.Equal(PartitionScheme.Gpt, resolved.Spec.Target.Scheme);
        Assert.Equal("MUELLER", resolved.Spec.Target.Label);
        Assert.True(resolved.Spec.Windows.BypassTpm);
        Assert.Equal("Kunde", resolved.Spec.Windows.LocalAccountName);
    }

    [Fact]
    public void ChildCannotChangeLockedOption()
    {
        var source = new MemorySource(
            Profile("standard", null, """{"windows":{"bypassTpm":false}}""", "windows.bypassTpm"),
            Profile("azubi", "standard", """{"windows":{"bypassTpm":true}}"""));

        var ex = Assert.Throws<BootrixException>(() => new ProfileResolver(source).Resolve("azubi"));

        Assert.Equal(ErrorCode.ProfileLocked, ex.Code);
        Assert.Equal("windows.bypassTpm", ex.Arguments[0]);
    }

    [Fact]
    public void LockOnObjectBlocksAllItsMembers()
    {
        var source = new MemorySource(
            Profile("standard", null, """{}""", "windows"),
            Profile("child", "standard", """{"windows":{"skipPrivacyQuestions":true}}"""));

        Assert.Throws<BootrixException>(() => new ProfileResolver(source).Resolve("child"));
    }

    [Fact]
    public void CyclesAreDetected()
    {
        var source = new MemorySource(
            Profile("a", "b", """{}"""),
            Profile("b", "a", """{}"""));

        var ex = Assert.Throws<BootrixException>(() => new ProfileResolver(source).Resolve("a"));

        Assert.Equal(ErrorCode.ProfileCycle, ex.Code);
    }

    [Fact]
    public void UnknownParentIsAnError()
    {
        var source = new MemorySource(Profile("a", "missing", """{}"""));

        var ex = Assert.Throws<BootrixException>(() => new ProfileResolver(source).Resolve("a"));

        Assert.Equal(ErrorCode.InvalidSpec, ex.Code);
    }

    [Fact]
    public void NewerSchemaIsRejected()
    {
        var newer = Profile("future", null, """{}""") with { SchemaVersion = 99 };

        var ex = Assert.Throws<BootrixException>(() => new ProfileResolver(new MemorySource(newer)).Resolve("future"));

        Assert.Equal(ErrorCode.UnsupportedSchemaVersion, ex.Code);
    }

    [Fact]
    public void SpecRoundTripsThroughJson()
    {
        var spec = new JobSpec
        {
            Name = "x",
            Kind = JobKind.TinyBuild,
            Target = new TargetOptions { Scheme = PartitionScheme.Mbr, LegacyBiosFixes = true },
            Windows = new WindowsSetupOptions { BypassCpu = true, DriverFolders = ["C:\\drivers"] },
        };

        var parsed = JobSpecJson.Parse(JobSpecJson.Serialize(spec));

        Assert.Equal(JobKind.TinyBuild, parsed.Kind);
        Assert.True(parsed.Target.LegacyBiosFixes);
        Assert.Equal(["C:\\drivers"], parsed.Windows.DriverFolders);
    }

    [Fact]
    public void InvalidEnumValueIsReportedAsInvalidSpec()
    {
        var ex = Assert.Throws<BootrixException>(() => JobSpecJson.Parse("""{"kind":"Nonsense"}"""));

        Assert.Equal(ErrorCode.InvalidSpec, ex.Code);
    }
}
