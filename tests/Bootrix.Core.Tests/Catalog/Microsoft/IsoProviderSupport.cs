// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using Bootrix.Core.Catalog;
using Bootrix.Core.Catalog.Microsoft;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.Catalog.Microsoft;

internal static class IsoProviderSupport
{
    public static readonly DateTimeOffset Now = new(2026, 10, 1, 7, 0, 0, TimeSpan.Zero);

    public static MicrosoftIsoProvider Provider(FakeMicrosoftServer server) =>
        new(server.Client(), NullLogger<MicrosoftIsoProvider>.Instance, new FakeTimeProvider(Now));

    public static async Task<CatalogVariant> German(MicrosoftIsoProvider provider, string product = "windows11") =>
        Assert.Single(await provider.ListVariantsAsync(product, CancellationToken.None), v => v.Language == "de-DE");

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status);
}
