// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Shell;

namespace Bootrix.App.Services;

/// <summary>Shows the progress of the running job on the taskbar button, so a long write can be watched from another window.</summary>
public sealed class TaskbarProgress
{
    private TaskbarItemInfo? _info;

    public void Attach(TaskbarItemInfo info) => _info = info;

    public void Set(double fraction) => Update(info =>
    {
        info.ProgressState = TaskbarItemProgressState.Normal;
        info.ProgressValue = Math.Clamp(fraction, 0, 1);
    });

    public void Failed() => Update(info =>
    {
        info.ProgressState = TaskbarItemProgressState.Error;
        info.ProgressValue = 1;
    });

    public void Clear() => Update(info => info.ProgressState = TaskbarItemProgressState.None);

    private void Update(Action<TaskbarItemInfo> change)
    {
        if (_info is null)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            change(_info);
        }
        else
        {
            dispatcher.InvokeAsync(() => change(_info));
        }
    }
}
