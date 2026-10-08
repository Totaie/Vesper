using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Vesper.Core.Rfb;
using Vesper.Core.Security;

namespace Vesper.FakeMac;

public sealed record FakeMacOptions(
    string Username,
    string Password,
    RSA Identity,
    bool OfferRsa,
    bool OfferDiffieHellman,
    bool Rfb38,
    bool Verbose,
    bool RejectRsaLogins = false,
    bool Impersonate = false);

/// <summary>
/// The server side of one viewer connection, speaking Apple's RFB 003.889 the way the measured
/// references describe a Mac speaking it: extended ServerInit, the cleartext prelude, the rekey,
/// the record layer, display layout, cursor cache, pushed updates and the pasteboard.
/// </summary>
public sealed class FakeMacConnection : IAsyncDisposable
{
    // RFC 5054's 4096-bit group with generator 5, which macOS 26 uses for type 30.
    private static readonly byte[] DhPrime = Convert.FromHexString(
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DD" +
        "EF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED" +
        "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF0598DA48361C55D39A69163FA8FD24CF5F" +
        "83655D23DCA3AD961C62F356208552BB9ED529077096966D670C354E4ABC9804F1746C08CA18217C32905E462E36CE3B" +
        "E39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF6955817183995497CEA956AE515D2261898FA0510" +
        "15728E5A8AAAC42DAD33170D04507A33A85521ABDF1CBA64ECFB850458DBEF0A8AEA71575D060C7DB3970F85A6E1E4C7" +
        "ABF5AE8CDB0933D71E8C94E04A25619DCEE3D2261AD2EE6BF12FFA06D98A0864D87602733EC86A64521F2B18177B200C" +
        "BBE117577A615D6C770988C0BAD946E208E24FA074E5AB3143DB5BFCE0FD108E4B82D120A92108011A723C12A787E6D7" +
        "88719A10BDBA5B2699C327186AF4E23C1A946834B6150BDA2583E9CA2AD44CE8DBBBC2DB04DE8EF92E8EFC141FBECAA6" +
        "287C59474E6BC05D99B2964FA090C3A2233BA186515BE7ED1F612970CEE2D7AFB81BDD762170481CD0069127D5B05AA9" +
        "93B4EA988D8FDDC186FFB7DC90A6C08F4DF435C934063199FFFFFFFFFFFFFFFF");

    private readonly Stream _transport;
    private readonly Desktop _desktop;
    private readonly FakeMacOptions _options;
    private readonly string _peer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Encoders _encoders = new();
    private readonly MvsEncoder _mvs = new();
    private byte[]? _mvsView;
    private readonly object _dirtyLock = new();
    private readonly List<Rectangle> _dirty = [];
    private readonly SemaphoreSlim _wake = new(0);
    private Stream _input;
    private RecordWriter? _records;
    private bool _apple;
    private int[] _encodings = [];
    private bool _requestPending;
    private bool _fullRequested;
    private uint? _pushInterval;
    private long _lastUpdateEnd;
    private bool _metadataOwed = true;
    private bool _pasteboardWatched;
    private bool _overEditor;
    private readonly HashSet<uint> _cursorStored = [];
    private bool _command;
    private int _updates;
    private long _bytesOut;

