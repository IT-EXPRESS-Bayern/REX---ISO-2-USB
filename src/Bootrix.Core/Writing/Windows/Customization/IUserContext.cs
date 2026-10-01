// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Writing.Windows.Customization;

/// <summary>
/// The rights files of the user are read with. The job runs elevated, and a path that arrives from the
/// unprivileged side must only open if the user could have opened it. In the broker the implementation impersonates
/// the client; where the job runs with the user's own account (the command line tool) there is nothing to switch.
/// There is deliberately no fallback to the process's rights: when the user cannot be impersonated, access fails.
/// </summary>
public interface IUserContext
{
    /// <summary>Runs <paramref name="action"/>, including everything it awaits, with the rights of the user.</summary>
    Task<T> RunAsync<T>(Func<Task<T>> action);
}

/// <summary>For jobs that already run as the user.</summary>
public sealed class ProcessUserContext : IUserContext
{
    public Task<T> RunAsync<T>(Func<Task<T>> action) => action();
}

public static class UserContextExtensions
{
    /// <summary>Runs a synchronous operation with the rights of the user.</summary>
    public static Task<T> Run<T>(this IUserContext context, Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(action);
        return context.RunAsync(() => Task.FromResult(action()));
    }
}
