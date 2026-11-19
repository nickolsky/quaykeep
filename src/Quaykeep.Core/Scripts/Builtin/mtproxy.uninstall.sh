#!/usr/bin/env bash
# @description Останавливает MTProxy и удаляет службу, конфигурацию и секрет клиента. Правила файрвола сохраняются.
# @description_en Stops MTProxy and deletes the service, configuration and client secret. Firewall rules are kept.
set -euo pipefail
fail(){ printf '%s\n' "$*" >&2; exit 1; }
[[ $EUID == 0 ]] || fail "Run as root"
dir=/opt/quaykeep-mtproxy
unit=quaykeep-mtproxy.service
file="/etc/systemd/system/$unit"
if [[ ! -e "$file" && ! -L "$file" && ! -e "$dir" && ! -L "$dir" ]]; then
  echo "Quaykeep MTProxy is absent"
  exit 0
fi
[[ -f "$file" && ! -L "$file" && "$(stat -c %u "$file")" == 0 ]] &&
  grep -qxF '# Installed by Quaykeep MTProxy' "$file" || fail "Cannot verify that Quaykeep owns this MTProxy service"
if [[ -e "$dir" || -L "$dir" ]]; then
  [[ -d "$dir" && "$(realpath -e -- "$dir")" == "$dir" && "$(stat -c %u "$dir")" == 0 ]] ||
    fail "Cannot verify the MTProxy installation directory"
  [[ ! -L "$dir/install.lock" ]] || fail "The installer lock is a symbolic link"
  exec 9>>"$dir/install.lock"
  flock -n 9 || fail "An MTProxy installation or uninstall is already running"
fi
systemctl daemon-reload
[[ "$(systemctl show -p FragmentPath --value "$unit")" == "$file" ]] || fail "This unit is loaded from a different path"
systemctl disable --now "$unit"
if [[ -d "$dir" ]]; then
  find "$dir" -xdev -depth -mindepth 1 ! -path "$dir/install.lock" -delete
fi
rm -- "$file"
systemctl daemon-reload
if [[ -d "$dir" ]]; then
  rm -- "$dir/install.lock"
  rmdir -- "$dir"
fi
echo "Quaykeep MTProxy was uninstalled."