    public FakeMacConnection(Stream transport, Desktop desktop, FakeMacOptions options, string peer)
    {
        _transport = transport;
        _input = transport;
        _desktop = desktop;
        _options = options;
        _peer = peer;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            byte[] wrapKey = await HandshakeAsync(linked.Token);
            if (_apple) await StartEncryptionAsync(wrapKey, linked.Token);
            if (_options.Impersonate)
            {
                // Without the real session keys there's nothing more it can do but wait.
                await Task.Delay(Timeout.Infinite, linked.Token);
            }
            _desktop.Changed += OnChanged;
            _desktop.Resized += OnResized;
            _desktop.SetStatus($"Viewer connected ({(_apple ? "Apple 003.889, encrypted" : "RFB 3.8")})");
            var sender = Task.Run(() => SendLoopAsync(linked.Token), linked.Token);
            var reader = Task.Run(() => ReadLoopAsync(linked.Token), linked.Token);
            var done = await Task.WhenAny(sender, reader);
            linked.Cancel();
            await done;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is EndOfStreamException or IOException or ConnectionClosedException)
        {
            Log("viewer disconnected");
        }
        finally
        {
            _desktop.Changed -= OnChanged;
            _desktop.Resized -= OnResized;
            _desktop.SetStatus("Viewer disconnected");
            Log($"session over: {_updates} updates, {_bytesOut / 1024.0 / 1024.0:0.0} MB sent");
        }
    }

    // ───────────────────────────── Handshake ─────────────────────────────

    private async Task<byte[]> HandshakeAsync(CancellationToken ct)
    {
        await WriteClearAsync(Encoding.ASCII.GetBytes(_options.Rfb38 ? "RFB 003.008\n" : "RFB 003.889\n"), ct);
        string clientVersion = Encoding.ASCII.GetString(await ReadExactAsync(12, ct));
        _apple = clientVersion == "RFB 003.889\n";
        Log($"viewer speaks {clientVersion.Trim()}");

        var types = new List<byte>();
        if (_options.OfferDiffieHellman) types.Add(30);
        if (_options.OfferRsa) types.Add(33);
        if (!_options.Rfb38) { types.Add(36); types.Add(35); }
        await WriteClearAsync([(byte)types.Count, .. types], ct);
        byte chosen = (await ReadExactAsync(1, ct))[0];
        Log($"viewer chose security type {chosen}");

        (bool ok, byte[] wrapKey) = chosen switch
        {
            33 when _options.OfferRsa => await RsaAsync(ct),
            30 when _options.OfferDiffieHellman => await DiffieHellmanAsync(ct),
            _ => throw new IOException($"viewer chose security type {chosen}, which wasn't offered"),
        };
        if (!ok)
        {
            Log("wrong user name or password");
            var fail = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(fail, 1);
            await WriteClearAsync(fail, ct);
            if (!_apple)
            {
                byte[] reason = Encoding.UTF8.GetBytes("Authentication failed");
                var len = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(len, (uint)reason.Length);
                await WriteClearAsync([.. len, .. reason], ct);
            }
            throw new IOException("authentication failed");
        }
        await WriteClearAsync([0, 0, 0, 0], ct);

        byte init = (await ReadExactAsync(1, ct))[0];
        bool extended = _apple && (init & 0x80) != 0;
        Log($"ClientInit 0x{init:X2}");

        byte[] name = Encoding.UTF8.GetBytes("FakeMac Studio");
        byte[] field = name;
        if (extended)
        {
            var bitmap = new byte[16];
            foreach (byte t in new byte[] { 0, 2, 3, 4, 5, 6, 8, 9, 0x0a, 0x0b, 0x0d, 0x12, 0x15, 0x17, 0x1f, 0x21 })
            {
                bitmap[t / 8] |= (byte)(0x80 >> (t % 8));
            }
            field = new byte[22 + name.Length];
            BinaryPrimitives.WriteUInt32BigEndian(field.AsSpan(2), 0x10 | 0x02 | (2u << 5));
            bitmap.CopyTo(field, 6);
            name.CopyTo(field, 22);
        }
        var si = new byte[24 + field.Length];
        lock (_desktop.Sync)
        {
            BinaryPrimitives.WriteUInt16BigEndian(si, (ushort)_desktop.Width);
            BinaryPrimitives.WriteUInt16BigEndian(si.AsSpan(2), (ushort)_desktop.Height);
        }
        PixelFormat.Bgrx32.Write(si.AsSpan(4));
        BinaryPrimitives.WriteUInt32BigEndian(si.AsSpan(20), (uint)field.Length);
        field.CopyTo(si, 24);
        await WriteClearAsync(si, ct);
        return wrapKey;
    }

    private async Task<(bool, byte[])> RsaAsync(CancellationToken ct)
    {
        uint requestLength = BinaryPrimitives.ReadUInt32BigEndian(await ReadExactAsync(4, ct));
        byte[] request = await ReadExactAsync((int)requestLength, ct);
        if (request.Length < 10 || Encoding.ASCII.GetString(request, 2, 4) != "RSA1")
        {
            throw new IOException("bad RSA key request");
        }
        byte[] der = _options.Identity.ExportRSAPublicKey();
        var reply = new byte[4 + 4 + 2 + der.Length + 1];
        BinaryPrimitives.WriteUInt32BigEndian(reply, (uint)(reply.Length - 4));
        reply[5] = 1; // "00 01 00 00"
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(8), (ushort)der.Length);
        der.CopyTo(reply, 10);
        await WriteClearAsync(reply, ct);

        uint blobLength = BinaryPrimitives.ReadUInt32BigEndian(await ReadExactAsync(4, ct));
        if (blobLength > 4096) throw new IOException("RSA login too large");
        byte[] blob = await ReadExactAsync((int)blobLength, ct);
        if (Encoding.ASCII.GetString(blob, 2, 4) != "RSA1" || BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(6)) != 1)
        {
            throw new IOException("bad RSA login envelope");
        }
        byte[] credentials = blob[8..136];
        int rsaLength = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(136));
        byte[] wrapped = blob[138..(138 + rsaLength)];
        if (_options.Impersonate)
        {
            // Someone replaying the Mac's public key can't decrypt the wrapped key. All it can do
            // is claim success and make up the session keys.
            Log("impersonating: the Mac's public key without its private key");
            await WriteClearAsync([0, 0, 0, 0], ct);
            return (true, RandomNumberGenerator.GetBytes(16));
        }
        byte[] aesKey = _options.Identity.Decrypt(wrapped, RSAEncryptionPadding.Pkcs1);
        await WriteClearAsync([0, 0, 0, 0], ct); // the counted acknowledgement, empty
        bool ok = CheckCredentials(aesKey, credentials);
        if (_options.RejectRsaLogins)
        {
            Log("refusing the RSA login (--reject-rsa), as a Mac that doesn't take this form would");
            ok = false;
        }
        return (ok, aesKey);
    }

    private async Task<(bool, byte[])> DiffieHellmanAsync(CancellationToken ct)
    {
        var p = new BigInteger(DhPrime, isUnsigned: true, isBigEndian: true);
        var g = new BigInteger(5);
        var b = new BigInteger(RandomNumberGenerator.GetBytes(64), isUnsigned: true, isBigEndian: true);
        var serverPublic = BigInteger.ModPow(g, b, p);
        int keyLength = DhPrime.Length;
        var head = new byte[4 + keyLength * 2];
        BinaryPrimitives.WriteUInt16BigEndian(head, 5);
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(2), (ushort)keyLength);
        DhPrime.CopyTo(head, 4);
        WritePadded(serverPublic, head.AsSpan(4 + keyLength, keyLength));
        await WriteClearAsync(head, ct);

        byte[] credentials = await ReadExactAsync(128, ct);
        byte[] clientPublic = await ReadExactAsync(keyLength, ct);
        var shared = BigInteger.ModPow(new BigInteger(clientPublic, isUnsigned: true, isBigEndian: true), b, p);
        var sharedBytes = new byte[keyLength];
        WritePadded(shared, sharedBytes);
        byte[] key = MD5.HashData(sharedBytes);
        return (CheckCredentials(key, credentials), key);
    }

    private bool CheckCredentials(byte[] aesKey, byte[] encrypted)
    {
        using var aes = Aes.Create();
        aes.Key = aesKey;
        byte[] block = aes.DecryptEcb(encrypted, PaddingMode.None);
        string user = Encoding.UTF8.GetString(block, 0, Array.IndexOf(block, (byte)0, 0, 64) is var u and >= 0 ? u : 64);
        int passEnd = Array.IndexOf(block, (byte)0, 64, 64);
        string pass = Encoding.UTF8.GetString(block, 64, (passEnd >= 0 ? passEnd : 128) - 64);
        Log($"sign-in attempt as \"{user}\"");
        return user == _options.Username && pass == _options.Password;
    }

    private static void WritePadded(BigInteger value, Span<byte> destination)
    {
        destination.Clear();
        int count = value.GetByteCount(isUnsigned: true);
        value.TryWriteBytes(destination[(destination.Length - count)..], out _, isUnsigned: true, isBigEndian: true);
    }

    private async Task StartEncryptionAsync(byte[] wrapKey, CancellationToken ct)
    {
        // Read the cleartext prelude up to SetEncryption command 2.
        bool started = false;
        while (true)
        {
            byte type = (await ReadExactAsync(1, ct))[0];
            switch (type)
            {
                case 0x21: // ViewerInfo
                    byte[] head = await ReadExactAsync(3, ct);
                    int length = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(1));
                    await ReadExactAsync(length, ct);
                    Log($"ViewerInfo, {length + 4} bytes");
                    break;
                case 0x0a:
                    byte[] mode = await ReadExactAsync(3, ct);
                    Log($"SetMode {mode[2]}");
                    break;
                case 0x15:
                    byte[] auto = await ReadExactAsync(7, ct);
                    _pasteboardWatched = auto[2] == 1;
                    Log($"AutoPasteboard {(_pasteboardWatched ? "start" : "stop")}");
                    break;
                case 0x12:
                    byte[] cmd = await ReadExactAsync(3, ct);
                    int command = BinaryPrimitives.ReadUInt16BigEndian(cmd.AsSpan(1));
                    if (command == 1)
                    {
                        byte[] rest = await ReadExactAsync(4, ct);
                        int count = BinaryPrimitives.ReadUInt16BigEndian(rest.AsSpan(2));
                        await ReadExactAsync(count * 4, ct);
                        started = true;
                    }
                    else
                    {
                        await ReadExactAsync(4, ct);
                        if (!started) throw new IOException("SetEncryption 2 before 1");
                        // The rekey goes out in clear; everything after it is records.
                        byte[] key = RandomNumberGenerator.GetBytes(16), iv = RandomNumberGenerator.GetBytes(16);
                        using var aes = Aes.Create();
                        aes.Key = wrapKey;
                        var update = new byte[4 + 12 + 36];
                        BinaryPrimitives.WriteUInt16BigEndian(update.AsSpan(2), 1);
                        BinaryPrimitives.WriteInt32BigEndian(update.AsSpan(12), 0x44f);
                        BinaryPrimitives.WriteUInt32BigEndian(update.AsSpan(16), 1);
                        aes.EncryptEcb(key, PaddingMode.None).CopyTo(update, 20);
                        aes.EncryptEcb(iv, PaddingMode.None).CopyTo(update, 36);
                        await WriteClearAsync(update, ct);
                        _records = new RecordWriter(key, iv);
                        _input = new RecordReaderStream(_transport, key, iv, []);
                        Log("record layer up (AES-128-CBC + SHA-1)");
                        if (_options.Impersonate)
                        {
                            // An impersonator would start talking at once, hoping to be believed.
                            await SendAsync([2], ct);
                        }
                        return;
                    }
                    break;
                default:
                    throw new IOException($"unexpected message {type} in the cleartext prelude");
            }
        }
    }

    // ───────────────────────────── Session ─────────────────────────────

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        while (true)
        {
            byte type = (await ReadExactAsync(1, ct))[0];
            switch (type)
            {
                case 0: // SetPixelFormat
                    await ReadExactAsync(19, ct);
                    break;
                case 2: // SetEncodings
                    byte[] h = await ReadExactAsync(3, ct);
                    int n = BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(1));
                    byte[] list = await ReadExactAsync(n * 4, ct);
                    _encodings = Enumerable.Range(0, n).Select(i => BinaryPrimitives.ReadInt32BigEndian(list.AsSpan(i * 4))).ToArray();
                    Log($"SetEncodings: {string.Join(", ", _encodings)} → sending {CodecName(PickCodec())}");
                    _metadataOwed = true;
                    Wake();
                    break;
                case 3: // FramebufferUpdateRequest
                    byte[] req = await ReadExactAsync(9, ct);
                    lock (_dirtyLock)
                    {
                        _requestPending = true;
                        if (req[0] == 0) _fullRequested = true;
                    }
                    Wake();
                    break;
                case 4: // KeyEvent
                    byte[] key = await ReadExactAsync(7, ct);
                    uint keysym = BinaryPrimitives.ReadUInt32BigEndian(key.AsSpan(3));
                    bool down = key[0] != 0;
                    if (keysym is 0xFFEB or 0xFFEC or 0xFFE9 or 0xFFEA) _command = down;
                    if (down && _command && keysym is 'c' or 'C') CopyOnTheMac();
                    _desktop.Key(keysym, down);
                    break;
                case 5: // PointerEvent
                    byte[] ptr = await ReadExactAsync(5, ct);
                    int x = BinaryPrimitives.ReadUInt16BigEndian(ptr.AsSpan(1));
                    int y = BinaryPrimitives.ReadUInt16BigEndian(ptr.AsSpan(3));
                    _desktop.Pointer(x, y, ptr[0]);
                    UpdateCursorForPointer(x, y);
                    break;
                case 6: // ClientCutText
                    byte[] cut = await ReadExactAsync(7, ct);
                    int cutLength = BinaryPrimitives.ReadInt32BigEndian(cut.AsSpan(3));
                    _desktop.Clipboard(Encoding.Latin1.GetString(await ReadExactAsync(cutLength, ct)));
                    break;
                case 0x08: // SetServerScaling
                    byte[] sc = await ReadExactAsync(9, ct);
                    double scale = BinaryPrimitives.ReadDoubleBigEndian(sc.AsSpan(1));
                    Log($"SetServerScaling {scale:0.###}");
                    _desktop.SetScale(scale);
                    break;
                case 0x09: // AutoFrameBufferUpdate
                    byte[] auto = await ReadExactAsync(15, ct);
                    uint interval = BinaryPrimitives.ReadUInt32BigEndian(auto.AsSpan(3));
                    _pushInterval = interval == uint.MaxValue ? null : interval;
                    Log($"AutoFrameBufferUpdate every {interval} us");
                    Wake();
                    break;
                case 0x0a: await ReadExactAsync(3, ct); break; // SetMode
                case 0x0b: // pasteboard fetch
                    byte[] fetch = await ReadExactAsync(7, ct);
                    uint id = BinaryPrimitives.ReadUInt32BigEndian(fetch.AsSpan(3));
                    await SendPasteboardAsync(id, ct);
                    break;
                case 0x0d: await ReadExactAsync(7, ct); _metadataOwed = true; Wake(); break; // SetDisplay
                case 0x15: // AutoPasteboard
                    byte[] ap = await ReadExactAsync(7, ct);
                    _pasteboardWatched = ap[2] == 1;
                    break;
                case 0x17: // event message 2
                    byte[] eh = await ReadExactAsync(3, ct);
                    int eventLength = BinaryPrimitives.ReadUInt16BigEndian(eh.AsSpan(1));
                    byte[] ev = await ReadExactAsync(eventLength, ct);
                    if (eventLength >= 54 && BinaryPrimitives.ReadUInt16BigEndian(ev.AsSpan(2)) == 11)
                    {
                        int dx = BinaryPrimitives.ReadInt32BigEndian(ev.AsSpan(22));
                        int dy = BinaryPrimitives.ReadInt32BigEndian(ev.AsSpan(26));
                        _desktop.Scroll(-dx, -dy);
                    }
                    break;
                case 0x1f: // pasteboard data from the viewer
                    byte[] ph = await ReadExactAsync(15, ct);
                    uint uncompressed = BinaryPrimitives.ReadUInt32BigEndian(ph.AsSpan(7));
                    uint compressed = BinaryPrimitives.ReadUInt32BigEndian(ph.AsSpan(11));
                    byte[] archive = await ReadExactAsync((int)compressed, ct);
                    string? text = ApplePasteboard.ExtractText(archive, uncompressed);
                    _desktop.Clipboard(text ?? "(no text)");
                    Log($"viewer pasted {text?.Length ?? 0} characters onto the Mac's pasteboard");
                    break;
                case 0x21: // ViewerInfo
                    byte[] vh = await ReadExactAsync(3, ct);
                    await ReadExactAsync(BinaryPrimitives.ReadUInt16BigEndian(vh.AsSpan(1)), ct);
                    break;
                default:
                    throw new IOException($"unknown client message type {type}");
            }
        }
    }

    private void CopyOnTheMac()
    {
        // Command-C on the fake Mac copies a line of the editor, as a stand-in for a real copy.
        _macClipboard = $"Copied on FakeMac at {DateTime.Now:HH:mm:ss} — {Guid.NewGuid().ToString()[..8]}";
        Log("Mac pasteboard changed (Command-C)");
        if (_pasteboardWatched) _ = SendAsync([0x14, 0, 0, 4, 0, 1, 0, 2], CancellationToken.None);
    }

    private string _macClipboard = "Hello from the FakeMac pasteboard";

    private async Task SendPasteboardAsync(uint id, CancellationToken ct)
    {
        byte[] message = ApplePasteboard.BuildSendMessage(_macClipboard, id);
        await SendAsync(message, ct);
    }

    private void UpdateCursorForPointer(int nativeX, int nativeY)
    {
        double k = _desktop.Density;
        bool over = nativeX / k is > 110 and < 1050 && nativeY / k is > 140 and < 700;
        if (over == _overEditor) return;
        _overEditor = over;
        _cursorOwed = true;
        Wake();
    }

    private bool _cursorOwed = true;

    private void OnChanged(Rectangle r)
    {
        lock (_dirtyLock) _dirty.Add(r);
        Wake();
    }

    private void OnResized()
    {
        lock (_dirtyLock)
        {
            _dirty.Clear();
            _fullRequested = true;
        }
        _metadataOwed = true;
        Wake();
    }

    private void Wake()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _wake.WaitAsync(TimeSpan.FromMilliseconds(50), ct);
            if (_encodings.Length == 0) continue;

            var messages = new List<byte[]>();
            if (_apple && (_metadataOwed || _cursorOwed))
            {
                var rects = new List<byte[]>();
                if (_metadataOwed)
                {
                    rects.Add(LayoutRect());
                    rects.Add(DeviceInfoRect());
                }
                rects.Add(CursorRect());
                messages.Add(Update(rects));
                _metadataOwed = false;
                _cursorOwed = false;
            }

            List<Rectangle> dirty;
            bool full;
            lock (_dirtyLock)
            {
                long now = Stopwatch.GetTimestamp();
                bool pushDue = _pushInterval is uint us && Stopwatch.GetElapsedTime(_lastUpdateEnd, now).TotalMicroseconds >= us;
                bool allowed = _requestPending || pushDue;
                full = _fullRequested;
                if (!allowed || (!full && _dirty.Count == 0))
                {
                    dirty = [];
                }
                else
                {
                    dirty = full ? [new Rectangle(0, 0, _desktop.Width, _desktop.Height)] : Coalesce(_dirty);
                    _dirty.Clear();
                    _fullRequested = false;
                    _requestPending = false;
                }
            }
            if (dirty.Count > 0)
            {
                messages.Add(Update(EncodeRects(dirty)));
            }
            foreach (var m in messages)
            {
                await SendAsync(m, ct);
            }
            if (dirty.Count > 0)
            {
                _updates++;
                _lastUpdateEnd = Stopwatch.GetTimestamp();
            }
        }
    }

    private static List<Rectangle> Coalesce(List<Rectangle> rects)
    {
        if (rects.Count <= 8) return [.. rects];
        var all = rects[0];
        foreach (var r in rects) all = Rectangle.Union(all, r);
        return [all];
    }

    private int PickCodec() =>
        _encodings.FirstOrDefault(e => e is 6 or 16 or 0x3ea or 0x3e9 or 0x3f3, 0);

    private static string CodecName(int codec) => codec switch
    {
        6 => "zlib", 16 => "ZRLE", 0x3ea => "RGB 5-5-5 zlib", 0x3e9 => "4-bit grey zlib", 0x3f3 => "adaptive (MVS)", _ => "Raw",
    };

    private List<byte[]> EncodeRects(List<Rectangle> rects)
    {
        int codec = PickCodec();
        var result = new List<byte[]>();
        lock (_desktop.Sync)
        {
            byte[] fb = _desktop.Frame;
            int stride = _desktop.Width * 4;
            var bounds = new Rectangle(0, 0, _desktop.Width, _desktop.Height);
            foreach (var raw in rects)
            {
                var r = Rectangle.Intersect(raw, bounds);
                if (r.IsEmpty) continue;
                if (codec == 0x3f3)
                {
                    // The adaptive codec works on the screen's 8-pixel grid.
                    int left = r.X / 8 * 8, top = r.Y / 8 * 8;
                    r = Rectangle.Intersect(Rectangle.FromLTRB(left, top, r.Right, r.Bottom), bounds);
                    if (_mvsView is null || _mvsView.Length != fb.Length) _mvsView = new byte[fb.Length];
                    result.Add(Rect(r.X, r.Y, r.Width, r.Height, codec, _mvs.Encode(fb, _desktop.Width, _desktop.Height, r, _mvsView)));
                    _desktop.ViewerFrame = _mvsView;
                    continue;
                }
                _desktop.ViewerFrame = null;
                byte[] body = codec switch
                {
                    6 => _encoders.Zlib(fb, stride, r.X, r.Y, r.Width, r.Height),
                    16 => _encoders.Zrle(fb, stride, r.X, r.Y, r.Width, r.Height),
                    0x3ea => _encoders.Rgb555(fb, stride, r.X, r.Y, r.Width, r.Height),
                    0x3e9 => _encoders.Gray4(fb, stride, r.X, r.Y, r.Width, r.Height),
                    _ => RawPixels(fb, stride, r),
                };
                result.Add(Rect(r.X, r.Y, r.Width, r.Height, codec, body));
            }
        }
        return result;
    }

    private static byte[] RawPixels(byte[] fb, int stride, Rectangle r)
    {
        var raw = new byte[r.Width * r.Height * 4];
        for (int row = 0; row < r.Height; row++) Buffer.BlockCopy(fb, (r.Y + row) * stride + r.X * 4, raw, row * r.Width * 4, r.Width * 4);
        return raw;
    }

    private byte[] LayoutRect()
    {
        // One 2x display, scaled as the viewer asked. Header 0x14 bytes, one 0x38-byte record.
        var p = new byte[0x14 + 0x38];
        int fbW, fbH;
        double scale;
        lock (_desktop.Sync)
        {
            fbW = _desktop.Width;
            fbH = _desktop.Height;
            scale = _desktop.Scale;
        }
        BinaryPrimitives.WriteUInt16BigEndian(p, 5);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), (ushort)_desktop.PointsWidth);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(4), (ushort)_desktop.PointsHeight);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(6), (ushort)fbW);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(8), (ushort)fbH);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(0x0a), uint.MaxValue);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(0x0e), 4);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(0x12), 1);
        var r = p.AsSpan(0x14);
        BinaryPrimitives.WriteDoubleBigEndian(r, _desktop.Density);
        BinaryPrimitives.WriteDoubleBigEndian(r[8..], scale);
        BinaryPrimitives.WriteUInt32BigEndian(r[0x10..], 1);
        WriteEdges(r[0x14..], _desktop.PointsWidth, _desktop.PointsHeight);
        WriteEdges(r[0x1c..], fbW, fbH);
        BinaryPrimitives.WriteUInt32BigEndian(r[0x24..], 1);
        PixelFormat.Bgrx32.Write(r[0x28..]);
        var body = new byte[2 + p.Length];
        BinaryPrimitives.WriteUInt16BigEndian(body, (ushort)p.Length);
        p.CopyTo(body, 2);
        return Rect(0, 0, 0, 0, 0x451, body);

        static void WriteEdges(Span<byte> e, int w, int h)
        {
            BinaryPrimitives.WriteInt16BigEndian(e, 0);
            BinaryPrimitives.WriteInt16BigEndian(e[2..], 0);
            BinaryPrimitives.WriteInt16BigEndian(e[4..], (short)h);
            BinaryPrimitives.WriteInt16BigEndian(e[6..], (short)w);
        }
    }

    private static byte[] DeviceInfoRect()
    {
        byte[] model = Encoding.ASCII.GetBytes("Mac14,13\0");
        byte[] c1 = "silver\0"u8.ToArray(), c2 = "gray\0"u8.ToArray();
        var info = new byte[16 + model.Length + c1.Length + c2.Length];
        BinaryPrimitives.WriteUInt16BigEndian(info, 2);
        BinaryPrimitives.WriteUInt32BigEndian(info.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(info.AsSpan(10), (ushort)model.Length);
        BinaryPrimitives.WriteUInt16BigEndian(info.AsSpan(12), (ushort)c1.Length);
        BinaryPrimitives.WriteUInt16BigEndian(info.AsSpan(14), (ushort)c2.Length);
        model.CopyTo(info, 16);
        c1.CopyTo(info, 16 + model.Length);
        c2.CopyTo(info, 16 + model.Length + c1.Length);
        var body = new byte[2 + info.Length];
        BinaryPrimitives.WriteUInt16BigEndian(body, (ushort)info.Length);
        info.CopyTo(body, 2);
        return Rect(0, 0, 0, 0, 0x456, body);
    }

    /// <summary>An arrow, or an I-beam over the editor: stored once with pixels, then selected by id.</summary>
    private byte[] CursorRect()
    {
        uint id = _overEditor ? 1001u : 1000u;
        int w = 24, h = 32;
        var (hx, hy) = _overEditor ? (12, 16) : (2, 2);
        if (_cursorStored.Contains(id))
        {
            var select = new byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(select, id);
            return Rect(0, 0, 0, 0, 0x450, select);
        }
        _cursorStored.Add(id);
        var bgrx = new byte[w * h * 4];
        var alpha = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bool ink, edge;
                if (_overEditor)
                {
                    ink = (x is 11 or 12 && y is >= 3 and < 29) || (y is 3 or 28 && x is >= 8 and < 16);
                    edge = (x is 10 or 13 && y is >= 2 and < 30) || (y is 2 or 4 or 27 or 29 && x is >= 7 and < 17);
                }
                else
                {
                    ink = x >= 2 && y >= 2 && x - 2 <= (y - 2) * 0.6 && y < 22 && x < 15;
                    edge = !ink && x >= 1 && y >= 1 && x - 1 <= (y - 1) * 0.6 + 1.5 && y < 24 && x < 17;
                }
                int i = y * w + x;
                byte v = (byte)(ink ? (_overEditor ? 0 : 0) : 255);
                bgrx[i * 4] = bgrx[i * 4 + 1] = bgrx[i * 4 + 2] = v;
                alpha[i] = (byte)(ink || edge ? 255 : 0);
            }
        }
        using var deflated = new MemoryStream();
        using (var z = new ZLibStream(deflated, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(bgrx);
            z.Write(alpha);
        }
        byte[] data = deflated.ToArray();
        var body = new byte[8 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(body, id);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4), (uint)data.Length);
        data.CopyTo(body, 8);
        return Rect(hx, hy, w, h, 0x450, body);
    }

    private static byte[] Rect(int x, int y, int w, int h, int encoding, byte[] body)
    {
        var r = new byte[12 + body.Length];
        BinaryPrimitives.WriteUInt16BigEndian(r, (ushort)x);
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(2), (ushort)y);
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(4), (ushort)w);
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(6), (ushort)h);
        BinaryPrimitives.WriteInt32BigEndian(r.AsSpan(8), encoding);
        body.CopyTo(r, 12);
        return r;
    }

    private static byte[] Update(List<byte[]> rects)
    {
        int total = 4 + rects.Sum(r => r.Length);
        var m = new byte[total];
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(2), (ushort)rects.Count);
        int at = 4;
        foreach (var r in rects)
        {
            r.CopyTo(m, at);
            at += r.Length;
        }
        return m;
    }

    // ───────────────────────────── I/O ─────────────────────────────

    private async Task SendAsync(byte[] message, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            ReadOnlyMemory<byte> wire = _records is null ? message : _records.Frame(message);
            await _transport.WriteAsync(wire, ct);
            await _transport.FlushAsync(ct);
            _bytesOut += wire.Length;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task WriteClearAsync(byte[] data, CancellationToken ct)
    {
        await _transport.WriteAsync(data, ct);
        await _transport.FlushAsync(ct);
    }

    private async Task<byte[]> ReadExactAsync(int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        await _input.ReadExactlyAsync(buffer, ct);
        return buffer;
    }

    private void Log(string message)
    {
        if (_options.Verbose || !message.StartsWith("SetServer", StringComparison.Ordinal))
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {_peer}: {message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _records?.Dispose();
        _encoders.Dispose();
        await _transport.DisposeAsync();
    }
}
