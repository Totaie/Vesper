using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Vesper.Core.Rfb.Decoders;
using Vesper.Core.Security;

namespace Vesper.Core.Rfb;

/// <summary>The user declined (or Vesper refused) the Mac's identity key.</summary>
public sealed class IdentityRejectedException(string message) : Exception(message);

/// <summary>
/// One connection to a Mac's Screen Sharing server: sign-in, Apple's encrypted record layer,
/// screen updates into a <see cref="Rfb.Framebuffer"/>, and input back.
///
/// Speaks Apple's RFB 003.889 when the server offers it (every Mac does), which is what makes
/// it fast: the Mac pushes changes as they happen (AutoFrameBufferUpdate) instead of answering
/// polls late, renders at a reduced scale on request, and sends the pointer as a separate image.
/// Falls back to plain RFB 3.8 for other servers.
/// </summary>
public sealed class RfbSession : IAsyncDisposable
{
    private const int EncodingRaw = 0, EncodingCopyRect = 1, EncodingRre = 2, EncodingHextile = 5,
        EncodingZlib = 6, EncodingZrle = 16, EncodingCursor = -239, EncodingDesktopSize = -223,
        EncodingLastRect = -224, EncodingExtendedDesktopSize = -308;

    private const int MaxNameLength = 64 * 1024;
    private const int MaxCutText = 4 << 20;
    private const uint PushCeiling = 1_000_000;

    private readonly Stream _transport;
    private readonly RfbReader _reader;
    private readonly RfbSessionOptions _options;
    private readonly DecodeContext _decode = new();
    private readonly AppleCursorCache _cursors = new();
    private readonly CancellationTokenSource _lifetime = new();
    private OutgoingQueue? _out;
    private byte[] _commands = new byte[16];
    private bool _apple;
    private double _appliedScale = 1.0;
    private int _pointerX, _pointerY;
    private double _scrollResidualX, _scrollResidualY;
    private uint _pasteboardRequest;
    private bool _armed;
    private uint _armedInterval;
    private double _updateCost;
    private long _lastRearmTicks;

    // Stats over a rolling one-second window.
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _windowStart;
    private int _windowUpdates;
    private long _windowBytes;
    private double _windowDecodeMs;

    private RfbSession(Stream transport, RfbSessionOptions options)
    {
        _transport = transport;
        _options = options;
        _reader = new RfbReader(transport);
        Framebuffer = new Framebuffer(1, 1);
        Info = new SessionInfo("", false, SignInMethod.AppleRsa, false, null, false);
    }

    public Framebuffer Framebuffer { get; }
    public SessionInfo Info { get; private set; }
    public SessionStats Stats { get; } = new();
    public AppleDisplayLayout? Layout { get; private set; }

    /// <summary>The Mac's model identifier (e.g. "Mac14,13"), once it has said.</summary>
    public string? MacModel { get; private set; }

    /// <summary>Raised on the receive thread. Handlers must not block.</summary>
    public event Action<CursorShape>? CursorChanged;
    public event Action<string>? ClipboardReceived;
    public event Action<AppleDisplayLayout>? LayoutChanged;
    public event Action<int, int>? Resized;
    public event Action? BellRang;
    public event Action<string>? Notice;
    public event Action<string>? ModelIdentified;

