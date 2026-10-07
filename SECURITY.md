# Security model

Vesper puts the screen and keyboard of a Mac on a Windows PC. Everything that crosses between
them can be sensitive: what's on screen, what you type (including passwords), and the account
password used to sign in. This document is what Vesper defends against, how, and where the
limits are.

## Threats and defences

### Someone on the network

**Passive eavesdropping** (shared Wi-Fi, a compromised router, an ISP).
Every session with a Mac uses Apple's record layer: after sign-in, both directions are AES-128-CBC with a
per-record SHA-1 integrity trailer and a sequence number that never resets, so records can't
be read, altered, reordered or replayed unnoticed. Vesper turns it on for every session; Apple's
own viewer leaves it off by default. A server that answers as plain VNC (RFB 3.8) has no record
layer, so Vesper refuses it before sending anything, unless the session runs inside the SSH
tunnel. Inside an SSH tunnel the session is additionally wrapped in SSH (ECDH key exchange,
AES-256-GCM).

**Active interception** (ARP spoofing, a rogue access point, DNS tampering).
The answer is authenticating the Mac before anything secret is sent:

- *Direct connections* use Apple's RSA sign-in (security type 33). The Mac presents its RSA
  identity key, at least 2048 bits. Vesper compares its SHA-256 fingerprint with the pinned
  one on every connection. On a mismatch, the connection stops before the credential block is
  built: no password, encrypted or not, leaves the PC. Trusting a changed key takes an explicit
  "I know why this changed" confirmation.
- *Presenting a key proves nothing on its own*: anyone can ask a Mac for its public key and
  replay it. The proof comes later. The password and a random wrap key go to the Mac encrypted
  to that key, and the Mac sends the session keys wrapped under the wrap key, so only the
  holder of the private key can produce a first encrypted record that checks out. Vesper uses
  RSA sign-in only on Apple's 003.889 protocol, where that record exists. It shows the screen,
  accepts input, saves a typed password and pins a newly trusted key only after that record
  checks out. A server that replays a Mac's key gets nothing it can read, and isn't remembered.
- *SSH tunnels* pin the SSH host key the same way, and stop before password authentication.
  SSH's key exchange checks the host's signature before the key is offered for pinning.
- *Apple's Diffie-Hellman sign-in* (type 30) has no server authentication. An active attacker
  could run the exchange with both ends and read the password. Vesper uses it only where the
  path itself authenticates the Mac:
  - inside a pinned SSH tunnel;
  - straight to the Mac's own Tailscale address (100.64.0.0/10 or fd7a:115c:a1e0::/48, reached
    through this PC's Tailscale adapter), where WireGuard proves which machine answers.
    Traffic that only passes through a VPN doesn't count: a Tailscale subnet router or exit
    node, a commercial VPN or a WireGuard tunnel to a network all leave the last hop open.
    Loopback doesn't count either, since a forwarded port can lead anywhere.

  The only other case is a Mac the user explicitly allows it for ("Allow Apple's older sign-in
  on unprotected networks", off by default). Even then it's refused for a Mac that has ever
  proven its identity key, and the session's security indicator turns amber.
- *Downgrade attempts*: an attacker who strips type 33 from the offer, or answers an RSA login
  with "rejected", can't push Vesper to Diffie-Hellman on an unprotected network. A Mac with a
  pinned identity key that stops offering RSA sign-in gets a warning, not a fallback. The
  single retry with Diffie-Hellman after a refused RSA login happens only where Diffie-Hellman
  is allowed anyway. Vesper remembers that a Mac refuses RSA sign-in only when it learned that
  over an authenticated path, and the Mac's editor can reset it.

**Trust on first use** means the very first connection to a Mac can't be verified this way.
Make the first connection on a network you trust (your home LAN, or over Tailscale). For the
SSH host key, compare the fingerprint with `ssh-keygen -lf /etc/ssh/ssh_host_ecdsa_key.pub` on
the Mac, as the prompt suggests.

**Apple's cleartext handshake.** Everything before the record layer starts (the version, the
offered sign-ins, ServerInit with the desktop name, size and "observe only" flag, and Vesper's
prelude) travels in clear, as Apple designed it. Someone in the middle can alter those bytes
to make the session fail or start in observe-only mode, but can't read or change anything
after it.

### A hostile or impersonated server

Everything the server sends is untrusted input:

- every length, count and rectangle is validated against hard limits and the framebuffer bounds
  before memory is touched (screens up to 16384², pointer images up to 512², names up to 64 KiB,
  pasteboard archives up to 64 MiB);
- decompression output is capped at what the rectangle could legitimately need, so zip bombs
  stop early;
- ZRLE runs, palette indices and subrectangles are checked against their tile;
- unknown message types end the session rather than being skipped by guesswork, and so does
  any encoding Vesper didn't ask for;
- RRE subrectangles can't repaint their rectangle more than a few times over, and screens are
  capped at 2²⁷ pixels, so a hostile server can't make Vesper spin or allocate gigabytes;
- text the server sends before sign-in (a refusal reason) is stripped of control and
  bidirectional-override characters and kept to the diagnostic log, never shown as Vesper's own
  words;
- Diffie-Hellman parameters are checked (odd prime, sane generator, public value in [2, p−2],
  key length 64–1024 bytes); RSA keys under 2048 bits are refused;
- the record layer releases no byte before its record's integrity check passes, compares
  trailers in constant time, and treats any failure as final.

