// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Bootrix.Core.Errors;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;

namespace Bootrix.Windows.Optical;

/// <summary>
/// Gives IMAPI a read-only IStream over a .NET stream: for images that are not a plain file (a decoded DMG,
/// a BIN/CUE converted on the fly, a file padded to whole sectors) and for the boot images of a folder disc.
/// IMAPI calls it from its own threads and one request at a time, so there is no locking. The stream is not
/// closed here; whoever created it ends it once IMAPI has let go. A failing call throws an IOException that
/// carries the STG_E_* code, which the runtime hands to the caller as the HRESULT.
/// </summary>
[ComVisible(true)]
internal sealed unsafe class ManagedStream(Stream inner) : IStream
{
    private const int StgEInvalidFunction = unchecked((int)0x80030001);
    private const int StgEReadFault = unchecked((int)0x8003001E);
    private const int StgEAccessDenied = unchecked((int)0x80030005);
    private const uint StgTypeStream = 2;

    private static readonly HRESULT Ok = new(0);

    HRESULT ISequentialStream.Read(void* pv, uint cb, uint* pcbRead) => ReadCore(pv, cb, pcbRead);

    HRESULT IStream.Read(void* pv, uint cb, uint* pcbRead) => ReadCore(pv, cb, pcbRead);

    HRESULT ISequentialStream.Write(void* pv, uint cb, uint* pcbWritten) => new(StgEAccessDenied);

    HRESULT IStream.Write(void* pv, uint cb, uint* pcbWritten) => new(StgEAccessDenied);

    public void Seek(long dlibMove, SeekOrigin dwOrigin, ulong* plibNewPosition)
    {
        var position = inner.Seek(dlibMove, dwOrigin);
        if (plibNewPosition is not null)
        {
            *plibNewPosition = (ulong)position;
        }
    }

    public void SetSize(ulong libNewSize) => throw new IOException("read-only stream", StgEAccessDenied);

    public void CopyTo(IStream pstm, ulong cb, ulong* pcbRead, ulong* pcbWritten) =>
        throw new IOException("CopyTo is not supported", StgEInvalidFunction);

    public void Commit(STGC grfCommitFlags)
    {
    }

    public void Revert()
    {
    }

    public void LockRegion(ulong libOffset, ulong cb, LOCKTYPE dwLockType) =>
        throw new IOException("locking is not supported", StgEInvalidFunction);

    public void UnlockRegion(ulong libOffset, ulong cb, uint dwLockType) =>
        throw new IOException("locking is not supported", StgEInvalidFunction);

    public void Stat(STATSTG* pstatstg, STATFLAG grfStatFlag)
    {
        // Only the size and type matter to IMAPI; the name is left out, which is what STATFLAG_NONAME asks for anyway.
        *pstatstg = default;
        pstatstg->type = StgTypeStream;
        pstatstg->cbSize = (ulong)inner.Length;
    }

    public void Clone(out IStream ppstm) => throw new IOException("Clone is not supported", StgEInvalidFunction);

    private HRESULT ReadCore(void* pv, uint cb, uint* pcbRead)
    {
        try
        {
            var read = 0;
            var target = new Span<byte>(pv, checked((int)cb));

            // A stream may return less than asked before its end; IMAPI treats a short read as the end, so fill the buffer.
            while (read < target.Length)
            {
                var n = inner.Read(target[read..]);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            if (pcbRead is not null)
            {
                *pcbRead = (uint)read;
            }

            return Ok;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException or BootrixException)
        {
            if (pcbRead is not null)
            {
                *pcbRead = 0;
            }

            return new HRESULT(StgEReadFault);
        }
    }
}