    public static async Task<RfbSession> ConnectAsync(Stream transport, RfbSessionOptions options, CancellationToken ct)
    {
        var session = new RfbSession(transport, options);
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
        handshake.CancelAfter(options.HandshakeTimeout);
        try
        {
            await session.HandshakeAsync(handshake.Token).ConfigureAwait(false);
            return session;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw new TimeoutException("The Mac stopped responding during sign-in.");
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // ───────────────────────────── Handshake ─────────────────────────────

    private async Task HandshakeAsync(CancellationToken ct)
    {
        byte[] banner = await _reader.ReadBytesAsync(12, ct).ConfigureAwait(false);
        var (major, minor) = ParseVersion(banner);
        _apple = major == 3 && minor == 889;
        if (!_apple && (major < 3 || (major == 3 && minor < 8)))
        {
            throw new ProtocolException($"This server speaks RFB {major}.{minor}; Vesper needs 3.8 or Apple's 3.889.");
        }
        await WriteRawAsync(_apple ? "RFB 003.889\n"u8.ToArray() : "RFB 003.008\n"u8.ToArray(), ct).ConfigureAwait(false);

        int count = await _reader.ReadU8Async(ct).ConfigureAwait(false);
        if (count == 0)
        {
            throw new ProtocolException("The Mac refused the connection: " + await ReadReasonAsync(ct).ConfigureAwait(false));
        }
        byte[] offered = await _reader.ReadBytesAsync(count, ct).ConfigureAwait(false);

        byte[] wrapKey;
        MacIdentity? identity = null;
        SignInMethod method;
        if (_options.Auth.AllowRsa && offered.Contains(ArdRsa.SecurityType))
        {
            (wrapKey, identity) = await SignInRsaAsync(ct).ConfigureAwait(false);
            method = SignInMethod.AppleRsa;
        }
        else if (_options.Auth.AllowDiffieHellman && offered.Contains(ArdDiffieHellman.SecurityType))
        {
            wrapKey = await SignInDiffieHellmanAsync(ct).ConfigureAwait(false);
            method = SignInMethod.AppleDiffieHellman;
        }
        else
        {
            string list = string.Join(", ", offered);
            throw new AuthenticationFailedException(
                offered.Contains(ArdDiffieHellman.SecurityType)
                    ? "This Mac only offers the older Diffie-Hellman sign-in, which isn't safe on an unprotected network. Connect through an SSH tunnel or a VPN, or allow it for this Mac in its settings."
                    : $"This server doesn't offer an Apple sign-in method (it offers {list}). Turn on Screen Sharing or Remote Management on the Mac.",
                credentialsRejected: false);
        }

        try
        {
            uint result = await _reader.ReadU32Async(ct).ConfigureAwait(false);
            if (result != 0)
            {
                // A Mac on 003.889 sends the word alone; a 3.8 server follows it with a reason.
                string detail = _apple ? "" : await ReadReasonAsync(ct).ConfigureAwait(false);
                throw new AuthenticationFailedException(
                    "The Mac didn't accept that user name and password." +
                    (detail.Length > 0 ? $" ({detail})" : "") +
                    " If they're right, check that this account is allowed under Screen Sharing (or Remote Management) in the Mac's Sharing settings.",
                    credentialsRejected: true);
            }

            await WriteRawAsync([(byte)(_apple ? 0x81 : 0x01)], ct).ConfigureAwait(false);
            var (name, flags) = await ReadServerInitAsync(ct).ConfigureAwait(false);
            bool observeOnly = (flags & 0x01) != 0;

            if (_apple)
            {
                await StartAppleEncryptionAsync(wrapKey, observeOnly, ct).ConfigureAwait(false);
            }
            else
            {
                _out = new OutgoingQueue(_transport, null);
            }
            Info = new SessionInfo(name, _apple, method, Encrypted: _apple, identity, observeOnly || _options.ViewOnly);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrapKey);
        }
    }

    private async Task<(byte[] WrapKey, MacIdentity Identity)> SignInRsaAsync(CancellationToken ct)
    {
        // The selection byte and the key request go out together, as Apple's viewer sends them.
        byte[] first = new byte[15];
        first[0] = ArdRsa.SecurityType;
        ArdRsa.KeyRequest().CopyTo(first, 1);
        await WriteRawAsync(first, ct).ConfigureAwait(false);

        uint length = await _reader.ReadU32Async(ct).ConfigureAwait(false);
        if (length < 6 || length > 16 * 1024)
        {
            throw new ProtocolException("The Mac's RSA key response has an implausible length.");
        }
        byte[] payload = await _reader.ReadBytesAsync((int)length, ct).ConfigureAwait(false);
        byte[] der = ArdRsa.ParseKeyResponse(payload);
        using var rsa = ArdRsa.ImportPublicKey(der);
        var identity = new MacIdentity(ArdRsa.Fingerprint(der), der, rsa.KeySize);

        // Nothing secret has been sent yet; the user (or the pin) decides before anything is.
        if (_options.IdentityVerifier is null || !await _options.IdentityVerifier.VerifyAsync(identity, ct).ConfigureAwait(false))
        {
            throw new IdentityRejectedException("The Mac's identity wasn't trusted, so no password was sent.");
        }

        byte[] user = ArdDiffieHellman.Utf8Truncated(_options.Username);
        byte[] pass = ArdDiffieHellman.Utf8Truncated(_options.Password);
        try
        {
            var (message, wrapKey) = ArdRsa.Login(rsa, user, pass);
            try
            {
                await WriteRawAsync(message, ct).ConfigureAwait(false);
                // The Mac acknowledges with a counted (normally empty) payload, then sends the result.
                uint ack = await _reader.ReadU32Async(ct).ConfigureAwait(false);
                if (ack > 16 * 1024)
                {
                    throw new ProtocolException("The Mac's sign-in acknowledgement has an implausible length.");
                }
                await _reader.SkipAsync(ack, ct).ConfigureAwait(false);
                return (wrapKey, identity);
            }
            catch
            {
                CryptographicOperations.ZeroMemory(wrapKey);
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(message);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(user);
            CryptographicOperations.ZeroMemory(pass);
        }
    }

    private async Task<byte[]> SignInDiffieHellmanAsync(CancellationToken ct)
    {
        await WriteRawAsync([ArdDiffieHellman.SecurityType], ct).ConfigureAwait(false);
        await _reader.EnsureAsync(4, ct).ConfigureAwait(false);
        byte[] generator = _reader.Take(2).ToArray();
        int keyLength = _reader.U16();
        ArdDiffieHellman.ValidateKeyLength(keyLength);
        byte[] prime = await _reader.ReadBytesAsync(keyLength, ct).ConfigureAwait(false);
        byte[] serverPublic = await _reader.ReadBytesAsync(keyLength, ct).ConfigureAwait(false);

        byte[] user = ArdDiffieHellman.Utf8Truncated(_options.Username);
        byte[] pass = ArdDiffieHellman.Utf8Truncated(_options.Password);
        try
        {
            var (credentials, clientPublic, wrapKey) = ArdDiffieHellman.Respond(new(generator, prime, serverPublic), user, pass);
            byte[] reply = [.. credentials, .. clientPublic];
            await WriteRawAsync(reply, ct).ConfigureAwait(false);
            return wrapKey;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(user);
            CryptographicOperations.ZeroMemory(pass);
        }
    }

    private async Task<(string Name, uint Flags)> ReadServerInitAsync(CancellationToken ct)
    {
        await _reader.EnsureAsync(24, ct).ConfigureAwait(false);
        int width = _reader.U16();
        int height = _reader.U16();
        PixelFormat.Parse(_reader.Take(16)); // the Mac's native format; Vesper sets its own
        uint nameLength = _reader.U32();
        if (nameLength > MaxNameLength)
        {
            throw new ProtocolException("The Mac sent an implausibly long desktop name.");
        }
        byte[] field = await _reader.ReadBytesAsync((int)nameLength, ct).ConfigureAwait(false);
        Framebuffer.Resize(width, height);

        // Apple's extended ServerInit: u16 0, u32 flags, a 16-byte bitmap of the client messages
        // the Mac accepts, then the real UTF-8 name.
        if (_apple && field.Length >= 22 && field[0] == 0 && field[1] == 0)
        {
            uint flags = BinaryPrimitives.ReadUInt32BigEndian(field.AsSpan(2));
            _commands = field[6..22];
            return (Encoding.UTF8.GetString(field, 22, field.Length - 22), flags);
        }
        return (Encoding.UTF8.GetString(field), 0);
    }

    private async Task StartAppleEncryptionAsync(byte[] wrapKey, bool observeOnly, CancellationToken ct)
    {
        // Written back to back in cleartext: once the Mac starts encrypting, anything else sent
        // in clear would break the session. AutoPasteboard is only started if Vesper is allowed
        // to receive the Mac's clipboard at all.
        using var prelude = new MemoryStream();
        prelude.Write(AppleMessages.ViewerInfoMessage());
        prelude.Write(AppleMessages.SetModeMessage(control: !observeOnly && !_options.ViewOnly));
        if (_options.ClipboardFromMac)
        {
            prelude.Write(AppleMessages.AutoPasteboardMessage(start: true));
        }
        prelude.Write(AppleMessages.SetEncryptionStart());
        prelude.Write(AppleMessages.SetEncryptionInbound());
        await WriteRawAsync(prelude.ToArray(), ct).ConfigureAwait(false);

        var (key, iv) = await AwaitRekeyAsync(wrapKey, ct).ConfigureAwait(false);
        try
        {
            _reader.SwitchSource(leftover => new RecordReaderStream(_transport, key, iv, leftover));
            _out = new OutgoingQueue(_transport, new RecordWriter(key, iv));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(iv);
        }
    }

    /// <summary>
    /// Reads cleartext messages until the rekey rectangle arrives. Only Bell and MiscStatus may
    /// precede it; anything else means the stream isn't what Vesper expects, and it stops.
    /// </summary>
    private async Task<(byte[] Key, byte[] Iv)> AwaitRekeyAsync(byte[] wrapKey, CancellationToken ct)
    {
        while (true)
        {
            byte type = await _reader.ReadU8Async(ct).ConfigureAwait(false);
            switch (type)
            {
                case 0:
                    await _reader.EnsureAsync(3, ct).ConfigureAwait(false);
                    _reader.Skip(1);
                    int rects = _reader.U16();
                    if (rects == 0) continue;
                    await _reader.EnsureAsync(12, ct).ConfigureAwait(false);
                    _reader.Skip(8);
                    int encoding = _reader.S32();
                    if (encoding != AppleMessages.EncodingRekey || rects != 1)
                    {
                        throw new ProtocolException("The Mac sent screen data before encryption was set up.");
                    }
                    byte[] body = await _reader.ReadBytesAsync(AppleRecordLayer.RekeyLength, ct).ConfigureAwait(false);
                    var (_, key, iv) = AppleRecordLayer.UnwrapRekey(wrapKey, body);
                    return (key, iv);
                case 2:
                    continue;
                case AppleMessages.MiscStatus:
                    await _reader.EnsureAsync(3, ct).ConfigureAwait(false);
                    _reader.Skip(1);
                    await _reader.SkipAsync(_reader.U16(), ct).ConfigureAwait(false);
                    continue;
                default:
                    throw new ProtocolException($"The Mac sent message {type} before encryption was set up.");
            }
        }
    }

    // ───────────────────────────── Session loop ─────────────────────────────

    /// <summary>Runs until the connection closes, fails, or <paramref name="ct"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var token = linked.Token;
        var output = _out ?? throw new InvalidOperationException("Not connected.");
        var writer = Task.Run(() => output.RunAsync(token), token);

        output.Enqueue(SetPixelFormatMessage(PixelFormat.Bgrx32));
        output.Enqueue(SetEncodingsMessage(EncodingList()));
        if (_apple && _options.ServerScale is double scale && scale > 0 && scale < 1)
        {
            output.Enqueue(AppleMessages.SetServerScalingMessage(scale));
        }
        RequestUpdate(incremental: false);

        var reader = Task.Run(() => ReadLoopAsync(token), token);
        var first = await Task.WhenAny(reader, writer).ConfigureAwait(false);
        _lifetime.Cancel();
        try
        {
            await first.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        try
        {
            await Task.WhenAll(reader, writer).ConfigureAwait(false);
        }
        catch
        {
            // The first task's exception (if any) has already been observed above.
        }
    }

    private int[] EncodingList()
    {
        var list = new List<int>();
        int preferredTrueColor = _options.Codec == Codec.Zrle ? EncodingZrle : EncodingZlib;
        int otherTrueColor = preferredTrueColor == EncodingZrle ? EncodingZlib : EncodingZrle;
        if (_apple)
        {
            // The Mac encodes with the first of zlib, ZRLE and its own codecs in this list.
            if (_options.Color == ColorQuality.Thousands) list.Add(AppleMessages.EncodingRgb555Zlib);
            if (_options.Color == ColorQuality.Grayscale) list.Add(AppleMessages.EncodingGrayZlib);
            // The rest follows Apple's own viewer (Full quality), plus Raw, the rekey and LastRect,
            // which encrypted sessions see. Every entry is decoded or stepped over: the Mac treats
            // the list as a promise.
            list.AddRange([preferredTrueColor, otherTrueColor, EncodingRaw, EncodingCursor,
                AppleMessages.EncodingCursorImage, AppleMessages.EncodingCursorPos, EncodingDesktopSize,
                AppleMessages.EncodingDisplayInfo, AppleMessages.EncodingDisplayLayout,
                AppleMessages.EncodingVendorKeysyms, AppleMessages.EncodingKeyboardSource,
                AppleMessages.EncodingDeviceInfo, AppleMessages.EncodingRekey, EncodingLastRect]);
        }
        else
        {
            list.AddRange([EncodingCopyRect, preferredTrueColor, otherTrueColor, EncodingHextile, EncodingRre, EncodingRaw,
                EncodingCursor, EncodingDesktopSize, EncodingLastRect, EncodingExtendedDesktopSize]);
        }
        return [.. list];
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        while (true)
        {
            byte type = await _reader.ReadU8Async(ct).ConfigureAwait(false);
            switch (type)
            {
                case 0:
                    await ReadFramebufferUpdateAsync(ct).ConfigureAwait(false);
                    break;
                case 1: // SetColourMapEntries: can't happen in true colour, but stay in step
                    await _reader.EnsureAsync(5, ct).ConfigureAwait(false);
                    _reader.Skip(3);
                    await _reader.SkipAsync(_reader.U16() * 6L, ct).ConfigureAwait(false);
                    break;
                case 2:
                    BellRang?.Invoke();
                    break;
                case 3:
                    await ReadServerCutTextAsync(ct).ConfigureAwait(false);
                    break;
                case AppleMessages.MiscStatus when _apple:
                    await ReadMiscStatusAsync(ct).ConfigureAwait(false);
                    break;
                case AppleMessages.PasteboardData when _apple:
                    await ReadPasteboardAsync(ct).ConfigureAwait(false);
                    break;
                default:
                    throw new ProtocolException($"The Mac sent a message Vesper doesn't know (type {type}).");
            }
        }
    }

    private async Task ReadFramebufferUpdateAsync(CancellationToken ct)
    {
        long started = _clock.ElapsedTicks;
        long bytesBefore = _reader.BytesReceived;
        await _reader.EnsureAsync(3, ct).ConfigureAwait(false);
        _reader.Skip(1);
        int rects = _reader.U16();
        bool fullRepaint = false;
        bool pixels = false;

        for (int i = 0; i < rects; i++)
        {
            await _reader.EnsureAsync(12, ct).ConfigureAwait(false);
            int x = _reader.U16(), y = _reader.U16(), w = _reader.U16(), h = _reader.U16();
            int encoding = _reader.S32();
            var rc = new PixelRect(x, y, w, h);

            switch (encoding)
            {
                case EncodingLastRect:
                    i = rects;
                    continue;
                case EncodingDesktopSize:
                    if (!_apple) ResizeFramebuffer(w, h);
                    continue;
                case EncodingExtendedDesktopSize when !_apple:
                    await _reader.EnsureAsync(4, ct).ConfigureAwait(false);
                    int screens = _reader.U8();
                    _reader.Skip(3);
                    await _reader.SkipAsync(screens * 16L, ct).ConfigureAwait(false);
                    if (y == 0) ResizeFramebuffer(w, h);
                    continue;
                case EncodingCursor:
                    await ReadRfbCursorAsync(x, y, w, h, ct).ConfigureAwait(false);
                    continue;
                case AppleMessages.EncodingCursorPos when _apple:
                    continue;
                case AppleMessages.EncodingCursorImage when _apple:
                    await ReadAppleCursorAsync(x, y, w, h, ct).ConfigureAwait(false);
                    continue;
                case AppleMessages.EncodingDisplayLayout when _apple:
                    await ReadDisplayLayoutAsync(ct).ConfigureAwait(false);
                    fullRepaint = true;
                    continue;
                case AppleMessages.EncodingDisplayInfo when _apple:
                    await _reader.EnsureAsync(10, ct).ConfigureAwait(false);
                    _reader.Skip(8);
                    await _reader.SkipAsync(_reader.U16() * 0x1cL, ct).ConfigureAwait(false);
                    continue;
                case AppleMessages.EncodingVendorKeysyms or AppleMessages.EncodingKeyboardSource when _apple:
                    await _reader.SkipAsync(await _reader.ReadU16Async(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                    continue;
                case AppleMessages.EncodingDeviceInfo when _apple:
                    await ReadDeviceInfoAsync(ct).ConfigureAwait(false);
                    continue;
                case AppleMessages.EncodingRekey when _apple:
                    throw new ProtocolException("The Mac changed encryption keys mid-session, which Vesper never asks for.");
            }

            if (!Framebuffer.Contains(x, y, w, h))
            {
                throw new ProtocolException($"The Mac sent a rectangle ({w}×{h} at {x},{y}) outside its {Framebuffer.Width}×{Framebuffer.Height} screen.");
            }
            pixels = true;
            switch (encoding)
            {
                case EncodingRaw: await StandardDecoders.RawAsync(_reader, Framebuffer, rc, _decode, ct).ConfigureAwait(false); break;
                case EncodingCopyRect: await StandardDecoders.CopyRectAsync(_reader, Framebuffer, rc, ct).ConfigureAwait(false); break;
                case EncodingRre: await StandardDecoders.RreAsync(_reader, Framebuffer, rc, ct).ConfigureAwait(false); break;
                case EncodingHextile: await StandardDecoders.HextileAsync(_reader, Framebuffer, rc, ct).ConfigureAwait(false); break;
                case EncodingZlib: await StandardDecoders.ZlibAsync(_reader, Framebuffer, rc, _decode, ct).ConfigureAwait(false); break;
                case EncodingZrle: await ZrleDecoder.DecodeAsync(_reader, Framebuffer, rc, _decode, ct).ConfigureAwait(false); break;
                case AppleMessages.EncodingRgb555Zlib when _apple: await AppleZlibDecoders.Rgb555Async(_reader, Framebuffer, rc, _decode, ct).ConfigureAwait(false); break;
                case AppleMessages.EncodingGrayZlib when _apple: await AppleZlibDecoders.Gray4Async(_reader, Framebuffer, rc, _decode, ct).ConfigureAwait(false); break;
                default:
                    throw new ProtocolException($"The Mac sent encoding {encoding}, which Vesper didn't ask for.");
            }
        }

        if (pixels) Framebuffer.Commit();
        RecordUpdate(started, bytesBefore, pixels);

        if (fullRepaint)
        {
            // A layout drops the Mac's push arming; re-arm for the whole (possibly new) screen,
            // then ask for a full repaint now that the update carrying the layout has ended.
            ArmPushes(_armedInterval == 0 ? _options.PushIntervalMicroseconds : _armedInterval);
            RequestUpdate(incremental: false);
        }
        else
        {
            // A still screen gets nothing unrequested, armed or not: always keep one request out.
            RequestUpdate(incremental: true);
        }
    }

    private void RecordUpdate(long startedTicks, long bytesBefore, bool pixels)
    {
        long now = _clock.ElapsedTicks;
        double costMs = (now - startedTicks) * 1000.0 / Stopwatch.Frequency;
        if (pixels)
        {
            _windowUpdates++;
            _windowDecodeMs += costMs;
            PacePushes(costMs);
        }
        _windowBytes += _reader.BytesReceived - bytesBefore;
        double windowSeconds = (now - _windowStart) / (double)Stopwatch.Frequency;
        if (windowSeconds >= 1.0)
        {
            Stats.UpdatesPerSecond = _windowUpdates / windowSeconds;
            Stats.MegabitsPerSecond = _windowBytes * 8 / windowSeconds / 1e6;
            Stats.DecodeMilliseconds = _windowUpdates > 0 ? _windowDecodeMs / _windowUpdates : 0;
            Stats.BytesReceived = _reader.BytesReceived;
            Stats.BytesSent = _out?.BytesSent ?? 0;
            _windowStart = now;
            _windowUpdates = 0;
            _windowBytes = 0;
            _windowDecodeMs = 0;
        }
    }

    /// <summary>
    /// Keeps the gap between pushed updates at least as long as an update takes to receive and
    /// decode, so the connection drains and the Mac reads input between updates. (The Mac reads
    /// nothing while it writes one, so unpaced pushes on a slow link would stall typing.)
    /// </summary>
    private void PacePushes(double costMs)
    {
        if (!_apple || !_armed) return;
        _updateCost += (costMs * 1000 - _updateCost) * 0.2;
        uint floor = _options.PushIntervalMicroseconds;
        uint wanted = (uint)Math.Clamp(_updateCost, floor, PushCeiling);
        bool moved = wanted >= _armedInterval * 1.5 || wanted <= _armedInterval / 1.5 || (wanted == floor && _armedInterval != floor);
        long now = _clock.ElapsedTicks;
        if (moved && now - _lastRearmTicks >= Stopwatch.Frequency / 2)
        {
            ArmPushes(wanted);
        }
    }

    private void ArmPushes(uint interval)
    {
        if (!_apple || _out is null) return;
        _armed = true;
        _armedInterval = interval;
        _lastRearmTicks = _clock.ElapsedTicks;
        _out.Enqueue(AppleMessages.AutoFrameBufferUpdateMessage(interval, Framebuffer.Width, Framebuffer.Height));
    }

    private void ResizeFramebuffer(int width, int height)
    {
        if (width == Framebuffer.Width && height == Framebuffer.Height) return;
        Framebuffer.Resize(width, height);
        Resized?.Invoke(width, height);
    }

    private async Task ReadDisplayLayoutAsync(CancellationToken ct)
    {
        int length = await _reader.ReadU16Async(ct).ConfigureAwait(false);
        byte[] payload = await _reader.ReadBytesAsync(length, ct).ConfigureAwait(false);
        var layout = AppleDisplayLayout.Parse(payload);
        Layout = layout;
        _appliedScale = layout.ServerScale;
        ResizeFramebuffer(layout.FramebufferWidth, layout.FramebufferHeight);
        LayoutChanged?.Invoke(layout);
    }

    /// <summary>
    /// DeviceInfo (0x456): u16 size, u16 version, u32, u32, three u16 string lengths (each counting
    /// its NUL), then the model identifier (hw.model, e.g. "Mac14,13") and two colour strings.
    /// </summary>
    private async Task ReadDeviceInfoAsync(CancellationToken ct)
    {
        int size = await _reader.ReadU16Async(ct).ConfigureAwait(false);
        byte[] body = await _reader.ReadBytesAsync(size, ct).ConfigureAwait(false);
        if (body.Length < 16) return;
        int modelLength = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(10));
        if (modelLength is < 2 or > 64 || 16 + modelLength > body.Length) return;
        string model = Encoding.ASCII.GetString(body, 16, modelLength - 1);
        if (model.All(c => char.IsAsciiLetterOrDigit(c) || c == ','))
        {
            MacModel = model;
            ModelIdentified?.Invoke(model);
        }
    }

    private async Task ReadAppleCursorAsync(int hotX, int hotY, int width, int height, CancellationToken ct)
    {
        uint id = await _reader.ReadU32Async(ct).ConfigureAwait(false);
        // Ids up to 999 name one of macOS's built-in cursors, which the viewer draws itself; the
        // rectangle is just the id (ard-rs, after Apple's _HandleCursorImageWithAlphaUpdate).
        if (id <= 999)
        {
            CursorChanged?.Invoke(CursorShape.SystemDefault);
            return;
        }
        uint length = await _reader.ReadU32Async(ct).ConfigureAwait(false);
        if (length > 1 << 20)
        {
            throw new ProtocolException("The Mac sent an implausibly large pointer image.");
        }
        byte[] deflated = await _reader.ReadBytesAsync((int)length, ct).ConfigureAwait(false);
        var shape = _cursors.Accept(id, hotX, hotY, width, height, deflated);
        if (shape is not null) CursorChanged?.Invoke(shape);
    }

    private async Task ReadRfbCursorAsync(int hotX, int hotY, int width, int height, CancellationToken ct)
    {
        if (width > CursorShape.MaxDimension || height > CursorShape.MaxDimension)
        {
            throw new ProtocolException("The server sent an implausibly large pointer image.");
        }
        byte[] pixels = await _reader.ReadBytesAsync(width * height * 4, ct).ConfigureAwait(false);
        byte[] mask = await _reader.ReadBytesAsync((width + 7) / 8 * height, ct).ConfigureAwait(false);
        CursorChanged?.Invoke(AppleCursorCache.FromRfbCursor(hotX, hotY, width, height, pixels, mask));
    }

    private async Task ReadServerCutTextAsync(CancellationToken ct)
    {
        await _reader.EnsureAsync(7, ct).ConfigureAwait(false);
        _reader.Skip(3);
        int length = _reader.S32();
        long size = Math.Abs((long)length);
        if (length < 0 || size > MaxCutText || !_options.ClipboardFromMac)
        {
            await _reader.SkipAsync(size, ct).ConfigureAwait(false);
            return;
        }
        byte[] text = await _reader.ReadBytesAsync((int)size, ct).ConfigureAwait(false);
        ClipboardReceived?.Invoke(Encoding.Latin1.GetString(text));
    }

    private async Task ReadMiscStatusAsync(CancellationToken ct)
    {
        await _reader.EnsureAsync(3, ct).ConfigureAwait(false);
        _reader.Skip(1);
        int length = _reader.U16();
        byte[] body = await _reader.ReadBytesAsync(length, ct).ConfigureAwait(false);
        if (body.Length < 4) return;
        ushort command = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(2));
        switch (command)
        {
            case AppleMessages.StatusPasteboardChanged when _options.ClipboardFromMac:
                _out?.Enqueue(AppleMessages.PasteboardFetchMessage(++_pasteboardRequest));
                break;
            case AppleMessages.StatusSessionEnded:
                Notice?.Invoke("The Mac's user ended the screen sharing session.");
                break;
            case AppleMessages.StatusDisplaysAsleep:
                Notice?.Invoke("The Mac's displays went to sleep.");
                break;
            case AppleMessages.StatusDisplaysAwake:
                Notice?.Invoke("The Mac's displays woke up.");
                break;
        }
    }

    private async Task ReadPasteboardAsync(CancellationToken ct)
    {
        await _reader.EnsureAsync(15, ct).ConfigureAwait(false);
        var header = _reader.Take(15);
        uint uncompressed = BinaryPrimitives.ReadUInt32BigEndian(header[7..]);
        uint compressed = BinaryPrimitives.ReadUInt32BigEndian(header[11..]);
        if (!_options.ClipboardFromMac || compressed > ApplePasteboard.MaxArchiveBytes || uncompressed > ApplePasteboard.MaxArchiveBytes)
        {
            await _reader.SkipAsync(compressed, ct).ConfigureAwait(false);
            return;
        }
        byte[] data = await _reader.ReadBytesAsync((int)compressed, ct).ConfigureAwait(false);
        string? text = ApplePasteboard.ExtractText(data, uncompressed);
        if (text is not null) ClipboardReceived?.Invoke(text);
    }

    // ───────────────────────────── Input ─────────────────────────────

    /// <summary>Whether the Mac takes Apple's precise scroll message.</summary>
    public bool SupportsPreciseScroll => _apple && AppleMessages.Accepts(_commands, AppleMessages.Event2);

    /// <summary>The framebuffer's pixels per point after server scaling, for sizing the window sensibly.</summary>
    public double EffectiveDensity => Layout?.EffectiveDensity ?? 1.0;

    private bool InputAllowed => _out is not null && !Info.ObserveOnly;

    /// <summary>Moves the pointer to a framebuffer pixel, with the given buttons held.</summary>
    public void SendPointer(int x, int y, PointerButtons buttons)
    {
        if (!InputAllowed) return;
        x = Math.Clamp(x, 0, Math.Max(0, Framebuffer.Width - 1));
        y = Math.Clamp(y, 0, Math.Max(0, Framebuffer.Height - 1));
        // Under server scaling the Mac still reads pointer positions in unscaled pixels.
        _pointerX = (int)Math.Round(x / _appliedScale);
        _pointerY = (int)Math.Round(y / _appliedScale);
        byte mask = (byte)(buttons & PointerButtons.Left);
        if ((buttons & PointerButtons.Middle) != 0) mask |= (byte)(_apple ? 4 : 2);
        if ((buttons & PointerButtons.Right) != 0) mask |= (byte)(_apple ? 2 : 4);
        _out!.EnqueuePointer(mask, (ushort)Math.Min(_pointerX, ushort.MaxValue), (ushort)Math.Min(_pointerY, ushort.MaxValue));
    }

    /// <summary>
    /// Scrolls by a distance in the Mac's points; positive is right/down (content moves up).
    /// Sent as Apple's precise scroll when available, else as wheel clicks.
    /// </summary>
    public void SendScroll(double dxPoints, double dyPoints, PointerButtons held)
    {
        if (!InputAllowed) return;
        _scrollResidualX += dxPoints;
        _scrollResidualY += dyPoints;
        if (SupportsPreciseScroll)
        {
            int dx = (int)_scrollResidualX, dy = (int)_scrollResidualY;
            if (dx == 0 && dy == 0) return;
            _scrollResidualX -= dx;
            _scrollResidualY -= dy;
            _out!.Enqueue(AppleMessages.ScrollWheelMessage(dx, dy, _pointerX, _pointerY));
            return;
        }
        // Wheel bits: one click per ~40 points. A Mac only scrolls on a mask of exactly the bit.
        const double PointsPerClick = 40;
        byte baseMask = (byte)(held & PointerButtons.Left);
        while (Math.Abs(_scrollResidualY) >= PointsPerClick)
        {
            byte bit = (byte)(_scrollResidualY > 0 ? 0x10 : 0x08);
            _out!.EnqueuePointer((byte)(baseMask | bit), (ushort)_pointerX, (ushort)_pointerY);
            _out.Enqueue([5, baseMask, (byte)(_pointerX >> 8), (byte)_pointerX, (byte)(_pointerY >> 8), (byte)_pointerY]);
            _scrollResidualY -= Math.Sign(_scrollResidualY) * PointsPerClick;
        }
        while (Math.Abs(_scrollResidualX) >= PointsPerClick)
        {
            byte bit = (byte)(_scrollResidualX > 0 ? 0x40 : 0x20);
            _out!.EnqueuePointer((byte)(baseMask | bit), (ushort)_pointerX, (ushort)_pointerY);
            _out.Enqueue([5, baseMask, (byte)(_pointerX >> 8), (byte)_pointerX, (byte)(_pointerY >> 8), (byte)_pointerY]);
            _scrollResidualX -= Math.Sign(_scrollResidualX) * PointsPerClick;
        }
    }

    public void SendKey(uint keysym, bool down)
    {
        if (!InputAllowed) return;
        var m = new byte[8];
        m[0] = 4;
        m[1] = (byte)(down ? 1 : 0);
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(4), keysym);
        _out!.Enqueue(m);
    }

    /// <summary>Puts text on the Mac's clipboard.</summary>
    public void SendClipboard(string text)
    {
        if (_out is null) return;
        if (_apple)
        {
            _out.Enqueue(ApplePasteboard.BuildSendMessage(text, _pasteboardRequest));
            return;
        }
        byte[] latin1 = Encoding.Latin1.GetBytes(text);
        var m = new byte[8 + latin1.Length];
        m[0] = 6;
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(4), (uint)latin1.Length);
        latin1.CopyTo(m, 8);
        _out.Enqueue(m);
    }

