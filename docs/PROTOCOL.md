# Apple's Screen Sharing protocol, as Vesper speaks it

Apple doesn't document RFB 003.889. This is what Vesper implements, where each piece comes
from, and how confident that source is. Every byte layout here is exercised by Vesper's tests
against FakeMac. The type-30 maths is also checked against noVNC's published test vector.

**Sources.** *remotex* (MIT; `docs/apple-vnc-889.md`, measured against macOS 26.5–26.6),
*noVNC* and its *noVNC-ARD* fork (MPL-2.0), *libvncclient*, *ard-rs* (protocol facts only; no
code was taken from it), and the IANA RFB registry. Where sources disagree, the choice and the
reason are noted.

## Connecting

1. Server sends `RFB 003.889\n`. Vesper answers `RFB 003.889\n`, which turns on Apple's
   extensions. A server that isn't a Mac gets the plain RFB 3.8 path.
2. Security types: a one-byte count, then the types. macOS lists 30, 33, 36, (31, 32), (2), 35.
   Vesper picks **33** (RSA). It uses **30** (Diffie-Hellman) only where the transport already
   authenticates the Mac (see SECURITY.md).
3. Sign-in (below), then `SecurityResult`: a `u32`, 0 = success. On 003.889 a refusal is the
   word alone, with no reason string, so Vesper doesn't wait for one.
4. `ClientInit` = `0x81`: `0x80` asks for the extended ServerInit, `0x01` means shared.
5. Extended `ServerInit`: width, height, pixel format, then a "name" made of `u16 0`,
   `u32 flags` (bit 0 = observe only), a 16-byte bitmap of accepted client messages (type `t` is
   bit `7 − t % 8` of byte `t / 8`), and the UTF-8 name.
6. Cleartext prelude, written back to back: `ViewerInfo` (0x21, 66 bytes, numeric form),
   `SetMode(control)` (`0a 00 00 01`), `AutoPasteboard(start)` (`15 00 00 01 00 00 00 00`),
   `SetEncryption` command 1 (`12 00 00 01 00 01 00 01 00 00 00 01`), then command 2
   (`12 00 00 02 00 01 00 00`).
7. The Mac answers with a one-rectangle FramebufferUpdate, encoding `0x44f`, zero geometry:
   `u32 generation`, then the session key and initial IV, each one AES-128-ECB block under the
   sign-in's wrap key. Only Bell and MiscStatus may come before it. Everything after it, in both
   directions, is records.
8. Inside the records: `SetPixelFormat` (32bpp little-endian xRGB, which is B,G,R,x in memory),
   `SetEncodings`, a full update request, and `AutoFrameBufferUpdate` once the first display
   layout arrives.

## Sign-in

**Type 33, RSA plain login** (noVNC-ARD, reported working against live screensharingd; remotex
describes the same envelope):

```
client → u32 10 | 01 00 | "RSA1" | 00 00 | 00 00          key request (sent with the type byte)
server → u32 n  | 4 bytes (00 01 00 00) | u16 len | DER RSA public key | 00
client → u32 n  | 01 00 | "RSA1" | 00 01 | 128-byte credentials | u16le 256 | RSA(aesKey)
server → u32 0  (counted acknowledgement), then SecurityResult
```

The credential block is 128 bytes: random fill, then the UTF-8 user name at 0 and the password
at 64, each NUL-terminated and at most 63 bytes. It's encrypted with AES-128-ECB under a random
16-byte key, and that key is encrypted to the Mac with RSA PKCS#1 v1.5. The random key is the
wrap key for the rekey.

**Type 30, Diffie-Hellman** (libvncclient, noVNC, TigerVNC; six implementations agree):

```
server → u16 g | u16 L | prime (L bytes) | server public (L bytes)
client → 128-byte credentials (AES-128-ECB) | client public (L bytes)
```

The AES key is MD5 of the shared secret left-padded with zeros to L bytes. Dropping the leading
zeros makes a fraction of sign-ins fail. macOS 26 uses RFC 5054's 4096-bit group with g = 5
(L = 512); older Macs use smaller groups. L is accepted from 64 to 1024 bytes. The wrap key is
the same MD5.

## The record layer

```
wire       u16 ciphertext_len | ciphertext
plaintext  u16 body_len | body | zero filler | SHA1(u32_be(seq) | plaintext-before-trailer)
filler     (−(2 + body_len + 20)) mod 16 bytes
```

