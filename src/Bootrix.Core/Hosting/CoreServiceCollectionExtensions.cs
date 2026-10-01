// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Jobs;
using Bootrix.Core.Localization;
using Bootrix.Core.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Hosting;

public static class CoreServiceCollectionExtensions
{
    public static IServiceCollection AddBootrixCore(this IServiceCollection services, BootrixPaths paths)
    {
        services.AddSingleton(paths);
        services.AddSingleton(Localizer.Default);
        services.AddSingleton<JobRunner>();
        services.AddSingleton(_ => new JobJournal(paths.JournalDirectory));
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new FileLoggerProvider(paths.LogDirectory));
        });

        return services;
    }
}
