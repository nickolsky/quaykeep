set -euo pipefail
fail(){ printf '%s\n' "$*" >&2; exit 1; }
[[ $EUID == 0 ]] || fail "Run as root"
file="$DIR/docker-compose.yml"
if [[ ! -e "$DIR" && ! -L "$DIR" ]]; then echo "Deployment is absent"; exit 0; fi
[[ -d "$DIR" && "$(realpath -e -- "$DIR")" == "$DIR" && -f "$file" && ! -L "$file" ]] ||
  fail "Cannot verify the installer’s Compose file"
cd "$DIR"
compose=(docker compose --project-directory "$DIR" -f "$file")
images="$("${compose[@]}" config --images "$SERVICE")"
grep -Eq "^$IMAGE([:@]|$)" <<< "$images" || fail "The Compose file does not contain the expected app image"
services="$("${compose[@]}" config --services)"
grep -qxF "$SERVICE" <<< "$services" || fail "The expected app service is missing"
for service in $services; do
  [[ " $SERVICES " == *" $service "* ]] || fail "Unexpected service in the Compose file: $service"
done

# Work from Docker's labels, including stopped containers and custom project names.
filter='com.docker.compose.project'
ids="$(docker ps -aq --filter "label=$filter.working_dir=$DIR")"
if [[ -z "$ids" ]]; then echo "Deployment is already down. Data and configuration are kept."; exit 0; fi
project="$(docker inspect -f '{{index .Config.Labels "com.docker.compose.project"}}' $ids | sort -u)"
[[ "$project" =~ ^[a-z0-9][a-z0-9_-]*$ ]] || fail "Cannot verify the Compose project name"
ids="$(docker ps -aq --filter "label=$filter=$project")"
for id in $ids; do
  metadata="$(docker inspect -f '{{index .Config.Labels "com.docker.compose.project.working_dir"}}|{{index .Config.Labels "com.docker.compose.project.config_files"}}|{{index .Config.Labels "com.docker.compose.service"}}|{{.Config.Image}}' "$id")"
  IFS='|' read -r directory config service image <<< "$metadata"
  [[ "$directory" == "$DIR" && "$config" == "$file" && " $SERVICES " == *" $service "* ]] ||
    fail "Project $project also includes a container outside this installer"
  if [[ "$service" == "$SERVICE" ]] && ! grep -Eq "^$IMAGE([:@]|$)" <<< "$image"; then
    fail "The deployed image does not match this installer"
  fi
done
"${compose[@]}" -p "$project" down --remove-orphans
echo "Deployment removed. Data, images and configuration are kept."
