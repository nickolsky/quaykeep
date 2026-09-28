#!/usr/bin/env bash
# @name SFTP — пользователь только для файлов (OpenSSH, chroot) — Ubuntu / Debian / CentOS
# @name_en SFTP — files-only user (OpenSSH, chroot) — Ubuntu / Debian / CentOS
# @group Files
# @os ubuntu,debian,centos,rhel
# @description Создаёт пользователя, который может только передавать файлы по SFTP: без shell, без пробросов, запертого (chroot)
# @description в своей папке. Работает через уже установленный sshd — новых служб и портов нет. Файлы — в папке files внутри его корня.
# @description Настройки sshd проверяются (sshd -t) до перезагрузки sshd; при ошибке всё возвращается. Повторный запуск добавляет
# @description ещё одного пользователя или меняет пароль / ключ существующего. Вход по паролю можно оставить только этому
# @description пользователю, даже если на сервере он отключён.
# @description_en Creates a user who can only transfer files over SFTP: no shell, no forwarding, locked (chroot) into their own
# @description_en folder. Uses the sshd that is already there, so no new service or port. Files go to the "files" folder in their root.
# @description_en The sshd settings are checked (sshd -t) before sshd is reloaded, and put back on an error. Running it again adds
# @description_en another user or changes an existing one's password or key. Password login can be allowed for this user only,
# @description_en even when it is off on the server.
#
# @param SFTP_USER text required default=sftpuser label="Пользователь" label_en="User" hint="Латиница в нижнем регистре, цифры, _ и -" hint_en="Lowercase letters, digits, _ and -"
# @param SFTP_PASSWORD secret label="Пароль" label_en="Password" hint="Не короче 12 символов. Пусто — сгенерировать для нового пользователя; для существующего — оставить прежний" hint_en="At least 12 characters. Empty = generate for a new user; keep the old one for an existing user"
# @param SFTP_ALLOW_PASSWORD bool default=1 label="Разрешить вход по паролю" label_en="Allow password login" hint="Выключите, чтобы входить только по ключу" hint_en="Turn off to log in with a key only"
# @param SFTP_PUBKEY text label="Публичный ключ (необязательно)" label_en="Public key (optional)" hint="Строка вида ssh-ed25519 AAAA… comment — добавляется к ключам пользователя" hint_en="A line like ssh-ed25519 AAAA… comment; added to the user's keys"
# @param SFTP_ROOT text required default=/srv/sftp label="Папка пользователей" label_en="Users folder" hint="У каждого пользователя своя подпапка; все папки на пути должны принадлежать root" hint_en="Each user gets a subfolder; every folder on the path must be owned by root"
#
# @result SFTP_URL label="Адрес SFTP" label_en="SFTP address"
# @result SFTP_USER label="Пользователь SFTP" label_en="SFTP user"
# @result SFTP_PASSWORD label="Пароль SFTP" label_en="SFTP password"
# @result SFTP_FOLDER label="Папка для файлов (SFTP)" label_en="Files folder (SFTP)"
#
# Runs standalone too: sudo SFTP_USER=bob bash sftp-user.sh
set -Eeuo pipefail
trap 'echo -e "\nERROR line $LINENO: $BASH_COMMAND\n" >&2' ERR

GROUP=sftponly
CFG=/etc/ssh/sshd_config

# ---- shared helpers: base (identical in every built-in script that has them; edit all copies) ----
# bash 5.2 reads "&" in a ${var//a/b} replacement as the matched text; the helpers here mean it literally
shopt -u patsub_replacement 2>/dev/null || true

