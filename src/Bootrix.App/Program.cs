// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Hosting;
using Bootrix.Windows.Broker;

namespace Bootrix.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // The same executable doubles as the elevated broker: no window, it only serves the disk engine.
        if (BrokerEntry.TryRun(args, out var exitCode, BootrixPaths.Detect(AppContext.BaseDirectory, perUser: true)))
        {
            return exitCode;
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
