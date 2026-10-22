#!/usr/bin/env bash
# Removes what install.sh added. Your settings (projects, tabs, window) in ~/.config/peachapps/RestPeach stay.
set -euo pipefail
data="${XDG_DATA_HOME:-$HOME/.local/share}"
dir="$HOME/.local/opt/restpeach"
config="${XDG_CONFIG_HOME:-$HOME/.config}/peachapps/RestPeach"

rm -f "$data/applications/restpeach.desktop"
rm -f "$data/icons/hicolor/256x256/apps/restpeach.png"
# Only our own link, never a "restpeach" the user put there.
if [[ -L "$HOME/.local/bin/restpeach" && "$(readlink "$HOME/.local/bin/restpeach")" == "$dir/RestPeach" ]]; then
  rm -f "$HOME/.local/bin/restpeach"
fi
rm -f "$dir/RestPeach" "$dir/RestPeach.new" "$dir/RestPeach.bin.new" "$dir/installed.txt" "$dir/uninstall.sh"
rmdir "$dir" 2>/dev/null || true

command -v update-desktop-database >/dev/null && update-desktop-database -q "$data/applications" || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q -t "$data/icons/hicolor" || true

echo "RestPeach desinstalado."
echo "Tu configuración sigue en $config (bórrala a mano si ya no la quieres)."
