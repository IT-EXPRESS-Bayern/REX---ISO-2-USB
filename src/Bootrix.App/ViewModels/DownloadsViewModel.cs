// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using Bootrix.App.Services;
using Bootrix.App.Views;
using Bootrix.Core.Catalog;
using Bootrix.Core.Catalog.Microsoft;
using Bootrix.Core.Errors;
using Bootrix.Core.Hosting;
using Bootrix.Core.Library;
using Bootrix.Core.Localization;
using Bootrix.Core.Net;
using Bootrix.Core.Presentation;
using Bootrix.Core.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;

namespace Bootrix.App.ViewModels;

public sealed partial class DownloadsViewModel : ObservableObject, IDisposable
{
    private readonly CatalogService _catalog;
    private readonly SegmentedDownloader _downloader;
    private readonly ImageLibrary _library;
    private readonly Localizer _localizer;
    private readonly IDialogService _dialogs;
    private readonly WriteViewModel _write;
    private readonly PageNavigator _navigator;
    private readonly BootrixPaths _paths;
    private readonly ILogger<DownloadsViewModel> _logger;
    private CancellationTokenSource? _download;
    private CancellationTokenSource? _variantsLoad;
    private bool _loaded;

    public DownloadsViewModel(
        CatalogService catalog,
        SegmentedDownloader downloader,
        ImageLibrary library,
        Localizer localizer,
        IDialogService dialogs,
        WriteViewModel write,
        PageNavigator navigator,
        BootrixPaths paths,
        ILogger<DownloadsViewModel> logger)
    {
        _catalog = catalog;
        _downloader = downloader;
        _library = library;
        _localizer = localizer;
        _dialogs = dialogs;
        _write = write;
        _navigator = navigator;
        _paths = paths;
        _logger = logger;
        _statusText = localizer.Get("Dl.Loading");
    }

    public ObservableCollection<ProductItem> Products { get; } = [];

    public ObservableCollection<VariantItem> Variants { get; } = [];

    public ObservableCollection<string> Architectures { get; } = [];

    public ObservableCollection<LibraryItem> LibraryItems { get; } = [];

    [ObservableProperty]
    private ProductItem? _selectedProduct;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    [NotifyPropertyChangedFor(nameof(IsManual))]
    private VariantItem? _selectedVariant;

    [ObservableProperty]
    private string? _selectedArchitecture;

    [ObservableProperty]
    private string _statusText;

    [ObservableProperty]
    private bool _hasStatus = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private string _progressTitle = "";

    [ObservableProperty]
    private string _speedText = "";

    [ObservableProperty]
    private string _remainingText = "";

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private string _resultText = "";

    [ObservableProperty]
    private InfoBarSeverity _resultSeverity = InfoBarSeverity.Success;