log(){ echo -e "\n== $* =="; }
warn(){ echo "WARNING: $*" >&2; }
die(){ echo "ERROR: $*" >&2; exit 1; }
cmd(){ command -v "$1" >/dev/null 2>&1; }
# SSH Manager: report a value back (saved to the server's attributes); no-op when run by hand
sshm_result(){ [[ -n "${SSHM_RESULT:-}" ]] && printf '%s=%s\n' "$1" "$2" >> "$SSHM_RESULT"; return 0; }
trim(){ local v="$1"; v="${v#"${v%%[![:space:]]*}"}"; printf '%s' "${v%"${v##*[![:space:]]}"}"; }

url_encode(){
  local value="$1" out="" i ch
  for (( i=0; i<${#value}; i++ )); do
    ch="${value:i:1}"
    case "$ch" in
      [a-zA-Z0-9.~_-]) out+="$ch" ;;
      *) printf -v out '%s%%%02X' "$out" "'$ch" ;;
    esac
  done
  printf '%s' "$out"
}

require_root(){ [[ "${EUID}" -eq 0 ]] || die "Run as root: sudo bash $0"; }

# Ubuntu, Debian, CentOS Stream, Rocky, AlmaLinux, RHEL (8 or newer) or a derivative. Sets OS_FAMILY (debian | rhel),
# OS_MAJOR and DOCKER_REPO / DOCKER_CODENAME for download.docker.com
require_os(){
  [[ -r /etc/os-release ]] || die "/etc/os-release not found"
  . /etc/os-release
  OS_MAJOR="${VERSION_ID:-0}"; OS_MAJOR="${OS_MAJOR%%.*}"
  case " ${ID:-} ${ID_LIKE:-} " in
    *" ubuntu "*) OS_FAMILY=debian; DOCKER_REPO=ubuntu; DOCKER_CODENAME="${UBUNTU_CODENAME:-${VERSION_CODENAME:-}}" ;;
    *" debian "*) OS_FAMILY=debian; DOCKER_REPO=debian; DOCKER_CODENAME="${VERSION_CODENAME:-}" ;;
    *" rhel "* | *" centos "*)
      OS_FAMILY=rhel; DOCKER_CODENAME=""
      if [[ "${ID:-}" == rhel ]]; then DOCKER_REPO=rhel; else DOCKER_REPO=centos; fi
      [[ "$OS_MAJOR" =~ ^[0-9]+$ ]] && (( OS_MAJOR >= 8 )) || die "CentOS / RHEL 8 or newer is required (found ${PRETTY_NAME:-unknown})" ;;
    *) die "Ubuntu, Debian or CentOS / Rocky / AlmaLinux / RHEL is required (found ${PRETTY_NAME:-unknown})" ;;
  esac
  if [[ "$OS_FAMILY" == debian ]]; then cmd apt-get || die "apt-get not found"; else cmd dnf || die "dnf not found"; fi
  echo "OS: ${PRETTY_NAME:-$ID}"
}

# EPEL (Extra Packages for Enterprise Linux) with CodeReady Builder, which some of its packages need
enable_epel(){
  rpm -q epel-release >/dev/null 2>&1 && return 0
  log "Enabling EPEL"
  dnf -y -q install epel-release >/dev/null 2>&1 ||
    dnf -y -q install "https://dl.fedoraproject.org/pub/epel/epel-release-latest-${OS_MAJOR}.noarch.rpm" || die "Could not enable EPEL"
  if cmd crb; then crb enable >/dev/null 2>&1 || true
  else dnf config-manager --set-enabled crb >/dev/null 2>&1 || dnf config-manager --set-enabled powertools >/dev/null 2>&1 || true
  fi
}

