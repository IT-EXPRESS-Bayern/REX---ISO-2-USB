// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Catalog;
using Bootrix.Core.Library;
using Bootrix.Core.Presentation;

namespace Bootrix.App.ViewModels;

public sealed record ProductItem(CatalogProduct Product, string FamilyName)
{
    public string Name => Product.Name;

    public string? Description => Product.Description;
}

public sealed record VariantItem(CatalogVariant Variant, VariantDescription Description)
{
    public string Title => Description.Title;

    public string Details => Description.Details;
}

public sealed record LibraryItem(LibraryEntry Entry, string Title, string Details);
