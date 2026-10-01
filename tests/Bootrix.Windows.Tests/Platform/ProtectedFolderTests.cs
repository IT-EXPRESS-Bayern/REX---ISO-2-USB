// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.AccessControl;
using System.Security.Principal;
using Bootrix.Core.Errors;
using Bootrix.Windows.Platform;

namespace Bootrix.Windows.Tests.Platform;

public sealed class ProtectedFolderTests : IDisposable
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bootrix-protected-" + Guid.NewGuid().ToString("N")[..10]);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static FolderRule Allow(SecurityIdentifier who, FileSystemRights rights) => new(who, rights, Allow: true);

    [WindowsFact]
    public void AFolderOfAdministratorsThatOnlyTheyMayWriteTo_IsTrusted()
    {
        var verdict = ProtectedFolder.Evaluate(false, Administrators, [Allow(System, FileSystemRights.FullControl), Allow(Administrators, FileSystemRights.FullControl)]);

        Assert.Equal(FolderTrust.Trusted, verdict);
    }

    [WindowsFact]
    public void UsersMayReadAndRunWithoutLosingTheTrust()
    {
        var verdict = ProtectedFolder.Evaluate(false, System, [Allow(Administrators, FileSystemRights.FullControl), Allow(Users, FileSystemRights.ReadAndExecute)]);

        Assert.Equal(FolderTrust.Trusted, verdict);
    }

    [WindowsTheory]
    [InlineData(FileSystemRights.WriteData)]
    [InlineData(FileSystemRights.AppendData)]
    [InlineData(FileSystemRights.Delete)]
    [InlineData(FileSystemRights.DeleteSubdirectoriesAndFiles)]
    [InlineData(FileSystemRights.ChangePermissions)]
    [InlineData(FileSystemRights.TakeOwnership)]
    [InlineData(FileSystemRights.Modify)]
    [InlineData(FileSystemRights.FullControl)]
    public void AnyWriteAccessForOthersMakesTheFolderLoose(FileSystemRights rights)
    {
        Assert.Equal(FolderTrust.LooseAccess, ProtectedFolder.Evaluate(false, Administrators, [Allow(Administrators, FileSystemRights.FullControl), Allow(Users, rights)]));
        Assert.Equal(FolderTrust.LooseAccess, ProtectedFolder.Evaluate(false, Administrators, [Allow(Everyone, rights)]));
    }

    [WindowsFact]
    public void DenyRulesAndHarmlessRightsDoNotCount()
    {
        var verdict = ProtectedFolder.Evaluate(
            false,
            Administrators,
            [new FolderRule(Users, FileSystemRights.FullControl, Allow: false), Allow(Users, FileSystemRights.ReadAttributes | FileSystemRights.Synchronize)]);

        Assert.Equal(FolderTrust.Trusted, verdict);
    }

    [WindowsFact]
    public void AFolderOwnedByAnOrdinaryUserCannotBeTrusted()
    {
        var user = new SecurityIdentifier("S-1-5-21-1-2-3-1001");

        Assert.Equal(FolderTrust.ForeignOwner, ProtectedFolder.Evaluate(false, user, [Allow(Administrators, FileSystemRights.FullControl)]));
        Assert.Equal(FolderTrust.ForeignOwner, ProtectedFolder.Evaluate(false, null, []));
    }

    [WindowsFact]
    public void AJunctionIsNeverTrusted()
    {
        Assert.Equal(FolderTrust.ReparsePoint, ProtectedFolder.Evaluate(true, Administrators, [Allow(Administrators, FileSystemRights.FullControl)]));
    }

    [WindowsFact]
    public void ANewFolderIsCreatedClosedToStandardUsers()
    {
        if (!ProcessElevation.IsElevated())
        {
            return;
        }

        ProtectedFolder.Ensure(_root);

        var security = new DirectoryInfo(_root).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        Assert.DoesNotContain(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), r => r.IdentityReference.Equals(Users));

        var child = Directory.CreateDirectory(Path.Combine(_root, "work"));
        Assert.DoesNotContain(child.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), r => r.IdentityReference.Equals(Users));
    }

    [WindowsFact]
    public void EnsureIsRepeatable()
    {
        if (!ProcessElevation.IsElevated())
        {
            return;
        }

        ProtectedFolder.Ensure(_root);
        File.WriteAllText(Path.Combine(_root, "kept.txt"), "x");

        ProtectedFolder.Ensure(_root);

        Assert.True(File.Exists(Path.Combine(_root, "kept.txt")));
    }

    [WindowsFact]
    public void ALooseFolderOfAnAdministratorIsTightened()
    {
        if (!ProcessElevation.IsElevated())
        {
            return;
        }

        var info = Directory.CreateDirectory(_root);
        var loose = info.GetAccessControl();
        loose.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(loose);

        ProtectedFolder.Ensure(_root);

        var rules = new DirectoryInfo(_root).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
        Assert.DoesNotContain(rules, r => r.IdentityReference.Equals(Users));
    }

    [WindowsFact]
    public void AUsersReadRuleIsKeptOnTheToolFolder()
    {
        if (!ProcessElevation.IsElevated())
        {
            return;
        }

        ProtectedFolder.Ensure(_root, usersMayRead: true);

        var rules = new DirectoryInfo(_root).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
        var users = Assert.Single(rules, r => r.IdentityReference.Equals(Users));
        Assert.Equal(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, users.FileSystemRights);
    }

    [WindowsFact]
    public void AFolderThatCannotBeTrusted_IsReportedWithItsPath()
    {
        if (!ProcessElevation.IsElevated())
        {
            return;
        }

        var info = Directory.CreateDirectory(_root);
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, Path.GetTempPath());

        var error = Assert.Throws<BootrixException>(() => ProtectedFolder.Ensure(link));

        Assert.Equal(ErrorCode.WorkspaceUntrusted, error.Code);
        Assert.Equal(link, error.Arguments[0]);
        Assert.True(info.Exists);
    }
}