APT_UPDATED=0
# Installs the packages that are missing. Names are Debian's; on CentOS / RHEL they are mapped (iproute2 = iproute)
# and a package missing from the base repositories is taken from EPEL.
pkg_install(){
  local missing=() p
  if [[ "$OS_FAMILY" == rhel ]]; then
    for p in "$@"; do
      [[ "$p" == iproute2 ]] && p=iproute
      rpm -q --whatprovides "$p" >/dev/null 2>&1 || missing+=("$p") # curl-minimal provides curl
    done
    (( ${#missing[@]} )) || return 0
    echo "dnf: installing ${missing[*]}"
    dnf -y -q install "${missing[@]}" 2>/dev/null && return 0
    enable_epel
    dnf -y -q install "${missing[@]}"
    return
  fi
  for p in "$@"; do
    dpkg-query -W -f='${Status}' "$p" 2>/dev/null | grep -q "ok installed" || missing+=("$p")
  done
  (( ${#missing[@]} )) || return 0
  if (( ! APT_UPDATED )); then apt-get -o DPkg::Lock::Timeout=300 -o Acquire::Retries=3 update -qq; APT_UPDATED=1; fi
  echo "apt: installing ${missing[*]}"
  apt-get -o DPkg::Lock::Timeout=300 -o Acquire::Retries=3 install -y -qq --no-install-recommends "${missing[@]}"
}

pkg_installed(){ if [[ "$OS_FAMILY" == rhel ]]; then rpm -q "$1" >/dev/null 2>&1; else dpkg-query -W -f='${Status}' "$1" 2>/dev/null | grep -q "ok installed"; fi; }

# docker pull with retries: registries time out now and then
pull(){
  local img i
  for img in "$@"; do
    for i in 1 2 3 4 5; do
      docker pull -q "$img" >/dev/null && continue 2
      echo "docker pull $img failed (attempt $i of 5), retrying" >&2
      sleep $(( i * 5 ))
    done
    die "Could not pull $img: no access to the registry?"
  done
}

compose_pull(){ # project folder
  local i
  for i in 1 2 3 4 5; do
    (cd "$1" && docker compose pull -q) && return 0
    echo "docker compose pull failed (attempt $i of 5), retrying" >&2
    sleep $(( i * 5 ))
  done
  die "Could not pull the images: no access to the registry?"
}

ensure_docker(){
  if cmd docker && docker compose version >/dev/null 2>&1; then
    systemctl enable --now docker >/dev/null 2>&1 || true
    return 0
  fi
  if [[ "$OS_FAMILY" == rhel ]]; then
    # also when "docker" is podman-docker: Docker CE replaces it and podman's runc/buildah (--allowerasing)
    log "Installing Docker Engine + compose plugin (download.docker.com)"
    [[ -f /etc/yum.repos.d/docker-ce.repo ]] ||
      curl -fsSL "https://download.docker.com/linux/${DOCKER_REPO}/docker-ce.repo" -o /etc/yum.repos.d/docker-ce.repo
    dnf -y -q install --allowerasing docker-ce docker-ce-cli containerd.io docker-compose-plugin
  elif cmd docker; then
    log "Docker is installed without the compose plugin: adding it"
    pkg_install docker-compose-plugin 2>/dev/null || pkg_install docker-compose-v2 || die "Install the docker compose plugin and run again"
  else
    log "Installing Docker Engine + compose plugin (download.docker.com)"
    [[ -n "${DOCKER_CODENAME}" ]] || die "Unknown distribution codename"
    install -m 0755 -d /etc/apt/keyrings
    curl -fsSL "https://download.docker.com/linux/${DOCKER_REPO}/gpg" -o /etc/apt/keyrings/docker.asc
    chmod a+r /etc/apt/keyrings/docker.asc
    echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/${DOCKER_REPO} ${DOCKER_CODENAME} stable" \
      > /etc/apt/sources.list.d/docker.list
    APT_UPDATED=0
    pkg_install docker-ce docker-ce-cli containerd.io docker-compose-plugin
  fi
  systemctl enable --now docker >/dev/null 2>&1 || service docker start >/dev/null 2>&1 || true
  docker compose version >/dev/null 2>&1 || die "docker compose is not available"
}

# Opens a port only when a firewall is actually filtering (never enables one)
open_port(){
  local port="$1" proto="${2:-tcp}"
  if cmd ufw && ufw status 2>/dev/null | grep -qi '^Status: active'; then
    ufw allow "${port}/${proto}" >/dev/null && echo "Firewall (ufw): opened ${port}/${proto}"
  elif cmd firewall-cmd && systemctl is-active --quiet firewalld 2>/dev/null; then
    firewall-cmd -q --add-port="${port}/${proto}" && firewall-cmd -q --permanent --add-port="${port}/${proto}" &&
      echo "Firewall (firewalld): opened ${port}/${proto}"
  elif cmd iptables && iptables -S INPUT 2>/dev/null | grep -q '^-P INPUT DROP'; then
    iptables -C INPUT -p "$proto" --dport "$port" -j ACCEPT 2>/dev/null || iptables -I INPUT -p "$proto" --dport "$port" -j ACCEPT
    echo "Firewall (iptables): opened ${port}/${proto} (not persistent)"
  fi
  return 0
}

# The process listening on a port, empty when it is free
port_owner(){
  local flag=-lntp; [[ "${2:-tcp}" == udp ]] && flag=-lnup
  ss "$flag" 2>/dev/null | awk -v p=":$1" 'NR > 1 && substr($4, length($4) - length(p) + 1) == p { print $4, $NF; exit }'
}
require_free_port(){
  local owner; owner="$(port_owner "$1" "${2:-tcp}")"
  [[ -z "$owner" ]] || die "Port $1/${2:-tcp} is already in use: ${owner}. Choose another port or stop that service."
}

public_ip(){
  curl -fsS4 --max-time 10 https://api.ipify.org 2>/dev/null || curl -fsS4 --max-time 10 https://ifconfig.me 2>/dev/null ||
    hostname -I 2>/dev/null | awk '{print $1}'
}
# ---- end of shared helpers: base ----

# ---- shared helpers: services (identical in every built-in script that has them; edit all copies) ----
rand_pass(){ local s; s="$(openssl rand -base64 64 | tr -dc 'A-Za-z0-9')"; printf '%s' "${s:0:${1:-24}}"; }

# NAME=value line for a compose .env file: single quotes are literal; double quotes when the value has one
dotenv(){
  local v="$2"
  if [[ "$v" == *"'"* ]]; then
    v="${v//\\/\\\\}"; v="${v//\"/\\\"}"; v="${v//\$/\$\$}"
    printf '%s="%s"\n' "$1" "$v"
  else
    printf "%s='%s'\n" "$1" "$v"
  fi
}

valid_domain(){ [[ "$1" =~ ^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$ ]]; }

# Stops when the domain does not resolve, warns when it points somewhere else
check_dns(){
  local resolved ip
  resolved="$(getent ahostsv4 "$1" 2>/dev/null | awk '{print $1; exit}' || true)"
  ip="$(public_ip)"
  [[ -n "$resolved" ]] || die "$1 does not resolve. Create an A record pointing to ${ip:-this server} first."
  [[ "$resolved" == "$ip" ]] || warn "$1 resolves to ${resolved}, this server looks like ${ip}; getting a certificate may fail"
  return 0
}

# RAM + swap below $1 MiB: a warning; free space on /var below $2 MiB: an error
check_resources(){
  local ram swap disk
  ram="$(awk '/^MemTotal/ {print int($2/1024)}' /proc/meminfo)"
  swap="$(awk '/^SwapTotal/ {print int($2/1024)}' /proc/meminfo)"
  disk="$(df -Pm /var | awk 'END {print $4}')"
  echo "RAM ${ram} MiB, swap ${swap} MiB, free on /var ${disk} MiB"
  (( ram + swap >= $1 )) || warn "Only $((ram + swap)) MiB of RAM + swap; $1 MiB or more is recommended"
  (( disk >= $2 )) || die "Need at least $2 MiB free on /var, found ${disk} MiB"
  return 0
}

# A swap file for servers with less than 2 GiB of RAM and no swap
ensure_swap(){ # size in MiB
  local ram; ram="$(awk '/^MemTotal/ {print int($2/1024)}' /proc/meminfo)"
  (( ram < 2048 )) || return 0
  [[ -z "$(swapon --noheadings 2>/dev/null)" ]] || return 0
  [[ ! -e /swapfile ]] || { warn "/swapfile exists but is not in use; leaving it alone"; return 0; }
  log "Adding a $1 MiB swap file (/swapfile): the server has ${ram} MiB of RAM"
  fallocate -l "${1}M" /swapfile 2>/dev/null || dd if=/dev/zero of=/swapfile bs=1M count="$1" status=none
  chmod 600 /swapfile
  mkswap /swapfile >/dev/null
  if swapon /swapfile 2>/dev/null; then
    grep -q '^/swapfile ' /etc/fstab || echo '/swapfile none swap sw 0 0' >> /etc/fstab
  else
    warn "Could not turn on swap (a container-based VPS?); continuing without it"
    rm -f /swapfile
  fi
}

# Waits until the URL answers with any HTTP status
wait_http(){ # url seconds
  local i
  for (( i = 0; i < $2; i += 5 )); do
    curl -s -o /dev/null --max-time 5 "$1" && return 0
    sleep 5
  done
  return 1
}

compose_down(){
  [[ -f "$1/docker-compose.yml" ]] || return 0
  (cd "$1" && docker compose down --remove-orphans >/dev/null 2>&1) || true
}

# Caddyfile for automatic HTTPS: domain, upstream (host:port), extra directives
write_caddyfile(){
  {
    if [[ -n "${ACME_EMAIL:-}" ]]; then printf '{\n\temail %s\n}\n\n' "$ACME_EMAIL"; fi
    printf '%s {\n\tencode zstd gzip\n%s\treverse_proxy %s\n}\n' "$1" "${3:-}" "$2"
  } > "$DIR/Caddyfile"
}

# The caddy service for docker-compose.yml (ports 80/443, certificates kept in ./caddy)
caddy_service(){
  cat <<'YAML'
  caddy:
    image: caddy:2-alpine
    restart: unless-stopped
    ports:
      - "80:80"
      - "443:443"
      - "443:443/udp"
    volumes:
      - ./Caddyfile:/etc/caddy/Caddyfile:ro
      - ./caddy/data:/data
      - ./caddy/config:/config
    logging:
      driver: local
      options:
        max-size: "2m"
        max-file: "3"
YAML
}
# ---- end of shared helpers: services ----

SSHD=/usr/sbin/sshd

reload_sshd(){
  if cmd systemctl && [[ -d /run/systemd/system ]]; then
    local u
    for u in ssh sshd; do
      if systemctl is-active --quiet "$u.service" 2>/dev/null; then systemctl reload "$u.service"; return; fi
    done
    # socket activation with no daemon running: every new sshd reads the file anyway
    for u in ssh sshd; do systemctl is-active --quiet "$u.socket" 2>/dev/null && return 0; done
  fi
  service ssh reload 2>/dev/null || service sshd reload 2>/dev/null || die "Could not reload sshd"
}

# ChrootDirectory needs every folder on the path owned by root and writable by nobody else
check_chroot_path(){
  local p="$1" owner mode
  while [[ -n "$p" ]]; do
    if [[ -e "$p" ]]; then
      owner="$(stat -c %u "$p")"; mode="$(stat -c %a "$p")"
      [[ "$owner" == 0 ]] || die "$p must be owned by root for the SFTP chroot (owner uid $owner)"
      (( (8#$mode & 8#022) == 0 )) || die "$p must not be writable by group or others for the SFTP chroot (mode $mode)"
    fi
    [[ "$p" == / ]] && break
    p="$(dirname "$p")"
  done
}

# sshd_config block for one user, between markers so a rerun replaces it; at the end of the file,
# where a Match block cannot swallow settings that follow it
write_sshd_block(){ # user allow-password(yes|no)
  local begin="# BEGIN sshm-sftp $1" end="# END sshm-sftp $1" tmp
  tmp="$(mktemp)"
  awk -v b="$begin" -v e="$end" '$0 == b { skip = 1; next } $0 == e { skip = 0; next } !skip' "$CFG" > "$tmp"
  cat >> "$tmp" <<EOF
$begin
Match User $1
    ChrootDirectory %h
    ForceCommand internal-sftp -u 0022
    PasswordAuthentication $2
    AllowTcpForwarding no
    AllowAgentForwarding no
    X11Forwarding no
    PermitTunnel no
$end
EOF
  cat "$tmp" > "$CFG" # keeps the owner, mode and SELinux label of sshd_config
  rm -f "$tmp"
}

# SELinux (CentOS / RHEL): sshd reads authorized_keys only with the ssh_home_t label
selinux_for_sftp(){
  cmd getenforce && [[ "$(getenforce 2>/dev/null)" != Disabled ]] || return 0
  cmd semanage || pkg_install policycoreutils-python-utils || { warn "SELinux: semanage is missing; key login may be refused"; return 0; }
  semanage fcontext -a -t ssh_home_t "${SFTP_ROOT}/[^/]+/\.ssh(/.*)?" 2>/dev/null ||
    semanage fcontext -m -t ssh_home_t "${SFTP_ROOT}/[^/]+/\.ssh(/.*)?" 2>/dev/null || true
  restorecon -R "$SFTP_ROOT" >/dev/null 2>&1 || true
  echo "SELinux: labels set for ${SFTP_ROOT}"
}

main(){
  require_root
  require_os

  SFTP_USER="$(trim "${SFTP_USER:-sftpuser}")"
  SFTP_ROOT="$(trim "${SFTP_ROOT:-/srv/sftp}")"; SFTP_ROOT="${SFTP_ROOT%/}"
  SFTP_PUBKEY="$(trim "${SFTP_PUBKEY:-}")"
  local allow="${SFTP_ALLOW_PASSWORD:-1}" new_password="${SFTP_PASSWORD:-}"

  [[ "$SFTP_USER" =~ ^[a-z_][a-z0-9_-]{0,31}$ ]] || die "Invalid user name: $SFTP_USER"
  [[ "$SFTP_ROOT" == /* && "$SFTP_ROOT" =~ ^[A-Za-z0-9._/-]+$ && "$SFTP_ROOT" != *..* ]] ||
    die "The users folder must be an absolute path of letters, digits and . _ - /"
  case "$SFTP_ROOT" in
    /|/etc|/etc/*|/proc*|/sys*|/dev*|/run*|/boot*|/usr|/usr/*|/bin*|/sbin*|/lib*|/root|/root/*|/home|/tmp|/var|/var/tmp)
      die "Do not use ${SFTP_ROOT} as the SFTP users folder" ;;
  esac
  [[ -z "$new_password" || ${#new_password} -ge 12 ]] || die "The password must be at least 12 characters"
  [[ "$new_password" != *[$'\n\r']* ]] || die "Invalid password"
  [[ -z "$SFTP_PUBKEY" || "$SFTP_PUBKEY" =~ ^(ssh-|ecdsa-|sk-)[A-Za-z0-9@.-]+\ [A-Za-z0-9+/=]+(\ [^[:cntrl:]]*)?$ ]] ||
    die "The public key must be one line: type, key and an optional comment (ssh-ed25519 AAAA… name)"
  local pw_auth=no; [[ "$allow" == 1 ]] && pw_auth=yes

  pkg_install openssh-server openssl
  SSHD="$(command -v sshd 2>/dev/null || echo /usr/sbin/sshd)"
  [[ -x "$SSHD" ]] || die "sshd not found"
  [[ -f "$CFG" ]] || die "$CFG not found"
  [[ -d /run/sshd ]] || mkdir -p -m 0755 /run/sshd
  "$SSHD" -t || die "sshd -t fails before any change; fix the sshd configuration first"

  local home="$SFTP_ROOT/$SFTP_USER" nologin existing=0
  nologin="$(command -v nologin 2>/dev/null || echo /usr/sbin/nologin)"
  getent group "$GROUP" >/dev/null || groupadd --system "$GROUP"
  if id "$SFTP_USER" >/dev/null 2>&1; then
    # never lock an ordinary account into a chroot: only users this script made
    id -nG "$SFTP_USER" | tr ' ' '\n' | grep -qx "$GROUP" ||
      die "User ${SFTP_USER} already exists and is not an SFTP user made by this script; choose another name"
    existing=1
    echo "User ${SFTP_USER} exists: updating it"
  fi

  install -d -m 0755 -o root -g root "$SFTP_ROOT"
  check_chroot_path "$SFTP_ROOT"
  if (( ! existing )); then
    log "Creating the user ${SFTP_USER}"
    useradd -M -d "$home" -s "$nologin" -U -G "$GROUP" "$SFTP_USER"
  fi
  install -d -m 0755 -o root -g root "$home"
  [[ -d "$home/files" ]] || install -d -m 0750 -o "$SFTP_USER" -g "$SFTP_USER" "$home/files"

  local generated=""
  if [[ -n "$new_password" ]]; then
    printf '%s:%s\n' "$SFTP_USER" "$new_password" | chpasswd
    if (( existing )); then echo "New password set for ${SFTP_USER}"; fi
  elif (( ! existing )); then
    # a new account always gets a password (an account without one may be refused even for key login)
    generated="$(rand_pass 20)"
    printf '%s:%s\n' "$SFTP_USER" "$generated" | chpasswd
  fi

  if [[ -n "$SFTP_PUBKEY" ]]; then
    install -d -m 0700 -o "$SFTP_USER" -g "$SFTP_USER" "$home/.ssh"
    touch "$home/.ssh/authorized_keys"
    grep -qxF "$SFTP_PUBKEY" "$home/.ssh/authorized_keys" || printf '%s\n' "$SFTP_PUBKEY" >> "$home/.ssh/authorized_keys"
    chown "$SFTP_USER:$SFTP_USER" "$home/.ssh/authorized_keys"
    chmod 600 "$home/.ssh/authorized_keys"
    echo "Public key added"
  fi
  [[ "$pw_auth" == yes || -s "$home/.ssh/authorized_keys" ]] ||
    warn "Password login is off and ${SFTP_USER} has no public key: nobody can log in as this user yet"
  if [[ "$OS_FAMILY" == rhel ]]; then selinux_for_sftp; fi

  log "Configuring sshd"
  local backup; backup="$(mktemp)"
  cp -p "$CFG" "$backup"
  write_sshd_block "$SFTP_USER" "$pw_auth"
  if ! "$SSHD" -t; then
    cat "$backup" > "$CFG"; rm -f "$backup"
    die "sshd rejected the new settings; sshd_config is back as it was"
  fi
  local effective
  effective="$("$SSHD" -T -C "user=${SFTP_USER},host=localhost,addr=127.0.0.1" 2>/dev/null || true)"
  if ! grep -qi '^forcecommand internal-sftp' <<<"$effective" || ! grep -qi '^chrootdirectory ' <<<"$effective"; then
    cat "$backup" > "$CFG"; rm -f "$backup"
    die "sshd does not apply the SFTP settings to ${SFTP_USER} (an earlier Match block?); sshd_config is back as it was"
  fi
  [[ -f "$CFG.sshm-sftp-bak" ]] || cp -p "$backup" "$CFG.sshm-sftp-bak"
  rm -f "$backup"
  grep -qi "^passwordauthentication ${pw_auth}" <<<"$effective" ||
    warn "sshd reports password login $(awk 'tolower($1) == "passwordauthentication" { print $2 }' <<<"$effective") for ${SFTP_USER}"
  if grep -Eqi '^(allowusers|allowgroups) ' <<<"$("$SSHD" -T 2>/dev/null)"; then
    warn "sshd has AllowUsers / AllowGroups: add ${SFTP_USER} (or the group ${GROUP}) there, otherwise sshd refuses the login"
  fi
  reload_sshd

  local port host url i
  port="$("$SSHD" -T 2>/dev/null | awk 'tolower($1) == "port" { print $2; exit }' || true)"
  # the reload makes sshd re-exec itself: wait until it listens again, so a login right after this works
  sleep 1
  for (( i = 0; i < 20; i++ )); do
    [[ -z "$port" || -n "$(port_owner "$port" tcp)" ]] && break
    sleep 0.5
  done
  host="${SSHM_HOST:-$(public_ip)}"
  if [[ "$host" == *:* && "$host" != \[* ]]; then host="[$host]"; fi
  url="sftp://${SFTP_USER}@${host}"
  [[ -z "$port" || "$port" == 22 ]] || url+=":${port}"

  echo ""
  echo "SFTP: ${url}"
  echo "User: ${SFTP_USER}${generated:+ / password ${generated}}; password login: ${pw_auth}"
  echo "Files: /files in the client = ${home}/files on the server"

  sshm_result SFTP_URL "$url"
  sshm_result SFTP_USER "$SFTP_USER"
  [[ -z "$generated" || "$pw_auth" != yes ]] || sshm_result SFTP_PASSWORD "$generated"
  sshm_result SFTP_FOLDER "/files"
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then main "$@"; fi
