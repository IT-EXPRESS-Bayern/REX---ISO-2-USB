// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Boot.Dos;
using Bootrix.Core.Errors;

namespace Bootrix.Windows.Writing.Dos;

/// <summary>Copies a DOS system disk onto a volume that Windows has mounted, addressed by its volume GUID path.</summary>
internal static class DosVolumeCopier
{
    public static void Copy(string volumeGuidPath, IReadOnlyList<DosFile> files, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var root = volumeGuidPath.TrimEnd('\\') + "\\";
        var total = Math.Max(1, files.Sum(file => (long)file.Content.Length));
        long done = 0;

        // The list order is kept because the old boot sectors expect the system files at the start of the data area. The
        // attributes are set after writing; a read-only file could not be written.
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(root, file.Path.TrimStart('\\'));
            if (file.Folder.Length > 0)
            {
                Directory.CreateDirectory(Path.Combine(root, file.Folder.TrimStart('\\')));
            }

            File.WriteAllBytes(path, file.Content);
            File.SetAttributes(path, file.Attributes);

            var written = new FileInfo(path).Length;
            if (written != file.Content.Length)
            {
                throw new BootrixException(ErrorCode.VerifyMismatch, $"{file.Path} has {written} bytes, expected {file.Content.Length}") { Arguments = [done] };
            }

            done += file.Content.Length;
            progress?.Report((double)done / total);
        }
    }
}
