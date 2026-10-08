# Vesper

**A fast, secure Apple Remote Desktop client for Windows.** Built for one job: putting your
Mac's screen on your PC as if you were sitting at it, without the lag of plain VNC and without
leaking a thing.

Vesper speaks Apple's own Screen Sharing protocol (RFB 003.889), not generic VNC. There's
nothing to install on the Mac. No accounts, no telemetry, no cloud, no update checks: it talks
only to the Macs you add.

![A session](docs/screenshots/session.png)

> Screenshots show FakeMac, Vesper's built-in stand-in for a Mac (see [Testing](#testing-without-a-mac)).

---

## Why it's fast where VNC lags

A Mac answers plain VNC clients the slow way. Vesper uses the parts of Apple's protocol that
Apple's own viewer (and Remote Desktop Manager's ARD mode) use:

| | Plain VNC | Vesper |
|---|---|---|
| Screen updates | Polled; a Mac answers incremental requests late (2–9 updates/s while scrolling) | **Pushed** by the Mac as the screen changes (`AutoFrameBufferUpdate`), paced so input never queues behind pictures |
| The picture | Raw pixels through zlib: megabytes per frame, which is what makes macOS VNC crawl on a Retina screen | Apple's **adaptive codec** (MVS), as Apple's viewer and Remote Desktop Manager use: JPEG-like 8×8 tiles, copies and a tile cache, arriving coarse and sharpening in a moment. A full 2560×1440 frame is a few hundred KB, decoded on every core |
| Retina displays | Every native pixel: a 5K Studio Display is 14.7 million pixels per frame | The Mac **scales on its side** to your PC's pixel density first; a 5K screen viewed from a 100% display arrives as 2560×1440, a quarter of the data |
| Mouse pointer | Drawn into the picture, so it moves at the network's pace | The Mac sends its pointer images; Vesper makes them **real Windows hardware cursors** that move at your mouse's own rate |
| Scrolling | Wheel "clicks" the Mac turns into ~2 px nudges | Apple's **precise scroll** message: smooth, trackpad-like, both axes |
| Drawing | CPU blits | GPU (Win2D/Direct2D) on its own thread; only changed rectangles are uploaded; vsync |
| Network | Nagle on | TCP_NODELAY, coalesced pointer moves, large receive buffers |

## Security

Your Mac's screen, keystrokes and password are the most sensitive things on your network.
Vesper's defaults assume a hostile network and an untrusted PC environment. See
[SECURITY.md](SECURITY.md) for the full threat model.

- **Encrypted end to end, always.** Every session with a Mac runs inside Apple's AES record layer,
  for the whole picture and every keystroke. Vesper always turns it on, while Apple's viewer
  leaves it off by default. A server that won't encrypt is refused before anything is sent
  (other VNC servers work only inside the SSH tunnel).
- **The Mac proves who it is.** Vesper signs in with the Mac's RSA identity key (Apple's
  security type 33) and **pins it on first use**, like SSH, but only once the Mac has proven it
  holds the private key, so replaying a Mac's public key gets an impersonator nowhere. If the
  key ever changes, Vesper stops *before your password is sent* and says why.
- **Optional SSH tunnel** through the Mac's Remote Login, with a pinned host key. It's a
  direct-tcpip channel handed straight to the session, so no local port is opened and no other
  program on your PC can use the tunnel.
- **No downgrades.** Apple's older Diffie-Hellman sign-in can't prove the Mac's identity, so
  it's only used where something else does: inside the SSH tunnel, or straight to the Mac's own
  Tailscale address (not through a subnet router, exit node or other VPN). Otherwise it needs
  your explicit permission for that Mac, and even then never for a Mac that has proven its
  identity key before.
- **Passwords** are sealed with AES-256-GCM under a key protected by Windows (DPAPI), and
  optionally **locked behind Windows Hello**, so another program running as you can't read them
  without a visible prompt. Each one is sealed together with where it may go, so a Mac's
  settings edited behind Vesper's back don't get the saved password. Unlocked passwords are
  wiped from memory after 15 idle minutes.
- **Hidden from screen capture by default.** Session windows show up black to screenshots,
  screen recording, Teams or Zoom screen sharing, and **Windows Recall**.
- **Clipboard sharing is off** unless you turn it on for a Mac. Even then it only works while
  the session window is in front, copies your password manager marks private stay on Windows,
  and text from the Mac is kept **out of Windows clipboard history and cloud clipboard sync**.
- **No leaks from the app itself.** Nothing is logged to disk, and Windows Error Reporting is
  disabled for the process, since a crash dump would hold screen contents and session keys.
  Legacy DLL injection points (AppInit DLLs, global hook DLLs, Winsock LSPs) and DLLs from the
  current directory or network shares are refused.
