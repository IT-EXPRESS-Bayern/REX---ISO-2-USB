// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.App;

public static class Program
{
    [STAThread]
    public static int Main()
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
