// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using Bootrix.App.Services;
using Bootrix.Core.Errors;
using Bootrix.Core.Localization;
using Bootrix.Core.Profiles;
using Bootrix.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bootrix.App.ViewModels;

public sealed record ProfileItem(ProfileEntry Entry, string Display);

/// <summary>Choosing, saving and deleting profiles for the settings on the Write page.</summary>
public sealed partial class ProfilesViewModel : ObservableObject
{
    private readonly ProfileStore _store;
    private readonly WriteOptionsViewModel _options;
    private readonly IDialogService _dialogs;
    private readonly Localizer _localizer;
    private readonly ILogger<ProfilesViewModel> _logger;
    private bool _loading;

    public ProfilesViewModel(
        ProfileStore store,
        WriteOptionsViewModel options,
        IDialogService dialogs,
        SettingsStore settings,
        Localizer localizer,
        ILogger<ProfilesViewModel> logger)
    {
        _store = store;
        _options = options;
        _dialogs = dialogs;
        _localizer = localizer;
        _logger = logger;

        Reload();
        localizer.CultureChanged += (_, _) => Reload();
        settings.Changed += (_, _) => Reload();
    }

    public ObservableCollection<ProfileItem> Items { get; } = [];

    /// <summary>Supplies the "read back" choice that is saved with a profile; it lives on the page, not in the options.</summary>
    public Func<bool> Verify { get; set; } = () => true;

    /// <summary>Raised after a profile was taken over into the form.</summary>
    public event EventHandler<JobSpec>? Applied;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private ProfileItem? _selected;

    [ObservableProperty]
    private string _info = "";

    [ObservableProperty]
    private bool _hasInfo;

    partial void OnSelectedChanged(ProfileItem? value)
    {
        if (_loading)
        {
            return;
        }

        if (value is null || value.Entry.Name.Length == 0)
        {
            _options.ActiveProfile = null;
            Show("");
            return;
        }

        try
        {
            var resolved = _store.Resolve(value.Entry.Name);
            _options.ApplySpec(resolved.Spec);
            _options.ActiveProfile = resolved;
            Applied?.Invoke(this, resolved.Spec);
            Show(resolved.LockedPaths.Count > 0 ? _localizer.Get("Profile.Locks", resolved.LockedPaths.Count) : "");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Profile {Name} could not be applied", value.Entry.Name);
            _options.ActiveProfile = null;
            var description = ErrorCatalog.Describe(ex, _localizer);
            Show($"{description.Cause} {description.Action}");
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var name = await _dialogs.PromptAsync(
            _localizer.Get("Profile.Prompt.Title"),
            _localizer.Get("Profile.Prompt.Text"),
            Selected?.Entry.Name ?? "",
            _localizer.Get("Profile.Prompt.Button"));
        if (name is null)
        {
            return;
        }

        try
        {
            _store.Save(name, _options.ToSpec(Verify()));
            Reload(select: ProfileStore.ValidateName(name));
        }
        catch (Exception ex)
        {
            var description = ErrorCatalog.Describe(ex, _localizer);
            Show($"{description.Cause} {description.Action}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        if (Selected is not { } item)
        {
            return;
        }

        _store.Delete(item.Entry.Name);
        Reload();
    }

    private bool CanDelete() => Selected is { Entry: { Origin: ProfileOrigin.User, Name.Length: > 0 } };

    private void Reload(string? select = null)
    {
        _loading = true;
        try
        {
            var keep = select ?? Selected?.Entry.Name;
            Items.Clear();
            Items.Add(new ProfileItem(new ProfileEntry("", ProfileOrigin.User, null, false), _localizer.Get("Profile.None")));
            foreach (var entry in _store.List())
            {
                Items.Add(new ProfileItem(entry, entry.Origin == ProfileOrigin.Team ? $"{entry.Name} ({_localizer.Get("Profile.Team")})" : entry.Name));
            }

            Selected = Items.FirstOrDefault(i => i.Entry.Name.Length > 0 && string.Equals(i.Entry.Name, keep, StringComparison.OrdinalIgnoreCase)) ?? Items[0];
        }
        finally
        {
            _loading = false;
        }
    }

    private void Show(string text)
    {
        Info = text;
        HasInfo = text.Length > 0;
    }
}
