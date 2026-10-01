// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Workshop;

/// <summary>
/// Marks a property that holds a secret in clear text: a product key, a Wi-Fi key or a recovery password. Log and report
/// writers must skip such properties unless the user explicitly asked for them; <see cref="WorkshopJson.Options"/> does so.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class SensitiveAttribute : Attribute
{
}