AES-128-CBC with one chain per direction for the whole session: each record's last ciphertext
block is the next record's IV. Each direction has its own record counter, starting at 0 and
never reset. A server message may span records, and the reader exposes the concatenation of
bodies as a stream. Vesper frames each client message as its own record(s).

## Speed

- **`AutoFrameBufferUpdate`** (0x09): `09 00 | u16 1 | u32 interval µs | u16 x, y, w, h`. The Mac
  pushes changes in the region at most once per interval (remotex measured 1,000,000 as about
  one push a second; ard-rs reads the unit as milliseconds, but remotex's measurement settles
  it). Without it, a physical display answers incremental requests late. Vesper arms 16,667 µs
  (60 per second) and widens it while an update takes longer than that to receive and decode,
  because the Mac reads no input while it writes an update. It's re-armed after every display
  layout, and one incremental request is always kept outstanding, since a still screen gets
  nothing unrequested.
- **`SetServerScaling`** (0x08): `08 00 | f64 factor`, with the factor in (0, 1]. The Mac
  renders at that factor before encoding. Vesper asks for `PC pixels per DIP ÷ Mac density`,
  decided once the layout reports the density. Pointer positions stay in **unscaled** pixels,
  so Vesper divides by the factor in force.
- **Encodings**: the Mac encodes with the first of zlib (6), ZRLE (16) and its own codecs in the
  list. Vesper puts zlib first on the local network (Apple's Full quality does the same; it's
  the least work for the Mac) and ZRLE first further away. Thousands of colours (`0x3ea`,
  big-endian RGB 5-5-5 through its own deflate stream) and 4-bit greyscale (`0x3e9`) are
  optional. Apple's DCT codec (`0x3f3`, "Adaptive") isn't implemented: its coefficient coding
  isn't publicly documented.
- **Cursor cache** (`0x450`): header x,y = hotspot, w,h = size, then `u32 id`. Ids up to 999 are
  macOS's built-in cursors and carry nothing else (ard-rs, from Apple's
  `_HandleCursorImageWithAlphaUpdate`; reading 8 bytes for them stalls the stream). Above 999
  comes `u32 len` and its own zlib stream of w·h·4 B,G,R,x bytes followed by a separate w·h
  alpha plane. A length of 0 selects a stored shape.
- **Precise scroll** (0x17, kind 11, 58 bytes): line, 16.16 and point deltas for three axes,
  where the Mac counts up and left as positive, the continuous flag, and the pointer position.
  Sent only to Macs whose ServerInit bitmap lists 0x17. Otherwise Vesper falls back to wheel
  bits, which a Mac only honours alone.
- **Buttons**: on 003.889, 0x02 is right and 0x04 is middle (the reverse of RFB).
- **Modifiers**: the Mac reads Super and Alt keysyms as Command, and Meta as Option. Vesper
  sends Command as Super_L and Option as Meta_L.

## Metadata (one-rectangle updates)

| Encoding | What | Payload |
|---|---|---|
| `0x44c` CursorPos | pointer position in the header | none |
| `0x44d` DisplayInfo | older display list | 10-byte header (count at 8), 0x1c per display |
| `0x451` Display layout | screens, current selection, densities | `u16 len` (counts what follows), 0x14 header, 0x38 per display |
| `0x453` Vendor keysyms | fixed table | `u16 len`, skipped |
| `0x455` Keyboard source | Mac's input source | `u16 len`, skipped |
| `0x456` DeviceInfo | model identifier, e.g. `Mac14,13` | `u16 len`; model at offset 16 of the body |

## Pasteboard

macOS doesn't use RFB cut text. With `AutoPasteboard` started, the Mac sends MiscStatus
(`14 00 00 04 00 01 | u16 cmd`) command 2 when its pasteboard changes. Vesper then fetches
(`0b 00 00 00 | u32 id`) only if clipboard sharing is on. The reply and Vesper's own sends are
`1f 00 | flag | 00 | u32 id | u32 uncompressed | u32 compressed | zlib archive`. An archive is a
run of items, each a `u32` flavour count, then per flavour: counted name, reserved `u32`,
counted key/value tags, counted data. Vesper reads and writes only `public.utf8-plain-text`.
