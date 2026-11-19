#!/usr/bin/env bash
# @description Удаляет сайт из конфигурации nginx. Файлы сайта сохраняются.
# @description_en Removes the site from nginx. The website files are kept.
set -euo pipefail
fail(){ printf '%s\n' "$*" >&2; exit 1; }
[[ $EUID == 0 ]] || fail "Run as root"
site=""
enabled=/etc/nginx/sites-enabled/sshm-site.conf
for candidate in /etc/nginx/sites-available/sshm-site.conf /etc/nginx/conf.d/sshm-site.conf; do
  if [[ -e "$candidate" || -L "$candidate" ]]; then
    [[ -z "$site" && -f "$candidate" && ! -L "$candidate" ]] &&
      grep -qxF '# written by Quaykeep (static website); certbot adds its HTTPS lines here' "$candidate" ||
        fail "Cannot verify the nginx site configuration"
    site="$candidate"
  fi
done
[[ -n "$site" ]] || { echo "Quaykeep nginx site is absent"; exit 0; }
link=0
if [[ -e "$enabled" || -L "$enabled" ]]; then
  [[ -L "$enabled" && "$(realpath -e -- "$enabled")" == "$site" ]] || fail "The enabled site points somewhere else"
  link=1
fi
nginx -t
backup="$(mktemp -d "$(dirname "$site")/.qk-uninstall.XXXXXX")"
done_removing=0
restore(){
  if (( ! done_removing )) && [[ -f "$backup/site" ]]; then
    mv -- "$backup/site" "$site"
    if (( link )) && [[ ! -e "$enabled" && ! -L "$enabled" ]]; then ln -s -- "$site" "$enabled"; fi
    if systemctl is-active --quiet nginx; then systemctl reload nginx || true; fi
  fi
  rmdir -- "$backup"
}
trap restore EXIT
mv -- "$site" "$backup/site"
if (( link )); then rm -- "$enabled"; fi
nginx -t
if systemctl is-active --quiet nginx; then systemctl reload nginx; fi
rm -- "$backup/site"
done_removing=1
echo "Quaykeep nginx site removed. Files are kept."
