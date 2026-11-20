set -eu
dir=/etc/quaykeep/relays
file=/etc/systemd/system/quaykeep-relay@.service
unit() { printf 'quaykeep-relay@%s.service' "$1"; }
fail() { echo "$*" >&2; exit 1; }

[ -d /run/systemd/system ] || fail 'A running systemd is required for cross-family forwards.'
install -d -m 0755 "$dir"
exec 9>"$dir/.lock"
flock -n 9 || fail 'Another cross-family forward change is in progress.'

if [ -n "$old" ]; then
    [ -f "$dir/$old.conf" ] || fail 'The selected forward is no longer installed.'
fi
if [ "$action" = remove ]; then
    oldunit=$(unit "$old")
    active=0
    enabled=0
    if systemctl is-active --quiet "$oldunit"; then active=1; fi
    if systemctl is-enabled --quiet "$oldunit"; then enabled=1; fi
    if ! systemctl disable --now "$oldunit" || ! rm -- "$dir/$old.conf"; then
        if [ "$enabled" = 1 ]; then systemctl enable "$oldunit" || echo 'Could not restore boot enablement.' >&2; fi
        if [ "$active" = 1 ]; then systemctl start "$oldunit" || echo 'Could not restart the previous relay.' >&2; fi
        fail 'Could not remove the relay.'
    fi
else
    [ -x /usr/bin/python3 ] || fail 'Install Python 3 on the forwarding server first.'
    /usr/bin/python3 -c 'import sys; sys.exit(sys.version_info < (3, 8))' || fail 'Python 3.8 or later is required.'
    command -v ip >/dev/null 2>&1 || fail 'The ip command is required.'
    stage=$(mktemp -d "$dir/.apply.XXXXXX")
    newids=''
    restore=0
    active=0
    enabled=0
    cleanup() {
        result=$?
        if [ "$result" -ne 0 ]; then
            for id in $newids; do
                systemctl disable --now "$(unit "$id")" >/dev/null 2>&1 || true
                rm -f -- "$dir/$id.conf"
            done
            if [ "$restore" = 1 ]; then
                if [ "$enabled" = 1 ]; then systemctl enable "$(unit "$old")" || echo 'Could not restore boot enablement.' >&2; fi
                if [ "$active" = 1 ]; then systemctl start "$(unit "$old")" || echo 'Could not restart the previous relay.' >&2; fi
            fi
        fi
        rm -rf -- "$stage"
    }
    trap cleanup EXIT
    trap 'exit 1' HUP INT TERM
    # Preflight every destination before stopping a previous listener.
    while read -r id protocol address port target targetport; do
        [ ! -e "$dir/$id.conf" ] || fail 'The forward ID is already in use.'
        ip route get "$target" >/dev/null || fail "No route to $target from the forwarding server."
        printf '%s %s %s %s %s\n' "$protocol" "$address" "$port" "$target" "$targetport" > "$stage/$id.conf"
        chmod 0644 "$stage/$id.conf"
    done <<EOF
$definitions
EOF

    if [ -e "$file" ] || [ -L "$file" ]; then
        [ ! -L "$file" ] && grep -qxF '# Managed by Quaykeep cross-family forwarding' "$file" ||
            fail 'The relay systemd unit path is already in use.'
    fi
    printf '%s\n' "$program" > "$stage/relay.py"
    printf '%s\n' "$template" > "$stage/unit"
    chmod 0644 "$stage/relay.py" "$stage/unit"
    if [ ! -f "$dir/relay.py" ] || [ "$(cat "$dir/relay.py")" != "$program" ]; then
        mv -f -- "$stage/relay.py" "$dir/relay.py"
    fi
    if [ ! -f "$file" ] || [ "$(cat "$file")" != "$template" ]; then
        unittemp=$(mktemp /etc/systemd/system/.quaykeep-relay.XXXXXX)
        if ! install -m 0644 "$stage/unit" "$unittemp" || ! mv -f -- "$unittemp" "$file"; then
            rm -f -- "$unittemp"
            fail 'Could not install the relay unit.'
        fi
    fi
    systemctl daemon-reload

    if [ -n "$old" ]; then
        if systemctl is-active --quiet "$(unit "$old")"; then active=1; fi
        if systemctl is-enabled --quiet "$(unit "$old")"; then enabled=1; fi
        restore=1
        systemctl stop "$(unit "$old")"
    fi
    while read -r id protocol address port target targetport; do
        mv -- "$stage/$id.conf" "$dir/$id.conf"
        newids="$newids $id"
        if ! systemctl enable --now "$(unit "$id")"; then
            journalctl -u "$(unit "$id")" -n 8 --no-pager >&2 || true
            fail "Could not bind $protocol $address port $port."
        fi
    done <<EOF
$definitions
EOF
    if [ -n "$old" ]; then
        systemctl disable "$(unit "$old")"
        rm -- "$dir/$old.conf"
    fi
fi
echo SSHM_OK
echo SSHM_PERSIST=systemd
