#!/usr/bin/env bash
# Builds Vesper's Arch Linux package from this checkout: a self-contained Release publish for
# linux-x64, packed and handed to makepkg. Run as an ordinary user (makepkg refuses root), with
# the .NET SDK from global.json and base-devel installed. The package lands in dist/arch.
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/Directory.Build.props" | head -n1)"
out="$root/dist/arch"
rm -rf "$out"
mkdir -p "$out/vesper"

dotnet publish "$root/src/Vesper.Linux/Vesper.Linux.csproj" -c Release -r linux-x64 --self-contained -o "$out/vesper" -nologo
rm -f "$out"/vesper/*.pdb
cp "$root/src/Vesper.App/Assets/Vesper-256.png" "$out/vesper/vesper.png"
cp "$root/THIRD-PARTY-NOTICES.md" "$out/vesper/"
tar -C "$out" --owner=0 --group=0 -czf "$out/vesper-$version-linux-x64.tar.gz" vesper
rm -rf "$out/vesper"

cp "$root/packaging/arch/PKGBUILD" "$root/packaging/linux/vesper.desktop" "$out/"
sed -i "s/^pkgver=.*/pkgver=$version/" "$out/PKGBUILD"
cd "$out"
updpkgsums
makepkg --force --cleanbuild
ls -l "$out"/*.pkg.tar.zst
