#!/usr/bin/env bash
# Builds Vesper's Linux AppImage from this checkout: a self-contained Release publish for
# linux-x64 in an AppDir, packed by appimagetool around AppImage's static type-2 runtime. Both
# tools are pinned by SHA-256. Needs the .NET SDK from global.json, curl and squashfs-tools'
# dependencies; no FUSE (appimagetool runs extracted). The AppImage lands in dist/appimage.
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/Directory.Build.props" | head -n1)"
out="$root/dist/appimage"
tools="$out/tools"
appdir="$out/Vesper.AppDir"
rm -rf "$out"
mkdir -p "$tools" "$appdir/usr/lib/vesper"

fetch() { # url sha256 file
    curl -fsSL "$1" -o "$3"
    echo "$2  $3" | sha256sum -c -
}
fetch https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-x86_64.AppImage \
    ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0 "$tools/appimagetool"
fetch https://github.com/AppImage/type2-runtime/releases/download/20251108/runtime-x86_64 \
    2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d "$tools/runtime-x86_64"
chmod +x "$tools/appimagetool"

dotnet publish "$root/src/Vesper.Linux/Vesper.Linux.csproj" -c Release -r linux-x64 --self-contained \
    -o "$appdir/usr/lib/vesper" -nologo
rm -f "$appdir"/usr/lib/vesper/*.pdb
cp "$root/THIRD-PARTY-NOTICES.md" "$root/LICENSE" "$appdir/usr/lib/vesper/"

# The runtime mounts the image and runs AppRun: Vesper itself (it finds its files through its
# real path, so a link is enough).
ln -s usr/lib/vesper/vesper "$appdir/AppRun"
cp "$root/packaging/linux/vesper.desktop" "$appdir/vesper.desktop"
cp "$root/src/Vesper.App/Assets/Vesper-256.png" "$appdir/vesper.png"
ln -s vesper.png "$appdir/.DirIcon"

ARCH=x86_64 "$tools/appimagetool" --appimage-extract-and-run --no-appstream \
    --runtime-file "$tools/runtime-x86_64" "$appdir" "$out/Vesper-$version-x86_64.AppImage"
rm -rf "$appdir" "$tools"
ls -l "$out"/*.AppImage
