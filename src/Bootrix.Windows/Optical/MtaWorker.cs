// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Windows.Optical;

/// <summary>
/// Runs IMAPI work on a thread of its own in the multithreaded apartment. The IMAPI classes are
/// registered as free-threaded or both, so they need no message pump, and a burn blocks inside
/// IDiscFormat2Data::Write for as long as it takes. On the UI thread (an STA) every call would go through
/// a proxy and Write would pump messages while it waits, letting the window re-enter code that is in the
/// middle of a burn. A short-lived thread per operation also keeps COM objects from outliving it.
/// </summary>
internal static class MtaWorker
{
    public static Task<T> RunAsync<T>(Func<T> work, string name)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (OperationCanceledException ex)
            {
                completion.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = name,
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return completion.Task;
    }

    public static Task RunAsync(Action work, string name) =>
        RunAsync<object?>(
            () =>
            {
                work();
                return null;
            },
            name);
}
