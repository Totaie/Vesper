using System.Security.Cryptography;
using Vesper.Core.Security;

namespace Vesper.Core.Rfb;

/// <summary>
/// Serialises client→server messages onto the transport from one writer task. Every message is
/// sent in order. Pointer moves that haven't left yet are coalesced, so a slow link sends the
/// latest position instead of a backlog. Under Apple's record layer each message is framed as its
/// own record(s), since the CBC chain has to be advanced in send order.
/// </summary>
internal sealed class OutgoingQueue : IDisposable
{
    private readonly Stream _stream;
    private readonly RecordWriter? _records;
    private readonly object _lock = new();
    private readonly List<byte[]> _pending = [];
    private readonly SemaphoreSlim _signal = new(0);
    private int _pointerIndex = -1;
    private byte _pointerButtons;
    private bool _closed;

    public OutgoingQueue(Stream stream, RecordWriter? records)
    {
        _stream = stream;
        _records = records;
    }

    public long BytesSent { get; private set; }

    public void Enqueue(byte[] message)
    {
        lock (_lock)
        {
            if (_closed) return;
            _pending.Add(message);
            _pointerIndex = -1;
        }
        _signal.Release();
    }

    /// <summary>Queues a PointerEvent, replacing a not-yet-sent move with the same buttons.</summary>
    public void EnqueuePointer(byte buttons, ushort x, ushort y)
    {
        lock (_lock)
        {
            if (_closed) return;
            if (_pointerIndex >= 0 && _pointerIndex == _pending.Count - 1 && _pointerButtons == buttons)
            {
                var m = _pending[_pointerIndex];
                m[2] = (byte)(x >> 8); m[3] = (byte)x;
                m[4] = (byte)(y >> 8); m[5] = (byte)y;
                return;
            }
            _pending.Add([5, buttons, (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y]);
            _pointerIndex = _pending.Count - 1;
            _pointerButtons = buttons;
        }
        _signal.Release();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var batch = new List<byte[]>();
        using var buffer = new MemoryStream(64 * 1024);
        while (true)
        {
            await _signal.WaitAsync(ct).ConfigureAwait(false);
            lock (_lock)
            {
                batch.AddRange(_pending);
                _pending.Clear();
                _pointerIndex = -1;
            }
            if (batch.Count == 0) continue;

            buffer.SetLength(0);
            foreach (var message in batch)
            {
                if (_records is null)
                {
                    buffer.Write(message);
                }
                else
                {
                    buffer.Write(_records.Frame(message).Span);
                }
                // Key events are what someone types on the Mac, passwords included: don't leave
                // them lying around in memory longer than it takes to send them.
                CryptographicOperations.ZeroMemory(message);
            }
            batch.Clear();
            int length = (int)buffer.Length;
            await _stream.WriteAsync(buffer.GetBuffer().AsMemory(0, length), ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(buffer.GetBuffer().AsSpan(0, length));
            BytesSent += length;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _closed = true;
            _pending.Clear();
        }
        _records?.Dispose();
        _signal.Dispose();
    }
}
