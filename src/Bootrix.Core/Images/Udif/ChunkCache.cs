// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics.CodeAnalysis;

namespace Bootrix.Core.Images.Udif;

/// <summary>Least-recently-used cache of decoded chunks with a byte budget. Not thread-safe.</summary>
internal sealed class ChunkCache(long budgetBytes)
{
    private readonly Dictionary<int, LinkedListNode<(int Index, byte[] Data)>> _entries = [];
    private readonly LinkedList<(int Index, byte[] Data)> _order = new();

    public long Bytes { get; private set; }

    public bool Contains(int index) => _entries.ContainsKey(index);

    public bool TryGet(int index, [NotNullWhen(true)] out byte[]? data)
    {
        if (!_entries.TryGetValue(index, out var node))
        {
            data = null;
            return false;
        }

        _order.Remove(node);
        _order.AddFirst(node);
        data = node.Value.Data;
        return true;
    }

    public void Add(int index, byte[] data)
    {
        if (_entries.Remove(index, out var existing))
        {
            _order.Remove(existing);
            Bytes -= existing.Value.Data.Length;
        }

        _entries[index] = _order.AddFirst((index, data));
        Bytes += data.Length;

        // The newest entry always stays, even if it alone exceeds the budget.
        while (Bytes > budgetBytes && _order.Count > 1)
        {
            var oldest = _order.Last!;
            _order.RemoveLast();
            _entries.Remove(oldest.Value.Index);
            Bytes -= oldest.Value.Data.Length;
        }
    }
}
