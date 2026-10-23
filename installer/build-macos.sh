#!/usr/bin/env bash
# Baut den macOS-Client (Apple Silicon): SyncDrive.app mit Oberfläche, Daemon und syncdrive_cli, verpackt als Disk-Image
# SyncDrive-Client-<Version>-<Build>-arm64.dmg (zum Ziehen nach „Programme“) samt Prüfsumme. Läuft nur unter macOS (iconutil, hdiutil,
# codesign). Bis zum Apple-Developer-Konto ad-hoc signiert (ADR-14): läuft auf dem eigenen Mac, Gatekeeper warnt beim ersten Öffnen.
#
#   installer/build-macos.sh [Build-Nummer] [Ausgabeordner]
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/Directory.Build.props" | head -1)"
build="${1:-$(git -C "$root" rev-list --count HEAD)}"
output="${2:-$root/artifacts/client}"
name="SyncDrive-Client-$version-$build-arm64"
work="$root/artifacts/obj/macos"
app="$work/stage/SyncDrive.app"

rm -rf "$work"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources" "$output"

# 1. Oberfläche (mit Daemon) und CLI eigenständig in ein gemeinsames Verzeichnis, eine .NET-Laufzeit für alle
for project in SyncDrive.Client.Cli SyncDrive.Client.Tray; do
  dotnet publish "$root/src/$project/$project.csproj" -c Release -r osx-arm64 --self-contained \
    -p:PublishDir="$app/Contents/MacOS/" -p:DebugType=none -p:UseAppHost=true --nologo
done

# 2. Programmsymbol aus dem Grundzeichen (dieselbe Zeichnung wie unter Windows)
SYNCDRIVE_WRITE_ICONSET="$work/SyncDrive.iconset" \
  dotnet test "$root/tests/SyncDrive.Client.Tray.Tests" -c Release --filter "FullyQualifiedName~The_mac_icon_set" --nologo
iconutil -c icns "$work/SyncDrive.iconset" -o "$app/Contents/Resources/SyncDrive.icns"

# 3. Info.plist: nur Menüleiste (kein Dock-Symbol), ab macOS 13, Apple Silicon
cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>SyncDrive</string>
  <key>CFBundleDisplayName</key><string>SyncDrive</string>
  <key>CFBundleIdentifier</key><string>org.syncdrive.client</string>
  <key>CFBundleExecutable</key><string>syncdrive</string>
  <key>CFBundleIconFile</key><string>SyncDrive</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$build</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>LSArchitecturePriority</key><array><string>arm64</string></array>
  <key>LSUIElement</key><true/>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSHumanReadableCopyright</key><string>MIT License</string>
</dict>
</plist>
PLIST

# 4. Ad-hoc signieren (Apple Silicon führt nur signierten Code aus); mit Developer-ID folgt hier codesign + notarytool
codesign --force --deep --sign - "$app"
codesign --verify --deep --strict "$app"

# 5. Disk-Image: App und Verweis auf „Programme“ zum Hineinziehen
ln -s /Applications "$work/stage/Applications"
hdiutil create -volname "SyncDrive" -srcfolder "$work/stage" -ov -format UDZO "$output/$name.dmg"
(cd "$output" && shasum -a 256 "$name.dmg" > "$name.sha256")
echo "Fertig: $output/$name.dmg"
