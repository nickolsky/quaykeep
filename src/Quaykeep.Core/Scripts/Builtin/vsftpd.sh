#!/usr/bin/env bash
# @name FTP / FTPS-сервер (vsftpd) — Ubuntu / Debian / CentOS
# @name_en FTP / FTPS server (vsftpd) — Ubuntu / Debian / CentOS
# @group Files
# @os ubuntu,debian,centos,rhel
# @description vsftpd для программ, которые не умеют SFTP (камеры, сканеры, старые клиенты). По умолчанию только FTPS — FTP
# @description с шифрованием TLS (explicit, «FTP через TLS» в FileZilla); сертификат Let's Encrypt, если указан домен, иначе
# @description самоподписанный. Пользователь заперт (chroot) в своей папке, писать можно в папку files; войти могут только
# @description пользователи из этого скрипта. Открываются порт FTP и диапазон пассивных портов. Повторный запуск добавляет
# @description ещё одного пользователя или меняет пароль существующего.
# @description_en vsftpd for software that cannot do SFTP (cameras, scanners, old clients). By default FTPS only: FTP encrypted with
# @description_en TLS (explicit, "FTP over TLS" in FileZilla); a Let's Encrypt certificate when a domain is given, otherwise a
# @description_en self-signed one. The user is locked (chroot) into their folder and can write to its "files" folder; only users made
# @description_en by this script can log in. Opens the FTP port and the passive port range. Running it again adds another user or
# @description_en changes an existing one's password.
#
# @param FTP_USER text required default=ftpuser label="Пользователь" label_en="User" hint="Латиница в нижнем регистре, цифры, _ и -" hint_en="Lowercase letters, digits, _ and -"
# @param FTP_PASSWORD secret label="Пароль" label_en="Password" hint="Не короче 12 символов. Пусто — сгенерировать для нового пользователя; для существующего — оставить прежний" hint_en="At least 12 characters. Empty = generate for a new user; keep the old one for an existing user"
# @param FTP_TLS choice options=required,optional,off default=required label="Шифрование (TLS)" label_en="Encryption (TLS)" hint="required — только FTPS; optional — FTPS или обычный FTP; off — обычный FTP, пароль идёт открытым текстом" hint_en="required = FTPS only; optional = FTPS or plain FTP; off = plain FTP, the password travels in clear text"
# @param FTP_DOMAIN text label="Домен (необязательно)" label_en="Domain (optional)" hint="Для сертификата Let's Encrypt; нужен свободный порт 80. Пусто — самоподписанный сертификат" hint_en="For a Let's Encrypt certificate; port 80 must be free. Empty = a self-signed certificate" when=FTP_TLS=required
# @param ACME_EMAIL text label="E-mail для Let's Encrypt" label_en="E-mail for Let's Encrypt" hint="Необязательно, только с доменом" hint_en="Optional, only with a domain" when=FTP_TLS=required
# @param FTP_ROOT text required default=/srv/ftp label="Папка пользователей" label_en="Users folder" hint="У каждого пользователя своя подпапка" hint_en="Each user gets a subfolder"
# @param FTP_PORT number default=21 label="Порт FTP" label_en="FTP port"
# @param FTP_PASV_MIN number default=40000 label="Пассивные порты: от" label_en="Passive ports: from"
# @param FTP_PASV_MAX number default=40100 label="Пассивные порты: до" label_en="Passive ports: to"
#
# @result FTP_URL label="Адрес FTP" label_en="FTP address"
# @result FTP_USER label="Пользователь FTP" label_en="FTP user"
# @result FTP_PASSWORD label="Пароль FTP" label_en="FTP password"
# @result FTP_TLS label="Шифрование FTP" label_en="FTP encryption"
# @result FTP_PORT label="Порт FTP" label_en="FTP port" monitor="FTP"
#
# Runs standalone too: sudo FTP_USER=bob bash vsftpd.sh
set -Eeuo pipefail
trap 'echo -e "\nERROR line $LINENO: $BASH_COMMAND\n" >&2' ERR

