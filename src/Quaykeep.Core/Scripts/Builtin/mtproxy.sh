#!/usr/bin/env bash
# @name MTProxy для Telegram
# @name_en MTProxy for Telegram
# @group VPN
# @os ubuntu,debian,centos,rhel
# @description Устанавливает официальный MTProxy для Telegram как службу systemd (Linux x86-64).
# @description Ссылка для подключения сохраняется в результатах. Повторный запуск сохраняет секрет и обновляет настройки, когда Telegram доступен.
# @description_en Installs Telegram's official MTProxy as a systemd service (Linux x86-64).
# @description_en The connection link is saved in the results. Rerunning keeps the secret and refreshes the configuration when Telegram is reachable.
#
# @param MTPROXY_PORT number required default=443 label="Порт TCP" label_en="TCP port"
# @param SERVER_ADDRESS text label="Адрес сервера в ссылке" label_en="Server address in the link" hint="DNS-имя или IPv4; пусто — внешний IPv4" hint_en="DNS name or IPv4; empty = public IPv4"
# @param MTPROXY_PUBLIC_IP text label="Внешний IPv4 для NAT" label_en="Public IPv4 for NAT" hint="Необязательно; пусто — определить автоматически" hint_en="Optional; empty = autodetect"
# @result MTPROXY_URL label="Ссылка на MTProxy" label_en="MTProxy connection link"
# @result MTPROXY_PORT label="Порт MTProxy" label_en="MTProxy port" monitor="MTProxy"

set -Eeuo pipefail

DIR=/opt/quaykeep-mtproxy
UNIT=quaykeep-mtproxy.service
UNIT_FILE="/etc/systemd/system/$UNIT"
REVISION=69f2f083b57bbd94932cb9f4f32155f33e719ff1

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

build_proxy(){
  local destination="$1" upstream
  upstream="$(mktemp -d "$work/source.XXXXXX")"
  if [[ "$OS_FAMILY" == debian ]]; then
    pkg_install git make gcc libc6-dev libssl-dev zlib1g-dev
  else
    pkg_install git make gcc glibc-devel openssl-devel zlib-devel
  fi
  log "Building Telegram MTProxy"
  git -C "$upstream" init -q
  git -C "$upstream" fetch -q --depth 1 https://github.com/TelegramMessenger/MTProxy.git "$REVISION"
  git -C "$upstream" checkout -q --detach FETCH_HEAD

  # Upstream accepts the client secret via -S. Read it from systemd's environment file
  # when -S is given a placeholder, so it never appears in the service command line.
  local source="$upstream/mtproto/mtproto-proxy.c"
  local anchor='      if (strlen (optarg) != 32) {'
  [[ "$(grep -Fxc "$anchor" "$source")" == 1 ]] || die "Unexpected MTProxy option parser"
  sed -i '/^      if (strlen (optarg) != 32) {/i\
      if (val == '"'"'S'"'"' \&\& !strcmp (optarg, "-")) {\
        optarg = getenv ("QK_MTPROXY_SECRET");\
        if (!optarg) {\
          kprintf ("MTProxy client secret is missing\\n");\
          exit (2);\
        }\
      }' "$source"

  if ! make -C "$upstream" -j 2 CC='gcc -fcommon' > "$work/build.log" 2>&1; then
    tail -n 50 "$work/build.log" >&2
    die "MTProxy build failed"
  fi
  install -m 0755 "$upstream/objs/bin/mtproto-proxy" "$destination"
}

write_service(){
  cat > "$work/$UNIT" <<'UNIT'
# Installed by Quaykeep MTProxy
[Unit]
Description=Telegram MTProxy
Wants=network-online.target
After=network-online.target

[Service]
DynamicUser=yes
EnvironmentFile=/opt/quaykeep-mtproxy/current/service.env
ExecStart=/opt/quaykeep-mtproxy/current/mtproto-proxy -H ${QK_MTPROXY_PORT} -S - $QK_MTPROXY_NAT_ARGS --aes-pwd /opt/quaykeep-mtproxy/current/proxy-secret /opt/quaykeep-mtproxy/current/proxy-multi.conf
AmbientCapabilities=CAP_NET_BIND_SERVICE
CapabilityBoundingSet=CAP_NET_BIND_SERVICE
LimitCORE=0
Restart=on-failure
RestartSec=3

[Install]
WantedBy=multi-user.target
UNIT
}

listening(){
  local port="$1" pid="$2" sockets
  [[ "$pid" =~ ^[1-9][0-9]*$ ]] || return 1
  sockets="$(ss -Hlnpt "sport = :$port")"
  [[ -n "$sockets" ]] || return 1
  awk -v pid="$pid" 'index($0, "pid=" pid ",") == 0 { exit 1 }' <<< "$sockets"
}

