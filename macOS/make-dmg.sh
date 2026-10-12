#!/bin/bash
# Fabrique KSHPLAYER.dmg et KSHPLAYER-PRO.dmg à partir des applis compilées (signature ad hoc).
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p dist
for APP in "KSHPLAYER" "KSHPLAYER PRO"; do
  SRC="build/Build/Products/Release/$APP.app"
  [ -d "$SRC" ] || { echo "Introuvable : $SRC"; exit 1; }
  codesign --force --deep --sign - "$SRC"
  STAGE="$(mktemp -d)"
  cp -R "$SRC" "$STAGE/"
  ln -s /Applications "$STAGE/Applications"
  cp "LISEZ-MOI.txt" "$STAGE/LISEZ-MOI — première ouverture.txt"
  DMG="dist/${APP// /-}.dmg"
  hdiutil create -volname "$APP" -srcfolder "$STAGE" -ov -format UDZO -imagekey zlib-level=9 "$DMG"
  rm -rf "$STAGE"
  echo "Créé : $DMG ($(du -h "$DMG" | cut -f1))"
done