GROUP=ftponly
USERLIST=/etc/vsftpd.sshm-users
CERT_DIR=/etc/ssl/sshm-vsftpd

# ---- shared helpers: base (identical in every built-in script that has them; edit all copies) ----
# bash 5.2 reads "&" in a ${var//a/b} replacement as the matched text; the helpers here mean it literally
shopt -u patsub_replacement 2>/dev/null || true

log(){ echo -e "\n== $* =="; }
warn(){ echo "WARNING: $*" >&2; }
die(){ echo "ERROR: $*" >&2; exit 1; }
cmd(){ command -v "$1" >/dev/null 2>&1; }
# Quaykeep: report a value back (saved to the server's attributes); no-op when run by hand
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

# opens a port range only when a firewall is actually filtering (never enables one); ufw and iptables write
# ranges as a:b, firewalld as a-b
open_port_range(){ # from to
  if cmd ufw && ufw status 2>/dev/null | grep -qi '^Status: active'; then
    ufw allow "$1:$2/tcp" >/dev/null && echo "Firewall (ufw): opened $1-$2/tcp"
  elif cmd firewall-cmd && systemctl is-active --quiet firewalld 2>/dev/null; then
    firewall-cmd -q --add-port="$1-$2/tcp" && firewall-cmd -q --permanent --add-port="$1-$2/tcp" &&
      echo "Firewall (firewalld): opened $1-$2/tcp"
  elif cmd iptables && iptables -S INPUT 2>/dev/null | grep -q '^-P INPUT DROP'; then
    iptables -C INPUT -p tcp --dport "$1:$2" -j ACCEPT 2>/dev/null || iptables -I INPUT -p tcp --dport "$1:$2" -j ACCEPT
    echo "Firewall (iptables): opened $1-$2/tcp (not persistent)"
  fi
  return 0
}

# a certificate for FTPS: Let's Encrypt for a domain (certbot standalone on port 80), self-signed otherwise;
# sets CERT and KEY
ensure_cert(){
  if [[ -n "$FTP_DOMAIN" ]]; then
    local live="/etc/letsencrypt/live/${FTP_DOMAIN}"
    if [[ ! -f "$live/fullchain.pem" ]]; then
      check_dns "$FTP_DOMAIN"
      pkg_install certbot # EPEL on CentOS / RHEL
      require_free_port 80 tcp
      open_port 80 tcp
      local email=(--register-unsafely-without-email)
      [[ -z "$ACME_EMAIL" ]] || email=(-m "$ACME_EMAIL")
      log "Getting a Let's Encrypt certificate for ${FTP_DOMAIN}"
      certbot certonly --standalone -d "$FTP_DOMAIN" --non-interactive --agree-tos "${email[@]}" --keep-until-expiring ||
        die "certbot failed: check that ${FTP_DOMAIN} points to this server and port 80 is reachable from the internet"
    fi
    install -d -m 0755 /etc/letsencrypt/renewal-hooks/deploy
    printf '#!/bin/sh\n# Quaykeep: vsftpd reads the certificate at start\nsystemctl restart vsftpd\n' \
      > /etc/letsencrypt/renewal-hooks/deploy/sshm-vsftpd.sh
    chmod 755 /etc/letsencrypt/renewal-hooks/deploy/sshm-vsftpd.sh
    systemctl enable --now certbot.timer >/dev/null 2>&1 || systemctl enable --now certbot-renew.timer >/dev/null 2>&1 || true
    CERT="$live/fullchain.pem"; KEY="$live/privkey.pem"
    return 0
  fi
  CERT="$CERT_DIR/cert.pem"; KEY="$CERT_DIR/key.pem"
  [[ -f "$CERT" && -f "$KEY" ]] && return 0
  log "Making a self-signed certificate"
  install -d -m 0700 "$CERT_DIR"
  openssl req -x509 -newkey rsa:2048 -nodes -days 3650 -subj "/CN=${PUBLIC_IP:-vsftpd}" \
    -keyout "$KEY" -out "$CERT" >/dev/null 2>&1 || die "openssl could not make a certificate"
  chmod 600 "$KEY"
}

