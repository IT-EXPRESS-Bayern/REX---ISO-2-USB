// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.App.Services;

/// <summary>Lets a view model switch pages without knowing the window. The main window plugs itself in when it is created.</summary>
public sealed class PageNavigator
{
    private Func<Type, bool>? _navigate;

    public void Attach(Func<Type, bool> navigate) => _navigate = navigate;

    public bool Navigate<TPage>() => _navigate?.Invoke(typeof(TPage)) ?? false;
}
