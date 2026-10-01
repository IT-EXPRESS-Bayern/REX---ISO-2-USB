// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using Bootrix.Core.Engine;
using Bootrix.Core.Errors;
using Bootrix.Core.Localization;
using Bootrix.Core.Presentation;
using Bootrix.Core.Settings;
using Bootrix.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bootrix.App.ViewModels;

/// <summary>The disks the user can choose from, kept current while the page is open.</summary>
public sealed partial class DeviceListViewModel : ObservableObject, IDisposable
{
    private readonly IEngine _engine;
    private readonly SettingsStore _settings;
    private readonly Localizer _localizer;
    private readonly ILogger<DeviceListViewModel> _logger;
    private int _refreshRunning;

    public DeviceListViewModel(IEngine engine, SettingsStore settings, Localizer localizer, ILogger<DeviceListViewModel> logger)
    {
        _engine = engine;
        _settings = settings;
        _localizer = localizer;
        _logger = logger;

        engine.DevicesChanged += OnExternalChange;
        settings.Changed += OnSettingsChanged;
        localizer.CultureChanged += OnExternalChange;
    }

    public ObservableCollection<DeviceItem> Items { get; } = [];

    /// <summary>Raised when the choice changes, including when a disk that was chosen disappears.</summary>
    public event EventHandler? SelectionChanged;

    [ObservableProperty]
    private bool _hasDevices;

    [ObservableProperty]
    private string _errorText = "";

    /// <summary>While a job runs the list stays as it is.</summary>
    [ObservableProperty]
    private bool _isLocked;

    public IReadOnlyList<DeviceItem> Selected => [.. Items.Where(item => item.IsSelected)];

    public string SelectedText => _localizer.Get("Write.Selected", Selected.Count);

    public void Dispose()
    {
        _engine.DevicesChanged -= OnExternalChange;
        _settings.Changed -= OnSettingsChanged;
        _localizer.CultureChanged -= OnExternalChange;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        // Change notifications of the disk watcher arrive in bursts; one refresh at a time is enough.
        if (IsLocked || Interlocked.Exchange(ref _refreshRunning, 1) == 1)
        {
            return;
        }

        try
        {
            var settings = _settings.Current;
            var filter = new DiskFilter
            {
                IncludeUsbHardDisks = settings.ShowUsbHardDisks,
                IncludeInternalDisks = settings.ServiceMode,
                IncludeBlocked = true,
            };

            var devices = await _engine.ListDisksAsync(filter, CancellationToken.None);
            var kept = Items.Where(item => item.IsSelected).Select(item => item.Device.DevicePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var item in Items)
            {
                item.PropertyChanged -= OnItemChanged;
            }

            Items.Clear();
            foreach (var device in devices)
            {
                var description = DeviceDescription.Describe(device, _localizer);
                var item = new DeviceItem(device, description) { IsSelected = description.Selectable && kept.Contains(device.DevicePath) };
                item.PropertyChanged += OnItemChanged;
                Items.Add(item);
            }

            HasDevices = Items.Count > 0;
            ErrorText = "";
            RaiseSelectionChanged();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Listing the disks failed");
            var description = ErrorCatalog.Describe(ex, _localizer);
            ErrorText = $"{description.Cause} {description.Action} ({description.Code})";
        }
        finally
        {
            Interlocked.Exchange(ref _refreshRunning, 0);
        }
    }

    private void OnExternalChange(object? sender, EventArgs e) => Application.Current.Dispatcher.InvokeAsync(() => RefreshAsync());

    private void OnSettingsChanged(object? sender, AppSettings e) => Application.Current.Dispatcher.InvokeAsync(() => RefreshAsync());

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceItem.IsSelected))
        {
            RaiseSelectionChanged();
        }
    }

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(SelectedText));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
}