write_conf(){
  local ssl=NO force=NO
  [[ "$FTP_TLS" == off ]] || ssl=YES
  [[ "$FTP_TLS" != required ]] || force=YES
  log "Writing $CONF"
  {
    echo "# written by Quaykeep (FTP server); the original is in ${CONF}.sshm-orig"
    echo "listen=YES"
    echo "listen_ipv6=NO"
    echo "listen_port=${FTP_PORT}"
    echo "anonymous_enable=NO"
    echo "local_enable=YES"
    echo "write_enable=YES"
    echo "local_umask=022"
    echo "dirmessage_enable=NO"
    echo "xferlog_enable=YES"
    echo "use_localtime=YES"
    echo "connect_from_port_20=NO"
    echo "chroot_local_user=YES"
    echo "secure_chroot_dir=${EMPTY_DIR}"
    echo "pam_service_name=vsftpd"
    echo "userlist_enable=YES"
    echo "userlist_deny=NO"
    echo "userlist_file=${USERLIST}"
    echo "pasv_enable=YES"
    echo "pasv_min_port=${FTP_PASV_MIN}"
    echo "pasv_max_port=${FTP_PASV_MAX}"
    [[ -z "$PUBLIC_IP" ]] || echo "pasv_address=${PUBLIC_IP}"
    echo "ssl_enable=${ssl}"
    if [[ "$ssl" == YES ]]; then
      echo "rsa_cert_file=${CERT}"
      echo "rsa_private_key_file=${KEY}"
      echo "allow_anon_ssl=NO"
      echo "force_local_logins_ssl=${force}"
      echo "force_local_data_ssl=${force}"
      echo "ssl_sslv2=NO"
      echo "ssl_sslv3=NO"
      echo "require_ssl_reuse=NO"
      echo "ssl_ciphers=HIGH"
    fi
  } > "$CONF"
  chmod 600 "$CONF"
}

# SELinux (CentOS / RHEL): vsftpd may write outside /var/ftp and listen on a non-standard port
selinux_for_ftp(){
  cmd getenforce && [[ "$(getenforce 2>/dev/null)" != Disabled ]] || return 0
  setsebool -P ftpd_full_access on 2>/dev/null || warn "SELinux: could not turn on ftpd_full_access"
  if [[ "$FTP_PORT" != 21 ]]; then
    cmd semanage || pkg_install policycoreutils-python-utils || true
    semanage port -a -t ftp_port_t -p tcp "$FTP_PORT" 2>/dev/null || semanage port -m -t ftp_port_t -p tcp "$FTP_PORT" 2>/dev/null ||
      warn "SELinux: could not allow vsftpd on port ${FTP_PORT}"
  fi
  echo "SELinux: vsftpd allowed"
}

# lists the user's folder over FTP(S) from the server itself: the login and the passive ports work
self_test(){ # password
  cmd curl || return 0
  local netrc rc=0 tls=()
  [[ "$FTP_TLS" == off ]] || tls=(--ssl-reqd -k)
  netrc="$(mktemp)"
  printf 'machine 127.0.0.1 login %s password %s\n' "$FTP_USER" "$1" > "$netrc"
  curl -fsS --max-time 20 ${tls[@]+"${tls[@]}"} --ftp-skip-pasv-ip --netrc-file "$netrc" "ftp://127.0.0.1:${FTP_PORT}/files/" >/dev/null || rc=$?
  rm -f "$netrc"
  if (( rc == 0 )); then echo "Self-test: login and listing over FTP${tls[*]:+S} work"
  else warn "Self-test: could not list the folder over FTP (curl exit ${rc})"; fi
  return 0
}

