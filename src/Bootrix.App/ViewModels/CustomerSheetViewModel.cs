// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bootrix.App.Services;
using Bootrix.Core.Engine;
using Bootrix.Core.Errors;
using Bootrix.Core.Jobs;
using Bootrix.Core.Json;
using Bootrix.Core.Localization;
using Bootrix.Core.Presentation;
using Bootrix.Core.Workshop.Capture;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bootrix.App.ViewModels;

/// <summary>Capturing the inventory of the PC the program runs on into an encrypted customer sheet, and reading such a sheet.</summary>
public sealed partial class CustomerSheetViewModel(
    ICustomerPcCapture capture,
    IEngine engine,
    IDialogService dialogs,
    Localizer localizer,
    ILogger<CustomerSheetViewModel> logger) : ObservableObject
{
    [ObservableProperty]
    private bool _programs = true;

    [ObservableProperty]
    private bool _drivers = true;

    [ObservableProperty]
    private bool _wlan = true;

    [ObservableProperty]
    private bool _wlanKeys;

    [ObservableProperty]
    private bool _bitLockerKeys;

    [ObservableProperty]
    private bool _productKey;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CaptureCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string _sheetText = "";

    public bool HasStatus => Status.Length > 0;

    public bool HasSheet => SheetText.Length > 0;

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    partial void OnSheetTextChanged(string value) => OnPropertyChanged(nameof(HasSheet));

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task CaptureAsync()
    {
        IsBusy = true;
        SheetText = "";
        Status = localizer.Get("Sheet.Working");
        try
        {
            var result = await TakeAsync();
            var password = await AskNewPasswordAsync();
            if (password is null)
            {
                Status = "";
                return;
            }

            var path = dialogs.PickSaveSheet(CustomerSheetView.SuggestedFileName(DateTimeOffset.Now));
            if (path is null)
            {
                Status = "";
                return;
            }

            await using (var file = File.Create(path))
            {
                CustomerSheetFile.Write(file, result, password);
            }

            SheetText = CustomerSheetView.ToText(result, localizer);
            Status = localizer.Get("Sheet.Saved", path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The customer sheet could not be made");
            Status = Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task OpenAsync()
    {
        if (dialogs.PickOpenSheet() is not { } path)
        {
            return;
        }

        var password = await dialogs.PromptPasswordAsync(localizer.Get("Sheet.Password.Title"), localizer.Get("Sheet.Password.Open"), localizer.Get("Sheet.Password.Button"));
        if (password is null)
        {
            return;
        }

        try
        {
            await using var file = File.OpenRead(path);
            SheetText = CustomerSheetView.ToText(CustomerSheetFile.Read(file, password), localizer);
            Status = "";
        }
        catch (Exception ex) when (ex is BootrixException or IOException or UnauthorizedAccessException)
        {
            SheetText = "";
            Status = Describe(ex);
        }
    }

    [RelayCommand]
    private void ClosePreview() => SheetText = "";

    private bool CanRun() => !IsBusy;

    private async Task<CustomerPcCapture> TakeAsync()
    {
        // Wi-Fi keys and BitLocker recovery passwords can only be read with administrator rights, which the window does not have.
        if (WlanKeys || BitLockerKeys)
        {
            var result = await engine.RunJobAsync(
                new CaptureCustomerPcJobRequest
                {
                    IncludeInstalledPrograms = Programs,
                    IncludeThirdPartyDrivers = Drivers,
                    ListWlanProfiles = Wlan,
                    IncludeWlanKeys = WlanKeys,
                    IncludeBitLockerRecoveryKeys = BitLockerKeys,
                    IncludeWindowsProductKey = ProductKey,
                },
                new Progress<ProgressReport>(_ => { }),
                CancellationToken.None);
            if (!result.Succeeded)
            {
                throw result.ToException() ?? new BootrixException(ErrorCode.Unknown, "capture failed");
            }

            return JsonSerializer.Deserialize<CustomerPcCapture>(result.ReportJson ?? "{}", CoreJson.Options)
                ?? throw new BootrixException(ErrorCode.Unknown, "empty capture");
        }

        return await capture.CaptureAsync(
            new CustomerPcCaptureOptions
            {
                IncludeInstalledPrograms = Programs,
                IncludeThirdPartyDrivers = Drivers,
                ListWlanProfiles = Wlan,
                IncludeWindowsProductKey = ProductKey,
            });
    }

    private async Task<string?> AskNewPasswordAsync()
    {
        var title = localizer.Get("Sheet.Password.Title");
        var button = localizer.Get("Sheet.Password.Button");
        var first = await dialogs.PromptPasswordAsync(title, localizer.Get("Sheet.Password.Text"), button);
        if (first is null)
        {
            return null;
        }

        var again = await dialogs.PromptPasswordAsync(title, localizer.Get("Sheet.Password.Again"), button);
        if (again is null)
        {
            return null;
        }

        if (first != again)
        {
            throw new BootrixException(ErrorCode.InvalidSpec, "passwords differ") { Arguments = [localizer.Get("Sheet.Password.Mismatch")] };
        }

        return first;
    }

    private string Describe(Exception exception)
    {
        var description = ErrorCatalog.Describe(exception, localizer);
        return $"{description.Cause} {description.Action}";
    }
}
