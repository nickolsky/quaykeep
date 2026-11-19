#!/usr/bin/env bash
# @description Отзывает доступ указанного пользователя к vsftpd. Файлы и учётная запись сохраняются.
# @description_en Revokes the selected user's access to vsftpd. Files and the account are kept.
# @param FTP_USER text required label="Пользователь FTP" label_en="FTP user"
set -euo pipefail
fail(){ printf '%s\n' "$*" >&2; exit 1; }
[[ $EUID == 0 ]] || fail "Run as root"
user="${FTP_USER:-}"
[[ "$user" =~ ^[a-z_][a-z0-9_-]{0,31}$ ]] || fail "Enter a valid FTP username"
list=/etc/vsftpd.sshm-users
[[ -e "$list" || -L "$list" ]] || { echo "Quaykeep FTP allowlist is absent"; exit 0; }
[[ -f "$list" && ! -L "$list" && "$(stat -c %u "$list")" == 0 ]] || fail "Cannot verify the FTP allowlist"
grep -qxF -- "$user" "$list" || { echo "FTP access is already absent"; exit 0; }
managed=0
for config in /etc/vsftpd.conf /etc/vsftpd/vsftpd.conf; do
  if [[ -f "$config" ]] && grep -qF '# written by Quaykeep (FTP server);' "$config" &&
     grep -qxF "userlist_file=$list" "$config" && grep -qxF 'userlist_enable=YES' "$config" &&
     grep -qxF 'userlist_deny=NO' "$config"; then managed=1; fi
done
(( managed )) || fail "Cannot verify that vsftpd uses the Quaykeep allowlist"
temp="$(mktemp /etc/.qk-ftp-users.XXXXXX)"
trap 'rm -f -- "$temp"' EXIT
cp --preserve=all -- "$list" "$temp"
awk -v user="$user" '$0 != user' "$list" > "$temp"
mv -f -- "$temp" "$list"
echo "FTP access revoked. Files and the account are kept."
