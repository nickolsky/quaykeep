#!/usr/bin/env bash
# @description Отключает выбранную учётную запись SFTP. Файлы сохраняются, конфигурация sshd не меняется.
# @description_en Disables the selected SFTP account. Files are kept and sshd configuration is unchanged.
# @param SFTP_USER text required label="Пользователь SFTP" label_en="SFTP user"
set -euo pipefail
fail(){ printf '%s\n' "$*" >&2; exit 1; }
[[ $EUID == 0 ]] || fail "Run as root"
user="${SFTP_USER:-}"
[[ "$user" =~ ^[a-z_][a-z0-9_-]{0,31}$ && "$user" != root ]] || fail "Enter a valid SFTP username"
state=/var/lib/quaykeep/sftp-disabled
install -d -m 0700 "$state"
exec 8>"$state/$user.lock"
flock -n 8 || fail "An SFTP account operation is already running"
account="$(getent passwd "$user")" || { echo "SFTP account is absent"; exit 0; }
IFS=: read -r _ _ uid _ _ _ shell <<< "$account"
[[ "$uid" != 0 && "$shell" == */nologin ]] &&
  id -nG "$user" | tr ' ' '\n' | grep -qxF sftponly &&
  grep -qxF "# BEGIN sshm-sftp $user" /etc/ssh/sshd_config &&
  grep -qxF "# END sshm-sftp $user" /etc/ssh/sshd_config ||
  fail "Cannot verify this is a Quaykeep SFTP-only account"
expiry="$(getent shadow "$user" | cut -d: -f8)"
if [[ "$expiry" == 0 ]]; then echo "SFTP account is already disabled"; exit 0; fi
[[ "$expiry" =~ ^[0-9]*$ ]] || fail "Cannot read the current account expiration"
(umask 077; printf '%s:%s\n' "$uid" "$expiry" > "$state/$user")
if ! chage -E 0 -- "$user"; then rm -- "$state/$user"; exit 1; fi
echo "SFTP account disabled. Files are kept."
