// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;

namespace Bootrix.Windows.Interop;

internal static partial class WinTrust
{
    public static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public const uint UiNone = 2;
    public const uint RevokeNone = 0;
    public const uint ChoiceFile = 1;
    public const uint StateVerify = 1;
    public const uint StateClose = 2;
    public const uint ProvFlagsRevocationCheckNone = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    public struct FileInfo
    {
        public uint Size;
        public nint FilePath;
        public nint File;
        public nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Data
    {
        public uint Size;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public nint FileInfo;
        public uint StateAction;
        public nint StateData;
        public nint UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }

    [LibraryImport("wintrust.dll", EntryPoint = "WinVerifyTrust")]
    public static partial int WinVerifyTrust(nint window, ref Guid action, ref Data data);
}
