#!/usr/bin/env bash
# Installs RestPeach for the current user, without root: the binary in ~/.local/opt/restpeach (so the app can update
# itself there), a "restpeach" command in ~/.local/bin and a menu entry, so the desktop's app search finds it.
# Run it from the extracted release:  ./install.sh      Undo with:  ~/.local/opt/restpeach/uninstall.sh
set -euo pipefail
src="$(cd "$(dirname "$0")" && pwd)"
data="${XDG_DATA_HOME:-$HOME/.local/share}"
dir="$HOME/.local/opt/restpeach"
bin="$HOME/.local/bin"

if [[ ! -f "$src/RestPeach" ]]; then
  echo "No se encontró el binario RestPeach junto a install.sh." >&2
  exit 1
fi

mkdir -p "$dir" "$bin" "$data/applications" "$data/icons/hicolor/256x256/apps"
# Through a temporary name: replacing a running binary in place would break it, a rename does not.
install -m 755 "$src/RestPeach" "$dir/RestPeach.tmp"
mv -f "$dir/RestPeach.tmp" "$dir/RestPeach"
install -m 755 "$src/uninstall.sh" "$dir/uninstall.sh"
install -m 644 "$src/installed.txt" "$dir/installed.txt"
install -m 644 "$src/restpeach.png" "$data/icons/hicolor/256x256/apps/restpeach.png"
ln -sf "$dir/RestPeach" "$bin/restpeach"

cat > "$data/applications/restpeach.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=RestPeach
GenericName=API client
GenericName[es]=Cliente de APIs
Comment=Local-first API client: REST, GraphQL and environments in plain files
Comment[es]=Cliente de APIs local: REST, GraphQL y entornos en archivos de texto
Exec="$dir/RestPeach" %F
Icon=restpeach
Terminal=false
Categories=Development;WebDevelopment;
Keywords=api;rest;http;graphql;request;
EOF

command -v update-desktop-database >/dev/null && update-desktop-database -q "$data/applications" || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q -t "$data/icons/hicolor" || true

echo "RestPeach instalado en $dir"
echo "Búscalo en el menú de aplicaciones o ejecuta: restpeach"
case ":$PATH:" in *":$bin:"*) ;; *) echo "(Agrega $bin a tu PATH para usar el comando restpeach.)" ;; esac
echo "Para desinstalar: $dir/uninstall.sh"