The decoders are written in C#, so out-of-range accesses throw instead of corrupting memory.

### Other software on this PC

- **Saved passwords** live in `%LOCALAPPDATA%\Vesper\vault.bin`, sealed with AES-256-GCM (the
  header is authenticated) under a random 256-bit vault key. That key is protected by DPAPI
  (CurrentUser, with Vesper-specific entropy), so the file is useless on another account or
  another machine. With **Require Windows Hello** on, the vault key is also wrapped under a key
  derived (HKDF-SHA-256) from a Windows Hello signature. Hello keys are TPM-backed RSA with
  repeatable PKCS#1 signatures, so the vault can't be opened without a visible face, fingerprint
  or PIN prompt, even by malware running as you. Unlocked passwords are wiped from memory after
  the configured idle time with no session open. With Hello required, a vault that doesn't need
  Hello isn't one Vesper made: it's set aside unopened, and the user is warned.
- **Profiles** (`macs.json`) contain no secrets: host names, settings, and pinned public-key
  fingerprints. Any program running as you can edit them, so each saved password is sealed
  inside the vault together with a fingerprint of where it may go: the address, ports, account,
  transport, sign-in allowance and pinned keys. If a profile no longer matches, Vesper asks for
  the password instead of sending the saved one. Changes made through Vesper carry the password
  over, but only from the settings it was sealed for. (A program that can edit your files can
  also read what you type, so this stops the quiet route, not every route.)
- **Typed passwords** are remembered only after the Mac has accepted them and proven itself.
- **Screen capture**: session windows are excluded from capture by default
  (`WDA_EXCLUDEFROMCAPTURE`). Screenshot tools, screen recorders, meeting apps and Windows
  Recall see a black window.
- **Clipboard**: off by default. When on, Windows' clipboard is sent to the Mac only while the
  session window is in front, never from the background. Anything the copying app marked
  private (`ExcludeClipboardContentFromMonitorProcessing`, or clipboard history or cloud sync
  turned off for it, as password managers do) stays on Windows. Text from the Mac lands on the
  Windows clipboard only while the session window is in front (or within a second of leaving
  it), so the Mac can't swap what you copied in another app. It's marked not to be kept in
  clipboard history or synced to the cloud, and flagged for third-party clipboard managers to
  ignore.
- **Keyboard hook**: a low-level hook exists only while shortcuts are being captured (full
  screen, by default) and the session has focus. It's removed the moment the session loses
  focus, and it records nothing. It keeps a key's release from Windows only if it kept the
  press, so a key already held when the hook went in is released in Windows too. Held keys are
  released on the Mac whenever focus leaves, so nothing sticks either side.
- **Process hardening**: the current directory is removed from the DLL search path; images
  from remote shares and low-integrity locations are refused; legacy injection points
  (AppInit_DLLs, global window-hook DLLs, Winsock LSPs) are disabled; and Windows Error
  Reporting is disabled for Vesper, because a crash dump would contain screen contents, typed
  keys and session keys.
- **Memory hygiene**: credential blocks, wrap keys (also when a send fails), DH secrets, sent
  key events and send buffers are zeroed after use, and the session drops its reference to the
  password once sign-in is over. Closing a session waits for its receive and send loops to stop
  before freeing the decompressors and keys they use. .NET strings can't be wiped, so the
  password string itself can outlive its use until garbage collection; that's a limit of the
  platform.

### Leaks from Vesper itself

- No telemetry, analytics, crash reporting, update checks or accounts. The only outbound
  connections go to the Macs you add (plus local-network DNS-SD listening for "Macs on this
  network", which you can turn off).
- Nothing is logged to disk in release builds. A short diagnostic log (connection results, never
  credentials, keys, keystrokes or clipboard contents) is kept in memory and shown in
  Settings › About.
- Dependencies are few and Microsoft-published (Windows App SDK, Win2D,
  Microsoft.DevTunnels.Ssh). Versions are pinned centrally, restores are checked against lock
  files, and `NuGetAudit` fails the build on any known vulnerability.

## Limits

- **Apple's record layer is Apple's design.** It's AES-128-CBC with a plain SHA-1 trailer inside
  the encryption, not a modern AEAD. It's sound against the attacks above, but if you want
  standard, modern cryptography end to end, use the SSH tunnel (or Tailscale), which wraps it.
- **Apple's RSA sign-in uses PKCS#1 v1.5** to send the wrap key, because that's what the Mac
  accepts. If the Mac's side of it leaked padding errors, an attacker who recorded a session
  could in principle work on that key with many crafted connections to the Mac. That's on
  Apple's side and not something a client can fix; the SSH tunnel or Tailscale put that
  exchange out of an eavesdropper's reach.
- **Trust on first use** can't protect the very first connection (see above).
- **Malware running as you** can edit Vesper's files, and with a per-user install, Vesper
  itself. Vesper detects the tampering it can (profiles that no longer match their passwords, a
  vault without the Hello protection you chose), but it can't make a compromised account safe.
- **Malware with full control of your Windows account** can read the screen of an
  already-connected session the way it can read anything else you see, and it can capture what
  you type before Vesper does. Vesper raises the bar (capture exclusion, Hello-bound vault,
  injection hardening) but can't make a compromised PC safe.
- **The Mac** has to be trustworthy too: Vesper protects the connection, not the Mac.

## Reporting

This is a personal project. If you find a problem, open an issue in this repository. If it's
sensitive, describe it in general terms and ask for a private channel.
