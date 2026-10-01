// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Optical.Reading;

namespace Bootrix.Core.Tests.Optical.Support;

internal sealed class MemoryCheckpointStore : IRipCheckpointStore
{
    public RipCheckpoint? Current { get; set; }

    public int Saves { get; private set; }

    public RipCheckpoint? Load() => Current;

    public void Save(RipCheckpoint checkpoint)
    {
        Current = checkpoint;
        Saves++;
    }

    public void Clear() => Current = null;
}
