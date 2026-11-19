#!/usr/bin/env bash
# @description Удаляет Docker Engine, установленный Quaykeep, если в нём нет контейнеров и он не входит в Swarm. Данные Docker сохраняются.
# @description_en Removes a Quaykeep-installed Docker Engine if there are no containers and it is outside Swarm. Docker data is kept.
set -euo pipefail
fail(){ printf '%s\n' "$*" >&2; exit 1; }
[[ $EUID == 0 ]] || fail "Run as root"
marker=/var/lib/quaykeep/docker-ce-installed
[[ -f "$marker" && ! -L "$marker" ]] || fail "Quaykeep cannot verify that it installed this Docker Engine"
docker_local=(docker --host unix:///var/run/docker.sock)
[[ "$("${docker_local[@]}" info --format '{{.Swarm.LocalNodeState}}')" == inactive ]] ||
  fail "Cannot verify a local Docker Engine outside Swarm"
containers="$("${docker_local[@]}" ps -aq)"
[[ -z "$containers" ]] || fail "Uninstall all containers before removing Docker Engine"
if command -v apt-get >/dev/null && dpkg-query -W -f='${Status}' docker-ce | grep -qx 'install ok installed'; then
  apt-get -y remove docker-ce
elif command -v dnf >/dev/null && rpm -q docker-ce >/dev/null; then
  dnf -y --setopt=clean_requirements_on_remove=False remove docker-ce
else
  fail "Cannot verify the installed Docker CE package"
fi
rm -- "$marker"
echo "Docker Engine removed. Docker data is kept."