- **Built defensively.** Every length and rectangle from the Mac is bounds-checked before it's
  used. Decompression is capped (no zip bombs). Unknown messages end the session rather than
  being guessed at. Pinned NuGet packages are verified against lock files, and known-vulnerable
  packages fail the build.

![Identity change warning](docs/screenshots/identity-changed.png)

## Setting up the Mac

1. On the Mac: **System Settings › General › Sharing**, turn on **Screen Sharing** (or
   **Remote Management**).
2. Click **ⓘ** next to it and make sure your account is allowed. With Remote Management,
   "All users" isn't enough: add your account and allow **Observe** and **Control**.
3. *(Optional, for the SSH tunnel)* turn on **Remote Login** too.
4. In Vesper: **Add a Mac** with its name (`studio.local`) or IP address and your Mac account.

The first time you connect, Vesper shows the Mac's identity fingerprint and asks you to trust
it. From then on it's silent unless the key changes.

| | |
|---|---|
| ![First run](docs/screenshots/first-run.png) | ![Add a Mac](docs/screenshots/add-mac.png) |
| ![Home](docs/screenshots/home.png) | ![Settings](docs/screenshots/settings.png) |

## Using it

- **Ctrl is ⌘ Command** by default (switchable per Mac): Ctrl+C, Ctrl+V, Ctrl+Tab and Ctrl+Q
  do on the Mac what they do on Windows. Alt is ⌥ Option, and the Windows key is ⌃ Control.
- **Full screen** with the toolbar button or **Ctrl+Alt+Enter**. In full screen, Alt+Tab and the
  Windows key go to the Mac, like Remote Desktop. **Ctrl+Alt+Home** always hands them back to
  Windows.
- The **keyboard menu** sends Spotlight, Mission Control, Force Quit, Screenshot and Lock Screen.
- The **display menu** switches between fitting the window and 1:1 pixels, between scaled and
  native resolution, between Adaptive (Apple's fast codec, the default), lossless full colour,
  thousands of colours and greyscale, and between the Mac's displays. With connection stats on
  (Settings), the session shows its frame rate, bandwidth, latency and the codec in use.

## Building

Requirements: Windows 10 2004+ (Windows 11 recommended), .NET SDK 10.0.1xx, and Visual
Studio 2022 17.14 or newer (only for its MSBuild components; `dotnet build` works too).

```powershell
dotnet build Vesper.slnx -p:Platform=x64
dotnet test tests\Vesper.Core.Tests
.\scripts\publish.ps1            # self-contained Release build in .\publish\win-x64
.\scripts\install.ps1            # copies it to %LOCALAPPDATA%\Programs\Vesper with a Start menu entry
```

## Testing without a Mac

`tools/Vesper.FakeMac` is a loopback-only stand-in for macOS Screen Sharing. It has Apple's
handshake, RSA and Diffie-Hellman sign-in, the encrypted record layer, display layout, the
cursor cache, pushed updates and the pasteboard. It draws a desktop that shows every key,
click, scroll and clipboard change it receives. It can also misbehave on purpose: speak plain
VNC (`--rfb38`), refuse RSA logins (`--reject-rsa`), or impersonate a Mac by replaying its
public key without the private key (`--impersonate`).

```powershell
dotnet run --project tools\Vesper.FakeMac -- --animate --ssh-port 2222
# then add a Mac in Vesper: 127.0.0.1, port 5959, account "vesper", password "evening-star"
```

The test suite runs Vesper's session against it over real sockets, including through the SSH
tunnel, and checks the Diffie-Hellman maths against noVNC's published test vector.

## How it's built

```
src/Vesper.Core     the protocol: handshake, sign-in, record layer, decoders, SSH transport
src/Vesper.App      the WinUI 3 app: windows, GPU renderer, input, vault, clipboard
tests/              unit and end-to-end tests (against FakeMac)
tools/Vesper.FakeMac
docs/PROTOCOL.md    Apple's protocol as Vesper implements it, with sources
```

## Credits

Apple doesn't document RFB 003.889. Vesper's implementation rests on others' careful
reverse engineering, especially **remotex** (MIT, measurements of macOS 26), **ard-rs** (MIT,
whose adaptive-codec decoder Vesper ports; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)), **noVNC** and its
**noVNC-ARD** fork, and **libvncclient**. See [docs/PROTOCOL.md](docs/PROTOCOL.md).

## The name

Vesper is the evening star: Hesperus to the Greeks. The Hesperides, "nymphs of the evening",
guarded the golden apples in a garden at the western edge of the world, beside Atlas. A
guarded way into your Apple.
