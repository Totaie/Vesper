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
/// Speaks Apple's RFB 003.889 (every Mac does), which is what makes it fast: the Mac pushes
/// changes as they happen (AutoFrameBufferUpdate) instead of answering polls late, renders at a
/// reduced scale on request, and sends the pointer as a separate image. It's also what makes it
/// safe: everything after sign-in travels in Apple's encrypted record layer. A server that only
/// speaks plain RFB 3.8 has no such layer and is refused unless the caller's transport encrypts
/// (<see cref="RfbSessionOptions.AllowUnencrypted"/>).
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
    private RecordReaderStream? _records;
    private Task? _readTask, _writeTask;
    private int _disposed;
    private volatile HashSet<int> _requested = [];
    private byte[] _commands = new byte[16];
    private bool _apple;
    private double _appliedScale = 1.0;
    private int _pointerX, _pointerY;
    private double _scrollResidualX, _scrollResidualY;
    private uint _pasteboardRequest;
    private Deadline? _deadline;
    private (int Width, int Height)? _fitPixels;
    private ColorQuality _color;
    private volatile bool _clipboardFromMac;
    private double _requestedScale = 1.0;
    private bool _armed;
    private uint _armedInterval;
    private double _updateCost;
    private long _lastRearmTicks;
    private int _lastPixelEncoding = int.MinValue;

    // Stats over a rolling one-second window.
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _windowStart;
    private int _windowUpdates;
    private long _windowStartBytes;
    private double _windowDecodeMs;

    private RfbSession(Stream transport, RfbSessionOptions options)
    {
        _transport = transport;
        _options = options;
        _reader = new RfbReader(transport);
        _fitPixels = options.FitPixels;
        _color = options.Color;
        _clipboardFromMac = options.ClipboardFromMac;
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

    /// <summary>
    /// Raised once, on the receive thread, when the Mac's first encrypted record has checked out:
    /// from then on the session is known to be with whoever holds the sign-in's secret. (Right
    /// away for a session without Apple's record layer.) Show the Mac's screen from here.
    /// </summary>
    public event Action? Secured;

    public static async Task<RfbSession> ConnectAsync(Stream transport, RfbSessionOptions options, CancellationToken ct)
    {
        var session = new RfbSession(transport, options);
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var deadline = new Deadline(handshake, options.HandshakeTimeout);
        session._deadline = deadline;
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
        finally
        {
            options.ForgetPassword();
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
        if (!_apple && !_options.AllowUnencrypted)
        {
            // Without Apple's record layer nothing protects the session, and no sign-in proves who
            // answered: a Mac's public identity key can simply be replayed by whoever is in the way.
            throw new SecurityRefusedException(RefusalReason.NotApple,
                $"This server answered as plain VNC (RFB {major}.{minor}) instead of as a Mac's Screen Sharing, so the session couldn't be encrypted, and Vesper didn't send your password. " +
                "Every Mac speaks Apple's protocol, so this may not be your Mac, or something on the network may be in the way. Other VNC servers work only through the SSH tunnel.");
        }
        await WriteRawAsync(_apple ? "RFB 003.889\n"u8.ToArray() : "RFB 003.008\n"u8.ToArray(), ct).ConfigureAwait(false);

        int count = await _reader.ReadU8Async(ct).ConfigureAwait(false);
        if (count == 0)
        {
            throw new ConnectionRefusedException(await ReadReasonAsync(ct).ConfigureAwait(false));
        }
        byte[] offered = await _reader.ReadBytesAsync(count, ct).ConfigureAwait(false);
        _options.StageChanged?.Invoke(SessionStage.SigningIn);

        // RSA sign-in only proves anything inside Apple's record layer, where the session keys
        // arrive wrapped under a secret only the identity key's owner could have recovered.
        bool rsaUsable = _apple && offered.Contains(ArdRsa.SecurityType);
        bool diffieHellmanOffered = offered.Contains(ArdDiffieHellman.SecurityType);
        byte[] wrapKey;
        MacIdentity? identity = null;
        SignInMethod method;
        if (_options.Auth.AllowRsa && rsaUsable)
        {
            (wrapKey, identity) = await SignInRsaAsync(ct).ConfigureAwait(false);
            method = SignInMethod.AppleRsa;
        }
        else if (_options.Auth.AllowDiffieHellman && diffieHellmanOffered)
        {
            wrapKey = await SignInDiffieHellmanAsync(ct).ConfigureAwait(false);
            method = SignInMethod.AppleDiffieHellman;
        }
        else if (diffieHellmanOffered)
        {
            throw new SecurityRefusedException(RefusalReason.OnlyUnverifiableSignIn,
                "This Mac only offered Apple's older sign-in, which can't prove it's your Mac on this network, so Vesper didn't send your password. " +
                "Connect through an SSH tunnel or straight to the Mac's Tailscale address, or allow the older sign-in for this Mac in its settings.");
        }
        else
        {
            throw new AuthenticationFailedException(
                $"This server doesn't offer a sign-in Vesper can use here (it offers {string.Join(", ", offered)}). Turn on Screen Sharing or Remote Management on the Mac.",
                credentialsRejected: false);
        }

        try
        {
            uint result = await _reader.ReadU32Async(ct).ConfigureAwait(false);
            if (result != 0)
            {
                // A 3.8 server follows the word with a reason; it's the server's own unauthenticated
                // text, and the connection is over anyway, so it isn't read.
                throw new AuthenticationFailedException(
                    "The Mac didn't accept that user name and password. If they're right, make sure this account is allowed: in the Mac's Sharing settings, click ⓘ next to Screen Sharing (or Remote Management, where \"All users\" isn't enough: add the account and allow Observe and Control).",
                    credentialsRejected: true)
                {
                    Method = method,
                    DiffieHellmanOffered = diffieHellmanOffered,
                };
            }

            _options.StageChanged?.Invoke(SessionStage.Encrypting);
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
            _options.StageChanged?.Invoke(SessionStage.Ready);
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

        // Nothing secret has been sent yet; the user (or the pin) decides before anything is. The
        // handshake clock stops while a person is reading the prompt.
        bool trusted;
        _deadline?.Pause();
        try
        {
            trusted = _options.IdentityVerifier is not null && await _options.IdentityVerifier.VerifyAsync(identity, ct).ConfigureAwait(false);
        }
        finally
        {
            _deadline?.Resume();
        }
        if (!trusted)
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
        byte[]? credentials = null, reply = null, wrapKey = null;
        try
        {
            (credentials, byte[] clientPublic, wrapKey) = ArdDiffieHellman.Respond(new(generator, prime, serverPublic), user, pass);
            reply = [.. credentials, .. clientPublic];
            await WriteRawAsync(reply, ct).ConfigureAwait(false);
            byte[] result = wrapKey;
            wrapKey = null;
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(user);
            CryptographicOperations.ZeroMemory(pass);
            if (credentials is not null) CryptographicOperations.ZeroMemory(credentials);
            if (reply is not null) CryptographicOperations.ZeroMemory(reply);
            if (wrapKey is not null) CryptographicOperations.ZeroMemory(wrapKey); // the send failed
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
        // in clear would break the session. Pasteboard monitoring only works when started here,
        // so it always is; but the Mac only ever says "my clipboard changed", and Vesper fetches
        // the contents only while clipboard sharing is on.
        using var prelude = new MemoryStream();
        prelude.Write(AppleMessages.ViewerInfoMessage());
        prelude.Write(AppleMessages.SetModeMessage(control: !observeOnly && !_options.ViewOnly));
        prelude.Write(AppleMessages.AutoPasteboardMessage(start: true));
        prelude.Write(AppleMessages.SetEncryptionStart());
        prelude.Write(AppleMessages.SetEncryptionInbound());
        await WriteRawAsync(prelude.ToArray(), ct).ConfigureAwait(false);

        var (key, iv) = await AwaitRekeyAsync(wrapKey, ct).ConfigureAwait(false);
        try
        {
            _reader.SwitchSource(leftover => _records = new RecordReaderStream(_transport, key, iv, leftover));
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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var token = linked.Token;
        var output = _out ?? throw new InvalidOperationException("Not connected.");
        var writer = _writeTask = Task.Run(() => output.RunAsync(token), token);

        output.Enqueue(SetPixelFormatMessage(PixelFormat.Bgrx32));
        output.Enqueue(EncodingsMessage());
        if (_apple && _fitPixels is null && _options.ServerScale is double scale && scale > 0 && scale < 1)
        {
            _requestedScale = scale;
            output.Enqueue(AppleMessages.SetServerScalingMessage(scale));
        }
        RequestUpdate(incremental: false);

        var reader = _readTask = Task.Run(() => ReadLoopAsync(token), token);
        var first = await Task.WhenAny(reader, writer).ConfigureAwait(false);
        CancelLifetime();
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
            // Adaptive lists the adaptive codec and RGB 5-5-5 first, as Apple's viewer does.
            if (_color == ColorQuality.Adaptive) list.AddRange([AppleMessages.EncodingMvs, AppleMessages.EncodingRgb555Zlib]);
            if (_color == ColorQuality.Thousands) list.Add(AppleMessages.EncodingRgb555Zlib);
            if (_color == ColorQuality.Grayscale) list.Add(AppleMessages.EncodingGrayZlib);
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

    /// <summary>Sends SetEncodings, and from then on accepts exactly what was asked for.</summary>
    private byte[] EncodingsMessage()
    {
        int[] list = EncodingList();
        // Updates already in flight may still use an earlier list, so the accepted set only grows.
        _requested = [.. _requested, .. list];
        return SetEncodingsMessage(list);
    }

    /// <summary>
    /// Waits for the Mac's first encrypted record. It can only check out if the Mac recovered the
    /// session keys, which came wrapped under the sign-in's secret: for RSA sign-in, proof that the
    /// Mac holds its identity key's private half, and the point at which a new key may be pinned.
    /// </summary>
    private async Task AwaitFirstRecordAsync(CancellationToken ct)
    {
        if (_apple)
        {
            try
            {
                await _reader.EnsureAsync(1, ct).ConfigureAwait(false);
            }
            catch (ProtocolException)
            {
                throw new SecurityRefusedException(RefusalReason.IdentityNotProven,
                    "The Mac's first encrypted message didn't check out, so it never proved it holds the " +
                    (Info.Identity is null ? "sign-in's secret" : "identity key it presented") + ", and Vesper stopped. " +
                    "Either someone tampered with the connection, or this Mac encrypts differently from what Vesper expects. If it keeps happening on a network you trust, try the SSH tunnel.");
            }
            if (Info.Identity is { } identity) _options.IdentityVerifier?.Proven(identity);
        }
        Secured?.Invoke();
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        await AwaitFirstRecordAsync(ct).ConfigureAwait(false);
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
            if (!_requested.Contains(encoding))
            {
                throw new ProtocolException($"The Mac sent encoding {encoding}, which Vesper didn't ask for.");
            }

            switch (encoding)
            {
                case EncodingLastRect:
                    i = rects;
                    continue;
                case EncodingDesktopSize:
                    // A Mac normally reports size through its display layout; older ones may not.
                    ResizeFramebuffer(w, h);
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
                    // The older display list: u16 w, u16 h, u32 flags, u16 count, 0x1c per display.
                    // Whether its size is in points or pixels isn't known, so it's stepped over;
                    // sizes come from the layout, ServerInit or DesktopSize.
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
            if (encoding != _lastPixelEncoding)
            {
                _lastPixelEncoding = encoding;
                Stats.Codec = CodecName(encoding);
            }
            switch (encoding)
            {
                case EncodingRaw: await StandardDecoders.RawAsync(_reader, Framebuffer, rc, _decode, ct).ConfigureAwait(false); break;
                case EncodingCopyRect: await StandardDecoders.CopyRectAsync(_reader, Framebuffer, rc, ct).ConfigureAwait(false); break;
                case EncodingRre: await StandardDecoders.RreAsync(_reader, Framebuffer, rc, ct).ConfigureAwait(false); break;
                case EncodingHextile: await StandardDecoders.HextileAsync(_reader, Framebuffer, rc, ct).ConfigureAwait(false); break;
                case EncodingZlib: await StandardDecoders.ZlibAsync(_reader, Framebuffer, rc, _decode, ct).ConfigureAwait(false); break;
                case EncodingZrle: await ZrleDecoder.DecodeAsync(_reader, Framebuffer, rc, _decode, ct).ConfigureAwait(false); break;
                case AppleMessages.EncodingMvs when _apple: await _decode.Mvs.DecodeAsync(_reader, Framebuffer, rc, _decode, ct).ConfigureAwait(false); break;
                case AppleMessages.EncodingRgb555Zlib when _apple: await AppleZlibDecoders.Rgb555Async(_reader, Framebuffer, rc, _decode, ct).ConfigureAwait(false); break;
                case AppleMessages.EncodingGrayZlib when _apple: await AppleZlibDecoders.Gray4Async(_reader, Framebuffer, rc, _decode, ct).ConfigureAwait(false); break;
                default:
                    throw new ProtocolException($"The Mac sent encoding {encoding}, which Vesper didn't ask for.");
            }
        }

        if (pixels) Framebuffer.Commit();
        RecordUpdate(started, pixels);

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

    /// <summary>A line for the diagnostic log: the picture's size and codec, and how the adaptive codec behaved.</summary>
    public string DescribePicture()
    {
        var mvs = _decode.Mvs;
        string detail = mvs.BasePasses + mvs.Refinements == 0 ? ""
            : $"; adaptive: {mvs.BasePasses} base passes, {mvs.Refinements} refinements, {mvs.QuantizationUpdates} table updates, luma q {string.Join('/', mvs.LuminanceQuantization[..4].ToArray())}";
        return $"{Framebuffer.Width}×{Framebuffer.Height}, {(Stats.Codec.Length > 0 ? Stats.Codec : "no picture")}{detail}";
    }

    private static string CodecName(int encoding) => encoding switch
    {
        AppleMessages.EncodingMvs => "MVS",
        EncodingZlib => "zlib",
        EncodingZrle => "ZRLE",
        AppleMessages.EncodingRgb555Zlib => "RGB 5-5-5",
        AppleMessages.EncodingGrayZlib => "grey",
        EncodingRaw => "raw",
        EncodingHextile => "Hextile",
        EncodingRre => "RRE",
        EncodingCopyRect => "CopyRect",
        _ => encoding.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private void RecordUpdate(long startedTicks, bool pixels)
    {
        long now = _clock.ElapsedTicks;
        double costMs = (now - startedTicks) * 1000.0 / Stopwatch.Frequency;
        if (pixels)
        {
            _windowUpdates++;
            _windowDecodeMs += costMs;
            PacePushes(costMs);
        }
        double windowSeconds = (now - _windowStart) / (double)Stopwatch.Frequency;
        if (windowSeconds >= 1.0)
        {
            // Bytes are counted for the whole window, not per update: the reader pulls data off the
            // wire ahead of each update (while waiting for the next message type), so per-update
            // deltas would miss most of it.
            long total = _reader.BytesReceived;
            Stats.UpdatesPerSecond = _windowUpdates / windowSeconds;
            Stats.MegabitsPerSecond = (total - _windowStartBytes) * 8 / windowSeconds / 1e6;
            Stats.DecodeMilliseconds = _windowUpdates > 0 ? _windowDecodeMs / _windowUpdates : 0;
            Stats.BytesReceived = total;
            Stats.BytesSent = _out?.BytesSent ?? 0;
            _windowStart = now;
            _windowStartBytes = total;
            _windowUpdates = 0;
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
        RequestMatchingScale();
        LayoutChanged?.Invoke(layout);
    }

    /// <summary>True while a requested server scale hasn't yet been confirmed by a layout.</summary>
    public bool ScalePending => _apple && Layout is { } l && Math.Abs(l.ServerScale - _requestedScale) > 0.01;

    /// <summary>
    /// Changes the pixels the Mac's screen should fit (after the window moved to another
    /// monitor), or null for every native pixel.
    /// </summary>
    public void FitToPixels((int Width, int Height)? target)
    {
        _fitPixels = target;
        if (target is null && _apple && _requestedScale != 1.0)
        {
            _requestedScale = 1.0;
            _out?.Enqueue(AppleMessages.SetServerScalingMessage(1.0));
            return;
        }
        RequestMatchingScale();
    }

    /// <summary>
    /// Asks the Mac to render its screen (all of it, or the display selected) just large enough
    /// to fill the target pixels, and never above its native pixels. Full screen on that display
    /// is then exactly one framebuffer pixel per screen pixel: no resampling, so no blur.
    /// </summary>
    private void RequestMatchingScale()
    {
        if (!_apple || _out is null || Layout is not { } layout || _fitPixels is not (int targetW, int targetH)) return;
        if (layout.ServerScale <= 0 || layout.FramebufferWidth <= 0 || layout.FramebufferHeight <= 0) return;
        double nativeW = layout.FramebufferWidth / layout.ServerScale, nativeH = layout.FramebufferHeight / layout.ServerScale;
        // A hair over the exact ratio, so the Mac's rounding can't land a pixel short of the target.
        double wanted = Math.Clamp(Math.Min(targetW / nativeW, targetH / nativeH) + 1e-6, 0.1, 1.0);
        if (Math.Abs(wanted - layout.ServerScale) * Math.Max(nativeW, nativeH) < 0.75)
        {
            _requestedScale = layout.ServerScale; // already there, to the pixel
            return;
        }
        if (Math.Abs(wanted - _requestedScale) < 1e-6) return; // asked already; never loop on what the Mac granted
        _requestedScale = wanted;
        _out.Enqueue(AppleMessages.SetServerScalingMessage(wanted));
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
        if (model != MacModel && model.All(c => char.IsAsciiLetterOrDigit(c) || c == ','))
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
        if (length < 0 || size > MaxCutText || !_clipboardFromMac)
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
            case AppleMessages.StatusPasteboardChanged when _clipboardFromMac:
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
        if (!_clipboardFromMac || compressed > ApplePasteboard.MaxArchiveBytes || uncompressed > ApplePasteboard.MaxArchiveBytes)
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
        if (!InputAllowed || !double.IsFinite(dxPoints) || !double.IsFinite(dyPoints)) return;
        // However far one gesture claims to go, a screenful or so at a time.
        const double MaxPoints = 4000;
        _scrollResidualX = Math.Clamp(_scrollResidualX + dxPoints, -MaxPoints, MaxPoints);
        _scrollResidualY = Math.Clamp(_scrollResidualY + dyPoints, -MaxPoints, MaxPoints);
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
        if ((held & PointerButtons.Middle) != 0) baseMask |= (byte)(_apple ? 4 : 2);
        if ((held & PointerButtons.Right) != 0) baseMask |= (byte)(_apple ? 2 : 4);
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

    /// <summary>
    /// Turns receiving the Mac's clipboard on or off mid-session. Only later changes on the Mac come
    /// across; turning it on doesn't pull the Mac's old clipboard over the one on Windows.
    /// </summary>
    public void SetClipboardFromMac(bool enabled) => _clipboardFromMac = enabled;

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

    /// <summary>Switches colour depth mid-session; the Mac repaints in the new encoding.</summary>
    public void SetColorQuality(ColorQuality color)
    {
        if (!_apple || _out is null || color == _color) return;
        _color = color;
        _out.Enqueue(EncodingsMessage());
        RequestUpdate(incremental: false);
    }

    public ColorQuality Color => _color;

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
        if (length > 4096) return "(too long to show)";
        byte[] reason = await _reader.ReadBytesAsync((int)length, ct).ConfigureAwait(false);
        return Printable(Encoding.UTF8.GetString(reason), 200);
    }

    /// <summary>Server text with control and formatting characters (bidi overrides included) removed, cut to a length.</summary>
    internal static string Printable(string text, int maxLength)
    {
        var sb = new StringBuilder(Math.Min(text.Length, maxLength));
        foreach (char c in text)
        {
            if (sb.Length >= maxLength) { sb.Append('…'); break; }
            var category = char.GetUnicodeCategory(c);
            if (char.IsWhiteSpace(c)) { if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' '); }
            else if (category is not (System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format)) sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    private async Task WriteRawAsync(byte[] data, CancellationToken ct)
    {
        await _transport.WriteAsync(data, ct).ConfigureAwait(false);
        await _transport.FlushAsync(ct).ConfigureAwait(false);
    }

    private void CancelLifetime()
    {
        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        CancelLifetime();
        try
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Already gone.
        }

        // The receive loop may be in the middle of a decode, and the send loop in the middle of a
        // record: let them stop before freeing the zlib state and key material they're using.
        foreach (var task in new[] { _readTask, _writeTask })
        {
            if (task is null) continue;
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // Observed by RunAsync; here it only matters that the task has stopped.
            }
        }
        _out?.Dispose();
        _records?.Dispose();
        _decode.Dispose();
        // _lifetime isn't disposed: RunAsync may still be unwinding on another thread, and a
        // CancellationTokenSource without a timer holds nothing that needs releasing.
    }
}

/// <summary>A timeout that can be paused while the user reads a trust prompt.</summary>
internal sealed class Deadline : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly Timer _timer;
    private TimeSpan _remaining;
    private long _startedTicks;

    public Deadline(CancellationTokenSource cts, TimeSpan timeout)
    {
        _cts = cts;
        _remaining = timeout;
        _timer = new Timer(_ => { try { _cts.Cancel(); } catch (ObjectDisposedException) { } });
        Resume();
    }

    public void Pause()
    {
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        _remaining -= Stopwatch.GetElapsedTime(_startedTicks);
        if (_remaining < TimeSpan.FromSeconds(5)) _remaining = TimeSpan.FromSeconds(5);
    }

    public void Resume()
    {
        _startedTicks = Stopwatch.GetTimestamp();
        _timer.Change(_remaining, Timeout.InfiniteTimeSpan);
    }

    public void Dispose() => _timer.Dispose();
}