main(){
  require_root
  require_os

  FTP_USER="$(trim "${FTP_USER:-ftpuser}")"
  FTP_TLS="$(trim "${FTP_TLS:-required}")"
  FTP_DOMAIN="$(trim "${FTP_DOMAIN:-}")"; FTP_DOMAIN="${FTP_DOMAIN,,}"
  ACME_EMAIL="$(trim "${ACME_EMAIL:-}")"
  FTP_ROOT="$(trim "${FTP_ROOT:-/srv/ftp}")"; FTP_ROOT="${FTP_ROOT%/}"
  FTP_PORT="$(trim "${FTP_PORT:-21}")"
  FTP_PASV_MIN="$(trim "${FTP_PASV_MIN:-40000}")"
  FTP_PASV_MAX="$(trim "${FTP_PASV_MAX:-40100}")"
  local new_password="${FTP_PASSWORD:-}"
  [[ "$FTP_TLS" == required ]] || FTP_DOMAIN=""

  [[ "$FTP_USER" =~ ^[a-z_][a-z0-9_-]{0,31}$ ]] || die "Invalid user name: $FTP_USER"
  [[ "$FTP_TLS" =~ ^(required|optional|off)$ ]] || die "FTP_TLS must be required, optional or off"
  [[ -z "$FTP_DOMAIN" ]] || valid_domain "$FTP_DOMAIN" || die "Invalid domain: $FTP_DOMAIN"
  [[ "$FTP_ROOT" == /* && "$FTP_ROOT" =~ ^[A-Za-z0-9._/-]+$ && "$FTP_ROOT" != *..* ]] ||
    die "The users folder must be an absolute path of letters, digits and . _ - /"
  case "$FTP_ROOT" in
    /|/etc|/etc/*|/proc*|/sys*|/dev*|/run*|/boot*|/usr|/usr/*|/bin*|/sbin*|/lib*|/root|/root/*|/home|/tmp|/var|/var/tmp)
      die "Do not use ${FTP_ROOT} as the FTP users folder" ;;
  esac
  local p
  for p in "$FTP_PORT" "$FTP_PASV_MIN" "$FTP_PASV_MAX"; do
    [[ "$p" =~ ^[0-9]+$ ]] && (( p >= 1 && p <= 65535 )) || die "Invalid port: $p"
  done
  (( FTP_PASV_MIN <= FTP_PASV_MAX && FTP_PASV_MAX - FTP_PASV_MIN <= 1000 )) ||
    die "The passive ports must be a range of at most 1000 ports (from <= to)"
  (( FTP_PORT < FTP_PASV_MIN || FTP_PORT > FTP_PASV_MAX )) || die "The FTP port must not be in the passive range"
  [[ -z "$new_password" || ${#new_password} -ge 12 ]] || die "The password must be at least 12 characters"
  [[ "$new_password" != *[$'\n\r ']* ]] || die "The password must not contain spaces or line breaks"
  [[ "$FTP_TLS" != off ]] || warn "TLS is off: the password and the files travel over the network unencrypted"

  pkg_install vsftpd openssl curl ca-certificates iproute2
  if [[ "$OS_FAMILY" == rhel ]]; then CONF=/etc/vsftpd/vsftpd.conf; EMPTY_DIR=/usr/share/empty
  else CONF=/etc/vsftpd.conf; EMPTY_DIR=/var/run/vsftpd/empty; fi
  install -d -m 0755 "$EMPTY_DIR"
  [[ -f "${CONF}.sshm-orig" || ! -f "$CONF" ]] || cp -p "$CONF" "${CONF}.sshm-orig"

  local home="$FTP_ROOT/$FTP_USER" nologin existing=0
  nologin="$(command -v nologin 2>/dev/null || echo /usr/sbin/nologin)"
  getent group "$GROUP" >/dev/null || groupadd --system "$GROUP"
  if id "$FTP_USER" >/dev/null 2>&1; then
    id -nG "$FTP_USER" | tr ' ' '\n' | grep -qx "$GROUP" ||
      die "User ${FTP_USER} already exists and is not an FTP user made by this script; choose another name"
    existing=1
    echo "User ${FTP_USER} exists: updating it"
  fi
  # vsftpd's PAM checks the login shell against /etc/shells
  grep -qxF "$nologin" /etc/shells || echo "$nologin" >> /etc/shells

  install -d -m 0755 -o root -g root "$FTP_ROOT"
  if (( ! existing )); then
    log "Creating the user ${FTP_USER}"
    useradd -M -d "$home" -s "$nologin" -U -G "$GROUP" "$FTP_USER"
  fi
  # the chroot itself must not be writable by the user (vsftpd refuses it); uploads go to files/
  install -d -m 0755 -o root -g root "$home"
  [[ -d "$home/files" ]] || install -d -m 0750 -o "$FTP_USER" -g "$FTP_USER" "$home/files"
  touch "$USERLIST"; chmod 644 "$USERLIST"
  grep -qxF "$FTP_USER" "$USERLIST" || echo "$FTP_USER" >> "$USERLIST"

  local generated="" password="$new_password"
  if [[ -n "$new_password" ]]; then
    printf '%s:%s\n' "$FTP_USER" "$new_password" | chpasswd
    if (( existing )); then echo "New password set for ${FTP_USER}"; fi
  elif (( ! existing )); then
    generated="$(rand_pass 20)"; password="$generated"
    printf '%s:%s\n' "$FTP_USER" "$generated" | chpasswd
  fi

  # a rerun: our own vsftpd holds the port
  systemctl stop vsftpd >/dev/null 2>&1 || true
  require_free_port "$FTP_PORT" tcp
  PUBLIC_IP="$(public_ip || true)"
  CERT=""; KEY=""
  [[ "$FTP_TLS" == off ]] || ensure_cert
  write_conf
  if [[ "$OS_FAMILY" == rhel ]]; then selinux_for_ftp; fi

  log "Starting vsftpd"
  systemctl enable vsftpd >/dev/null 2>&1 || true
  if ! systemctl restart vsftpd; then
    journalctl -u vsftpd -n 20 --no-pager 2>/dev/null || true
    die "vsftpd did not start"
  fi
  sleep 1
  [[ -n "$(port_owner "$FTP_PORT" tcp)" ]] || { journalctl -u vsftpd -n 20 --no-pager 2>/dev/null || true; die "vsftpd does not listen on port ${FTP_PORT}"; }
  open_port "$FTP_PORT" tcp
  open_port_range "$FTP_PASV_MIN" "$FTP_PASV_MAX"
  [[ -z "$password" ]] || self_test "$password"

  local host url tls_text
  host="${FTP_DOMAIN:-${SSHM_HOST:-$PUBLIC_IP}}"
  if [[ "$host" == *:* && "$host" != \[* ]]; then host="[$host]"; fi
  url="ftp://${FTP_USER}@${host}"
  [[ "$FTP_PORT" == 21 ]] || url+=":${FTP_PORT}"
  case "$FTP_TLS" in
    required) tls_text="FTPS only (explicit TLS)" ;;
    optional) tls_text="FTPS or plain FTP" ;;
    *) tls_text="off (plain FTP)" ;;
  esac
  [[ -z "$CERT" || "$CERT" != "$CERT_DIR"/* ]] || tls_text+=", self-signed certificate"

  echo ""
  echo "FTP: ${url} (${tls_text})"
  echo "User: ${FTP_USER}${generated:+ / password ${generated}}"
  echo "Files: the folder /files in the client = ${home}/files on the server; passive ports ${FTP_PASV_MIN}-${FTP_PASV_MAX}"

  sshm_result FTP_URL "$url"
  sshm_result FTP_USER "$FTP_USER"
  [[ -z "$generated" ]] || sshm_result FTP_PASSWORD "$generated"
  sshm_result FTP_TLS "$tls_text"
  sshm_result FTP_PORT "$FTP_PORT"
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then main "$@"; fi