    /// <summary>The address to show when a download needs the browser instead.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasManualUrl))]
    private string? _manualUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastImage))]
    private string? _lastImagePath;

    [ObservableProperty]
    private bool _hasLibraryItems;

    public bool IsIdle => !IsBusy;

    public bool HasLastImage => LastImagePath is not null;

    public bool HasManualUrl => ManualUrl is not null;

    public bool IsManual => SelectedVariant?.Description.IsManual == true;

    public async Task EnsureLoadedAsync()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        await ReloadCatalogAsync();
        await ReloadLibraryAsync();
    }

    [RelayCommand]
    private async Task ReloadCatalogAsync()
    {
        SetStatus(_localizer.Get("Dl.Loading"));
        try
        {
            var listing = await _catalog.ListProductsAsync(CancellationToken.None);
            Products.Clear();
            foreach (var product in listing.Items)
            {
                Products.Add(new ProductItem(product, CatalogView.FamilyName(product.Family, _localizer)));
            }

            SetStatus(listing.Failures.Count == 0
                ? null
                : _localizer.Get("Dl.ProviderFailed", string.Join(", ", listing.Failures.Select(f => f.Provider))));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The catalog could not be loaded");
            SetStatus(ErrorCatalog.Describe(ex, _localizer).Cause);
        }
    }

    partial void OnSelectedProductChanged(ProductItem? value)
    {
        _ = LoadVariantsAsync(value);
    }

    partial void OnSelectedVariantChanged(VariantItem? value)
    {
        Architectures.Clear();
        if (value is null)
        {
            SelectedArchitecture = null;
            return;
        }

        foreach (var architecture in value.Variant.Architectures)
        {
            Architectures.Add(architecture);
        }

        SelectedArchitecture = Architectures.Contains("x64") ? "x64" : Architectures.FirstOrDefault();
    }

    private async Task LoadVariantsAsync(ProductItem? product)
    {
        var previous = _variantsLoad;
        var cts = _variantsLoad = new CancellationTokenSource();
        previous?.Cancel();
        previous?.Dispose();

        Variants.Clear();
        SelectedVariant = null;
        HasResult = false;
        if (product is null)
        {
            return;
        }

        try
        {
            SetStatus(_localizer.Get("Dl.Loading"));
            var variants = await _catalog.ListVariantsAsync(product.Product, cts.Token);
            foreach (var variant in variants)
            {
                Variants.Add(new VariantItem(variant, CatalogView.Describe(variant, _localizer)));
            }

            SetStatus(variants.Count == 0 ? _localizer.Get("Dl.NoVariants") : null);
            SelectedVariant = Variants.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The variants of {Product} could not be loaded", product.Product.Id);
            SetStatus(ErrorCatalog.Describe(ex, _localizer).Cause);
        }
    }

    private bool CanDownload() => !IsBusy && SelectedVariant is { Description.IsManual: false };

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync()
    {
        var item = SelectedVariant!;
        var product = SelectedProduct!;
        HasResult = false;
        ManualUrl = null;
        IsBusy = true;
        Percent = 0;
        ProgressTitle = _localizer.Get("Dl.Phase.Connecting");
        SpeedText = RemainingText = "";

        using var cts = new CancellationTokenSource();
        _download = cts;
        try
        {
            var request = await _catalog.ResolveAsync(item.Variant, SelectedArchitecture, cts.Token);
            var fileName = CatalogView.FileNameFor(request.Url);
            var destination = Path.Combine(_paths.CacheDirectory, "downloads", fileName);

            var result = await _downloader.DownloadAsync(request, destination, new Progress<DownloadProgress>(OnProgress), cts.Token);

            var added = await _library.AddAsync(
                result.Path,
                new LibraryImageInfo
                {
                    Name = item.Variant.Name,
                    Version = item.Variant.Version,
                    CatalogId = product.Product.Id,
                    VariantId = item.Variant.Id,
                    Architecture = SelectedArchitecture,
                    Language = item.Variant.Language,
                    FileName = fileName,
                    Source = request.Url.GetLeftPart(UriPartial.Path),
                },
                LibraryAddMode.Move,
                result.Sha256,
                cts.Token);

            LastImagePath = added.Entry.Path;
            ShowResult(InfoBarSeverity.Success, _localizer.Get("Dl.Done", ByteSize.Format(result.Length, _localizer.Culture)));
            await ReloadLibraryAsync();
        }
        catch (OperationCanceledException)
        {
            ShowResult(InfoBarSeverity.Warning, _localizer.Get("Write.Canceled"));
        }
        catch (MicrosoftDownloadBlockedException ex)
        {
            ManualUrl = ex.ManualUrl?.AbsoluteUri ?? product.Product.Homepage;
            ShowResult(InfoBarSeverity.Warning, _localizer.Get("Dl.Blocked"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Download of {Variant} failed", item.Variant.Id);
            var description = ErrorCatalog.Describe(ex, _localizer);
            ShowResult(InfoBarSeverity.Error, $"{description.Cause} {description.Action} ({description.Code})");
        }
        finally
        {
            _download = null;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CancelDownload() => _download?.Cancel();

    [RelayCommand]
    private void OpenVendorPage()
    {
        var url = SelectedVariant?.Variant.ManualUrl ?? ManualUrl ?? SelectedProduct?.Product.Homepage;
        if (url is not null)
        {
            _dialogs.OpenUrl(url);
        }
    }

    [RelayCommand]
    private void UseLastImage()
    {
        if (LastImagePath is { } path)
        {
            UseImage(path);
        }
    }

    [RelayCommand]
    private void UseLibraryItem(LibraryItem? item)
    {
        if (item is not null)
        {
            UseImage(item.Entry.Path);
            _ = _library.TouchAsync(item.Entry.Sha256, CancellationToken.None);
        }
    }

    [RelayCommand]
    private async Task RemoveLibraryItemAsync(LibraryItem? item)
    {
        if (item is null
            || !await _dialogs.ConfirmAsync(_localizer.Get("Dl.RemoveTitle"), _localizer.Get("Dl.RemoveBody", Path.GetFileName(item.Entry.Path)), _localizer.Get("Dl.RemoveOk")))
        {
            return;
        }

        await _library.RemoveAsync(item.Entry.Sha256, CancellationToken.None);
        await ReloadLibraryAsync();
    }

    public async Task ReloadLibraryAsync()
    {
        try
        {
            var listing = await _library.ListAsync(CancellationToken.None);
            LibraryItems.Clear();
            foreach (var entry in listing.Entries.OrderByDescending(e => e.LastUsedUtc))
            {
                var details = string.Join("  ·  ", new[] { entry.Info.Version, entry.Info.Architecture, ByteSize.Format(entry.Size, _localizer.Culture) }.Where(d => !string.IsNullOrWhiteSpace(d)));
                LibraryItems.Add(new LibraryItem(entry, entry.Info.Name ?? Path.GetFileName(entry.Path), details));
            }

            HasLibraryItems = LibraryItems.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The image library could not be read");
        }
    }

    public void Dispose()
    {
        _download?.Cancel();
        _variantsLoad?.Cancel();
        _variantsLoad?.Dispose();
    }

    private void UseImage(string path)
    {
        _write.SetImage(path);
        _navigator.Navigate<WritePage>();
    }

    private void OnProgress(DownloadProgress progress)
    {
        ProgressTitle = _localizer.Get("Dl.Phase." + progress.Phase);
        Percent = progress.BytesTotal is > 0 ? Math.Round(progress.BytesDone * 100.0 / progress.BytesTotal.Value, 1) : 0;
        SpeedText = progress.BytesPerSecond > 0
            ? $"{ByteSize.FormatRate(progress.BytesPerSecond, _localizer.Culture)}  ·  {_localizer.Get("Dl.Connections", progress.ActiveSegments)}"
            : "";
        RemainingText = ByteSize.FormatDuration(progress.Eta);
    }

    private void SetStatus(string? text)
    {
        StatusText = text ?? "";
        HasStatus = !string.IsNullOrEmpty(text);
    }

    private void ShowResult(InfoBarSeverity severity, string text)
    {
        ResultSeverity = severity;
        ResultText = text;
        HasResult = true;
    }
}
