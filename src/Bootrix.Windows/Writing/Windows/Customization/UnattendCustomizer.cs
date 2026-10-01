// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images;
using Bootrix.Core.Model;
using Bootrix.Core.Unattend;
using Bootrix.Core.Writing.Windows.Customization;
using Microsoft.Extensions.Logging;

namespace Bootrix.Windows.Writing.Windows.Customization;

/// <summary>Writes the autounattend.xml that Setup reads from the root of the medium.</summary>
public sealed class UnattendCustomizer(ILogger<UnattendCustomizer> logger) : IWindowsMediaCustomizer
{
    public string Id => "unattend";

    public bool Applies(MediaWriteContext write)
    {
        ArgumentNullException.ThrowIfNull(write);
        return write.Image.Kind == ImageKind.WindowsSetup && AnswerFileOptionsFactory.IsRequested(write.Spec.Windows);
    }

    public Task ApplyAsync(WindowsMediaCustomization customization, IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(customization);
        ArgumentNullException.ThrowIfNull(progress);
        cancellationToken.ThrowIfCancellationRequested();

        var write = customization.Write;
        var windows = write.Spec.Windows;
        var options = AnswerFileOptionsFactory.Create(
            windows,
            new AnswerFileContext
            {
                Arch = customization.Arch,
                DeviceSerial = customization.Target.Device.Serial,
                TargetNumber = TargetNumber(write, customization.Target),
                LocalAccountPassword = write.LocalAccountPassword,
                Editions = [.. write.Inspection.Windows?.Editions ?? []],
            });

        // Built before the medium is touched: an invalid name fails here and not half way through the file.
        var content = UnattendBuilder.ToBytes(options);
        progress.Report(0.5);

        var result = AnswerFileWriter.Write(customization.MediaRoot, content, windows.ExistingAnswerFile);
        if (result.Outcome == AnswerFileOutcome.LeftAlone)
        {
            logger.LogWarning("The image has its own {File}; it was left alone and the options of the job are not written to it", AnswerFileWriter.FileName);
        }
        else
        {
            // Only what was configured is logged, never the values: the file may carry the password of the account.
            logger.LogInformation(
                "Wrote {File}: local account {Account}, password {Password}, computer name {Computer}, hardware check bypass {Bypass}, original kept {Original}",
                AnswerFileWriter.FileName,
                options.Windows.LocalAccountName is not null,
                options.LocalAccountPassword is not null,
                options.ComputerName is not null,
                HardwareCheckBypass.IsRequested(windows),
                result.OriginalBackup is not null);
        }

        progress.Report(1);
        return Task.CompletedTask;
    }

    private static int TargetNumber(MediaWriteContext write, MediaWriteTarget target)
    {
        for (var i = 0; i < write.Targets.Count; i++)
        {
            if (ReferenceEquals(write.Targets[i], target))
            {
                return i + 1;
            }
        }

        return 1;
    }
}
