#!/bin/bash
# Copie les fichiers partagés avec Windows (barre de contrôle, icônes, raccourcis) dans les ressources Mac.
set -euo pipefail
cd "$(dirname "$0")"
rm -rf Resources/mpv
mkdir -p Resources/mpv/scripts Resources/mpv/fonts
cp ../mpv/scripts/kshosc.lua Resources/mpv/scripts/
cp ../mpv/fonts/uosc_icons.otf Resources/mpv/fonts/
cp ../mpv/input.conf Resources/mpv/
echo "Ressources partagées copiées."
