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
own viewer leaves it off by default. Inside an SSH tunnel the session is additionally wrapped in
SSH (ECDH key exchange, AES-256-GCM).

**Active interception** (ARP spoofing, a rogue access point, DNS tampering).
The answer is authenticating the Mac before anything secret is sent:

- *Direct connections* use Apple's RSA sign-in (security type 33). The Mac presents its RSA
  identity key, at least 2048 bits. Vesper pins its SHA-256 fingerprint on first use and
  compares it on every connection. On a mismatch, the connection stops before the credential
  block is built: no password, encrypted or not, leaves the PC. Trusting a changed key takes an
  explicit "I know why this changed" confirmation.
- *SSH tunnels* pin the SSH host key the same way, and stop before password authentication.
- *Apple's Diffie-Hellman sign-in* (type 30) has no server authentication. An active attacker
  could run the exchange with both ends and read the password. Vesper uses it only where the
  path is already authenticated: inside a pinned SSH tunnel, over a WireGuard VPN (Tailscale or
  WireGuard, detected from the adapter the socket is actually bound to, not from the address),
  on loopback, or for a Mac the user explicitly allows ("Allow Apple's older sign-in on
  unprotected networks", off by default). The session's security indicator turns amber when
  that last case is in use. A server that isn't a Mac (plain RFB 3.8) has no Apple encryption at
  all. It's only reachable through the same opt-in, and the indicator then reads "Not encrypted"
  in red.
- *Downgrade attempts*: an attacker who makes RSA sign-in fail can't push Vesper to
  Diffie-Hellman on an unprotected network. The single retry with Diffie-Hellman after a refused
  RSA login happens only where Diffie-Hellman is already allowed (see above).

**Trust on first use** means the very first connection to a Mac can't be verified this way.
Make the first connection on a network you trust (your home LAN, or over Tailscale). For the
SSH host key, compare the fingerprint with `ssh-keygen -lf /etc/ssh/ssh_host_ecdsa_key.pub` on
the Mac, as the prompt suggests.

### A hostile or impersonated server

Everything the server sends is untrusted input:

- every length, count and rectangle is validated against hard limits and the framebuffer bounds
  before memory is touched (screens up to 16384², pointer images up to 512², names up to 64 KiB,
  pasteboard archives up to 64 MiB);
- decompression output is capped at what the rectangle could legitimately need, so zip bombs
  stop early;
- ZRLE runs, palette indices and subrectangles are checked against their tile;
- unknown message types and encodings end the session rather than being skipped by guesswork;
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
  the configured idle time with no session open.
- **Profiles** (`macs.json`) contain no secrets: host names, settings, and pinned public-key
  fingerprints.
- **Screen capture**: session windows are excluded from capture by default
  (`WDA_EXCLUDEFROMCAPTURE`). Screenshot tools, screen recorders, meeting apps and Windows
  Recall see a black window.
- **Clipboard**: off by default. When on, Windows' clipboard is sent to the Mac only while the
  session window is in front, never from the background. Text arriving from the Mac is placed on
  the Windows clipboard marked not to be kept in clipboard history or synced to the cloud, and
  flagged for third-party clipboard managers to ignore.
- **Keyboard hook**: a low-level hook exists only while shortcuts are being captured (full
  screen, by default) and the session has focus. It's removed the moment the session loses
  focus, and it records nothing. Held keys are released on the Mac whenever focus leaves, so
  nothing sticks.
- **Process hardening**: the current directory is removed from the DLL search path; images
  from remote shares and low-integrity locations are refused; legacy injection points
  (AppInit_DLLs, global window-hook DLLs, Winsock LSPs) are disabled; and Windows Error
  Reporting is disabled for Vesper, because a crash dump would contain screen contents, typed
  keys and session keys.
- **Memory hygiene**: credential blocks, wrap keys, DH secrets, sent key events and send
  buffers are zeroed after use. .NET strings can't be wiped, so the password string itself can
  outlive its use until garbage collection; that's a limit of the platform.

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
- **Trust on first use** can't protect the very first connection (see above).
- **Malware with full control of your Windows account** can read the screen of an
  already-connected session the way it can read anything else you see, and it can capture what
  you type before Vesper does. Vesper raises the bar (capture exclusion, Hello-bound vault,
  injection hardening) but can't make a compromised PC safe.
- **The Mac** has to be trustworthy too: Vesper protects the connection, not the Mac.

## Reporting

This is a personal project. If you find a problem, open an issue in this repository. If it's
sensitive, describe it in general terms and ask for a private channel.
