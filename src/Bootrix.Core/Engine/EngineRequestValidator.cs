// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;

namespace Bootrix.Core.Engine;

/// <summary>
/// Checks a request before the elevated process acts on it. The unprivileged side is not trusted:
/// whatever it sends is checked for plausibility here, however carefully the GUI built it. The
/// messages name the field that is wrong and never repeat its value.
/// </summary>
public static class EngineRequestValidator
{
    private const int MaxTargets = 64;

    // One entry per kind of request. A request type without an entry is refused.
    private static readonly Dictionary<Type, Action<EngineJobRequest, List<string>>> Validators = new()
    {
        [typeof(RawWriteJobRequest)] = (request, problems) => ValidateRawWrite((RawWriteJobRequest)request, problems),
        [typeof(WriteImageJobRequest)] = (request, problems) => ValidateWriteImage((WriteImageJobRequest)request, problems),
    };

    public static IReadOnlyList<string> Validate(EngineJobRequest? request)
    {
        var problems = new List<string>();
        if (request is null)
        {
            problems.Add("request is missing");
        }
        else if (Validators.TryGetValue(request.GetType(), out var validator))
        {
            validator(request, problems);
        }
        else
        {
            problems.Add("request type is not supported");
        }

        return problems;
    }

    /// <exception cref="BootrixException">With <see cref="ErrorCode.InvalidSpec"/> when the request is not acceptable.</exception>
    public static void EnsureValid(EngineJobRequest? request)
    {
        var problems = Validate(request);
        if (problems.Count > 0)
        {
            throw new BootrixException(ErrorCode.InvalidSpec, string.Join("; ", problems)) { Arguments = [problems[0]] };
        }
    }

    private static void ValidateRawWrite(RawWriteJobRequest request, List<string> problems)
    {
        if (!EnginePathRules.IsAbsoluteFilePath(request.ImagePath))
        {
            problems.Add("image path is not an absolute file path");
        }

        ValidateTargets(request.Targets, problems);
    }

    private static void ValidateWriteImage(WriteImageJobRequest request, List<string> problems)
    {
        if (!EnginePathRules.IsAbsoluteFilePath(request.ImagePath))
        {
            problems.Add("image path is not an absolute file path");
        }

        ValidateTargets(request.Targets, problems);

        if (request.Spec is null)
        {
            problems.Add("job options are missing");
            return;
        }

        if (request.Spec.Windows?.DriverFolders?.Any(folder => !EnginePathRules.IsAbsoluteFilePath(folder)) == true)
        {
            problems.Add("a driver folder is not an absolute path");
        }

        if (request.Spec.Target?.Label is { Length: > 32 })
        {
            problems.Add("volume label is longer than 32 characters");
        }

        if (request.LocalAccountPassword is { Length: > 127 })
        {
            problems.Add("local account password is too long");
        }
    }

    private static void ValidateTargets(IReadOnlyList<EngineTarget>? targets, List<string> problems)
    {
        if (targets is null || targets.Count == 0)
        {
            problems.Add("no target disk");
            return;
        }

        if (targets.Count > MaxTargets)
        {
            problems.Add($"more than {MaxTargets} target disks");
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            if (target is null || !EnginePathRules.IsDeviceInterfacePath(target.DevicePath, EnginePathRules.DiskInterfaceGuid))
            {
                problems.Add("target is not a disk device path");
                continue;
            }

            if (target.Identity is null || !string.Equals(target.Identity.DevicePath, target.DevicePath, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add("target identity belongs to another device");
            }

            if (!seen.Add(target.DevicePath))
            {
                problems.Add("target disk is listed twice");
            }
        }
    }
}
