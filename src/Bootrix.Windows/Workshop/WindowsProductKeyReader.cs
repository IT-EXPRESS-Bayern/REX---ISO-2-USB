// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Workshop.Capture;
using Bootrix.Core.Workshop.Licensing;
using Microsoft.Win32;

namespace Bootrix.Windows.Workshop;

/// <summary>Decodes the DigitalProductId of the installed Windows. Called only after the user opted in.</summary>
internal static class WindowsProductKeyReader
{
    private const string VersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    /// <summary>Returns null when the registry holds no product ID or it does not decode to a key.</summary>
    public static WindowsProductKeyInfo? Read()
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(VersionKey);
        if (key?.GetValue("DigitalProductId") is not byte[] blob || DigitalProductIdDecoder.Decode(blob) is not { } productKey)
        {
            return null;
        }

        return new WindowsProductKeyInfo { MaskedKey = ProductKeys.Mask(productKey), PlainKey = productKey };
    }
}
