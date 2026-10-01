// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;

namespace Bootrix.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        new MainWindow().Show();
    }
}