    /// <summary>Asks the Mac to render at <paramref name="factor"/> of its native pixels (Apple only).</summary>
    public void SetServerScale(double factor)
    {
        if (!_apple || _out is null) return;
        _out.Enqueue(AppleMessages.SetServerScalingMessage(Math.Clamp(factor, 0.1, 1.0)));
    }

    /// <summary>Shows one of the Mac's displays, or all of them (null).</summary>
    public void SelectDisplay(uint? displayId)
    {
        if (!_apple || _out is null) return;
        _out.Enqueue(AppleMessages.SetDisplayMessage(displayId));
    }

    public void RequestUpdate(bool incremental)
    {
        // A 0×0 request is reported to hang the Mac's encoder (noVNC-ARD).
        if (_out is null || Framebuffer.Width == 0 || Framebuffer.Height == 0) return;
        var m = new byte[10];
        m[0] = 3;
        m[1] = (byte)(incremental ? 1 : 0);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(6), (ushort)Framebuffer.Width);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(8), (ushort)Framebuffer.Height);
        _out.Enqueue(m);
    }

    // ───────────────────────────── Helpers ─────────────────────────────

    private static byte[] SetPixelFormatMessage(PixelFormat format)
    {
        var m = new byte[20];
        format.Write(m.AsSpan(4));
        return m;
    }

    private static byte[] SetEncodingsMessage(int[] encodings)
    {
        var m = new byte[4 + encodings.Length * 4];
        m[0] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(2), (ushort)encodings.Length);
        for (int i = 0; i < encodings.Length; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(m.AsSpan(4 + i * 4), encodings[i]);
        }
        return m;
    }

    internal static (int Major, int Minor) ParseVersion(byte[] banner)
    {
        string text = Encoding.ASCII.GetString(banner);
        if (text.Length != 12 || !text.StartsWith("RFB ", StringComparison.Ordinal) || text[7] != '.' || text[11] != '\n'
            || !int.TryParse(text.AsSpan(4, 3), out int major) || !int.TryParse(text.AsSpan(8, 3), out int minor))
        {
            throw new ProtocolException("That port isn't a Screen Sharing (VNC) server.");
        }
        return (major, minor);
    }

    private async Task<string> ReadReasonAsync(CancellationToken ct)
    {
        uint length = await _reader.ReadU32Async(ct).ConfigureAwait(false);
        if (length > 4096) return "(reason too long to show)";
        byte[] reason = await _reader.ReadBytesAsync((int)length, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(reason);
    }

    private async Task WriteRawAsync(byte[] data, CancellationToken ct)
    {
        await _transport.WriteAsync(data, ct).ConfigureAwait(false);
        await _transport.FlushAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Already gone.
        }
        _out?.Dispose();
        _decode.Dispose();
        _lifetime.Dispose();
    }
}