wait_for_proxy(){
  local port="$1" i pid
  for (( i = 0; i < 15; i++ )); do
    pid="$(systemctl show -p MainPID --value "$UNIT")"
    if listening "$port" "$pid"; then return 0; fi
    sleep 1
  done
  return 1
}

switch_config(){
  ln -sfn -- "$1" "$work/current"
  mv -Tf -- "$work/current" "$DIR/current"
}

download_config(){
  local endpoint="$1" destination="$2" attempt
  for attempt in 1 2 3; do
    curl -fsSL --connect-timeout 10 --max-time 30 "https://core.telegram.org/$endpoint" -o "$destination" && return 0
    sleep "$attempt"
  done
  return 1
}

valid_proxy_config(){
  local config="$1"
  [[ -s "$config/proxy-secret" && "$(wc -c < "$config/proxy-secret")" -ge 32 ]] &&
    grep -q '^proxy_for ' "$config/proxy-multi.conf"
}

valid_ipv4(){
  local octet
  [[ "$1" =~ ^(0|[1-9][0-9]{0,2})(\.(0|[1-9][0-9]{0,2})){3}$ ]] || return 1
  for octet in ${1//./ }; do (( octet <= 255 )) || return 1; done
}

relay_source_ip(){
  local relay
  relay="$(awk '$1 == "proxy_for" && $3 ~ /^[0-9.]+:[0-9]+;$/ { sub(/:.*/, "", $3); print $3; exit }' "$1")"
  [[ -n "$relay" ]] || return 0
  ip -4 -o route get "$relay" 2>/dev/null | awk '{ for (i = 1; i < NF; i++) if ($i == "src") { print $(i + 1); exit } }'
}

discover_public_ipv4(){
  local url candidate
  for url in https://api.ipify.org https://ifconfig.me/ip; do
    candidate="$(curl -fsS4 --max-time 5 "$url" 2>/dev/null)" || continue
    if valid_ipv4 "$candidate"; then printf '%s\n' "$candidate"; return 0; fi
  done
  return 1
}

main(){
  require_root
  require_os
  [[ "$(uname -m)" == x86_64 ]] || die "Telegram MTProxy requires x86-64 with SSSE3 and PCLMULQDQ"
  grep -qE '^flags[[:space:]]*:.*\<ssse3\>' /proc/cpuinfo &&
    grep -qE '^flags[[:space:]]*:.*\<pclmulqdq\>' /proc/cpuinfo ||
    die "Telegram MTProxy requires SSSE3 and PCLMULQDQ CPU support"
  [[ -d /run/systemd/system ]] || die "A running systemd is required"

  local port address public
  port="$(trim "${MTPROXY_PORT:-443}")"
  [[ "$port" =~ ^[0-9]{1,5}$ ]] && (( 10#$port >= 1 && 10#$port <= 65535 )) ||
    die "The TCP port must be between 1 and 65535"
  port=$((10#$port))
  address="$(trim "${SERVER_ADDRESS:-}")"
  public="$(trim "${MTPROXY_PUBLIC_IP:-}")"
  [[ -z "$public" ]] || valid_ipv4 "$public" || die "Enter a valid public IPv4 address for NAT"
  local host_pattern='^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)*$'
  [[ -z "$address" || "$address" =~ $host_pattern ]] ||
    die "Enter a DNS name or an IPv4 address for the connection link"

  pkg_install ca-certificates curl openssl iproute2 util-linux

  # Serialize reruns so the active configuration and generated secret are updated together.
  install -d -m 0755 "$DIR"
  exec 9>"$DIR/install.lock"
  flock -n 9 || die "An MTProxy installation is already running"
  local previous="" active=0 pid secret stage
  if [[ -e "$UNIT_FILE" || -L "$UNIT_FILE" ]]; then
    [[ -f "$UNIT_FILE" && ! -L "$UNIT_FILE" ]] &&
      grep -qxF '# Installed by Quaykeep MTProxy' "$UNIT_FILE" || die "$UNIT_FILE already exists"
  elif [[ "$(systemctl show -p LoadState --value "$UNIT")" != not-found ]]; then
    die "$UNIT already exists"
  fi
  if [[ -L "$DIR/current" ]]; then
    previous="$(readlink "$DIR/current")"
    [[ "$previous" =~ ^config\.[a-zA-Z0-9]+$ && -f "$DIR/$previous/service.env" ]] ||
      die "Unexpected MTProxy configuration path"
  elif [[ -e "$DIR/current" ]]; then
    die "$DIR/current already exists"
  fi

  pid="$(systemctl show -p MainPID --value "$UNIT")"
  if systemctl is-active --quiet "$UNIT"; then active=1; fi
  if [[ -z "$previous" ]] || ! listening "$port" "$pid"; then require_free_port "$port"; fi

  work="$(mktemp -d "$DIR/.install.XXXXXX")"
  trap 'rm -rf -- "$work"' EXIT
  stage="$work/config"
  install -d -m 0755 "$stage"
  if ! { download_config getProxySecret "$stage/proxy-secret" &&
         download_config getProxyConfig "$stage/proxy-multi.conf" && valid_proxy_config "$stage"; }; then
    [[ -n "$previous" ]] && valid_proxy_config "$DIR/$previous" ||
      die "Could not obtain valid proxy configuration from Telegram"
    warn "Could not refresh Telegram configuration. Reusing the installed files."
    cp -- "$DIR/$previous/"{proxy-secret,proxy-multi.conf} "$stage/"
  fi

  # Telegram's relay handshake uses the address it sees on the connection.
  local private nat_args="" detected
  private="$(relay_source_ip "$stage/proxy-multi.conf" || true)"
  if [[ -z "$public" ]]; then
    if detected="$(discover_public_ipv4)"; then
      public="$detected"
    elif [[ -n "$previous" ]]; then
      public="$(sed -n 's/^QK_MTPROXY_PUBLIC_IP=//p' "$DIR/$previous/service.env")"
    fi
  fi
  if valid_ipv4 "$private" && valid_ipv4 "$public" && [[ "$private" != "$public" ]]; then
    nat_args="--nat-info $private:$public"
  elif [[ -z "$public" && "$private" =~ ^(10\.|172\.(1[6-9]|2[0-9]|3[01])\.|192\.168\.|100\.(6[4-9]|[7-9][0-9]|1[01][0-9]|12[0-7])\.) ]]; then
    warn "Could not discover the public IPv4 address. If this server is behind NAT, enter its public IPv4 address."
  fi
  address="${address:-${public:-$private}}"
  [[ "$address" =~ $host_pattern ]] || die "Could not determine an IPv4 address. Enter the address for the link."

  if [[ -n "$previous" && -x "$DIR/$previous/mtproto-proxy" && "$(<"$DIR/$previous/revision")" == "$REVISION" ]]; then
    ln "$DIR/$previous/mtproto-proxy" "$stage/mtproto-proxy"
  else
    build_proxy "$stage/mtproto-proxy"
  fi
  printf '%s\n' "$REVISION" > "$stage/revision"

  if [[ -n "$previous" ]]; then
    secret="$(sed -n 's/^QK_MTPROXY_SECRET=//p' "$DIR/$previous/service.env")"
    [[ "$secret" =~ ^[0-9a-f]{32}$ ]] || die "Existing MTProxy secret is invalid"
  else
    secret="$(openssl rand -hex 16)"
  fi
  (umask 077; printf 'QK_MTPROXY_PORT=%s\nQK_MTPROXY_SECRET=%s\nQK_MTPROXY_PUBLIC_IP=%s\nQK_MTPROXY_NAT_ARGS="%s"\n' \
    "$port" "$secret" "$public" "$nat_args" > "$stage/service.env")
  stage="config.${work##*.}"
  mv -T -- "$work/config" "$DIR/$stage"

  if [[ -f "$UNIT_FILE" ]]; then cp -p -- "$UNIT_FILE" "$work/previous-unit"; fi
  write_service
  install -m 0644 "$work/$UNIT" "$UNIT_FILE"
  switch_config "$stage"
  log "Starting Telegram MTProxy on TCP port $port"
  if ! systemctl daemon-reload || ! systemctl restart "$UNIT" || ! wait_for_proxy "$port" || ! systemctl enable "$UNIT"; then
    journalctl -u "$UNIT" -n 20 --no-pager >&2 || true
    systemctl stop "$UNIT" || true
    if [[ -n "$previous" ]]; then switch_config "$previous"; else rm -f -- "$DIR/current"; fi
    if [[ -f "$work/previous-unit" ]]; then
      install -m 0644 "$work/previous-unit" "$UNIT_FILE"
    else
      systemctl disable "$UNIT" || true
      rm -f -- "$UNIT_FILE"
    fi
    systemctl daemon-reload || true
    if (( active )); then systemctl start "$UNIT" || warn "Could not restart the previous MTProxy service"; fi
    rm -rf -- "$DIR/$stage"
    die "MTProxy did not start successfully"
  fi
  if [[ -n "$previous" ]]; then rm -rf -- "$DIR/$previous"; fi
  open_port "$port" tcp

  # Report the credential on first success or when the advertised endpoint changes.
  if [[ ! -f "$DIR/reported-endpoint" || "$(<"$DIR/reported-endpoint")" != "$address:$port" ]]; then
    local link="tg://proxy?server=$address&port=$port&secret=dd$secret"
    sshm_result MTPROXY_URL "$link"
    if [[ -z "${SSHM_RESULT:-}" && -t 1 ]]; then printf '%s\n' "$link"; fi
    printf '%s\n' "$address:$port" > "$work/reported-endpoint"
    mv -T -- "$work/reported-endpoint" "$DIR/reported-endpoint"
  fi
  sshm_result MTPROXY_PORT "$port"
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then main "$@"; fi
