// SPDX-License-Identifier: GPL-3.0-or-later
using System.Management;

namespace Bootrix.Windows.Workshop;

/// <summary>Reads WMI classes without leaking the COM-backed objects, with a time limit so a broken provider cannot hang the check.</summary>
internal static class Wmi
{
    public const string DefaultScope = @"root\CIMV2";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static List<T> Select<T>(string query, Func<ManagementObject, T> map, string scope = DefaultScope)
    {
        var options = new System.Management.EnumerationOptions { Timeout = Timeout, ReturnImmediately = false };
        using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(query), options);
        using var collection = searcher.Get();

        var result = new List<T>();
        foreach (var item in collection)
        {
            using var instance = (ManagementObject)item;
            result.Add(map(instance));
        }

        return result;
    }

    /// <summary>A property that is missing on some Windows versions reads as null instead of throwing.</summary>
    public static object? Get(ManagementBaseObject instance, string name)
    {
        try
        {
            return instance[name];
        }
        catch (ManagementException)
        {
            return null;
        }
    }

    public static string? GetString(ManagementBaseObject instance, string name) =>
        Get(instance, name) is string text && text.Length > 0 ? text : null;

    public static uint? GetUInt(ManagementBaseObject instance, string name) => Get(instance, name) switch
    {
        uint value => value,
        int value when value >= 0 => (uint)value,
        ushort value => value,
        byte value => value,
        _ => null,
    };

    public static bool? GetBool(ManagementBaseObject instance, string name) => Get(instance, name) as bool?;

    public static string[] GetStrings(ManagementBaseObject instance, string name) =>
        Get(instance, name) is string[] values ? values : [];
}
