#!/usr/bin/env bash
# Environnement de dev UmbraSync sur Mac Apple Silicon avec apple/container (une micro-VM par conteneur).
# Ce script remplace Compose : il ordonne le démarrage, et se met à jour tout seul depuis la branche
# apple-arm-support (images publiées sur GHCR par .github/workflows/apple-arm-images.yml).
#
# Usage : ./dev-container.sh <commande>
#   setup               une fois : domaine DNS .test (sudo), service container, .env
#   up                  démarre tout (infra si absente, puis server/auth/files/nginx)
#   apps                recrée seulement server, auth, files et nginx (données intactes)
#   update              ce que lance launchd : suit la branche, déploie les nouvelles images
#   deploy <tag>        déploie un tag d'image précis (ex. sha-ed1d76d)
#   rollback            revient au tag déployé précédemment
#   build               compile les images en local (IMAGE_SOURCE=local)
#   install-agent       installe la mise à jour automatique (launchd, toutes les 2 minutes)
#   uninstall-agent     la retire
#   down | status | logs <service> | schema <fichier.sql> | reset
set -euo pipefail

DIR="$(cd "$(dirname "$0")" && pwd)"
REPO="$(git -C "$DIR" rev-parse --show-toplevel)"
STATE="$DIR/state"                       # ignoré par git : tags déployés, config S3, verrou
P="umbra"                                # préfixe des conteneurs : umbra-postgres, umbra-server…
DOMAIN="test"
APPS=(server auth files)
AGENT="net.umbrasync.dev-update"
AGENT_PLIST="$HOME/Library/LaunchAgents/$AGENT.plist"

log() { echo "$(date '+%F %T') $*"; }
die() { echo "Erreur : $*" >&2; exit 1; }

load_env() {
  [ -f "$DIR/.env" ] || die ".env absent : lance d'abord ./dev-container.sh setup"
  set -a; . "$DIR/.env"; set +a
  UMBRA_ROOT="${UMBRA_ROOT:-/Volumes/UmbraDev}"
  APP_ROOT="$UMBRA_ROOT/container"
  IMAGE_SOURCE="${IMAGE_SOURCE:-ghcr}"
  IMAGE_REPO="${IMAGE_REPO:-ghcr.io/ashfall-codex}"
  BRANCH="${BRANCH:-apple-arm-support}"
  mkdir -p "$STATE"
}

deployed_tag() { cat "$STATE/deployed" 2>/dev/null || echo "$BRANCH"; }
image() {  # image <server|auth|files> [tag]
  if [ "$IMAGE_SOURCE" = "local" ]; then echo "umbra-dev/$1:latest"
  else echo "$IMAGE_REPO/umbra-$1:${2:-$(deployed_tag)}"; fi
}

state_of() { container ls --all 2>/dev/null | awk -v n="$P-$1" '$1==n {print $5}'; }
is_running() { [ "$(state_of "$1")" = "running" ]; }

ensure_system() {
  container ls >/dev/null 2>&1 && return 0
  log "Démarrage du service container"
  container system start --app-root "$APP_ROOT"
}

run() {  # run <nom> <args container run...>
  local n="$P-$1"; shift
  container rm --force "$n" >/dev/null 2>&1 || true
  container run -d --name "$n" "$@" >/dev/null
  log "démarré : $n"
}

wait_for() {  # wait_for <service> <commande...>
  local s="$1"; shift
  for _ in $(seq 1 60); do
    container exec "$P-$s" "$@" >/dev/null 2>&1 && return 0
    sleep 2
  done
  die "$P-$s ne répond pas au bout de 2 minutes (./dev-container.sh logs $s)"
}

# ---------- Infrastructure : jamais recréée si elle tourne déjà ----------

infra() {
  for v in pgdata redisdata s3data mcdf filescache; do
    container volume inspect "$P-$v" >/dev/null 2>&1 || container volume create "$P-$v" >/dev/null
  done

  is_running postgres || run postgres -m 1G -c 1 -p 127.0.0.1:5433:5432 \
    -e POSTGRES_USER=mare -e "POSTGRES_PASSWORD=$POSTGRES_PASSWORD" -e POSTGRES_DB=mare \
    -e PGDATA=/var/lib/postgresql/data/pgdata -v "$P-pgdata:/var/lib/postgresql/data" postgres:15

  is_running redis || run redis -m 256M -c 1 -v "$P-redisdata:/data" redis:7-alpine redis-server --appendonly yes

  if ! is_running s3; then
    # SeaweedFS remplace le bucket Scaleway ; lecture anonyme sur le bucket, comme le CDN en prod.
    mkdir -p "$STATE/s3"
    cat > "$STATE/s3/s3.json" <<EOF
{"identities":[
 {"name":"umbra","credentials":[{"accessKey":"$MINIO_ROOT_USER","secretKey":"$MINIO_ROOT_PASSWORD"}],
  "actions":["Admin","Read","Write","List","Tagging"]},
 {"name":"anonymous","actions":["Read:umbrasync"]}
]}
EOF
    run s3 -m 512M -c 1 -p 8333:8333 -v "$P-s3data:/data" -v "$STATE/s3:/config:ro" \
      chrislusf/seaweedfs:latest server -dir=/data -s3 -s3.port=8333 -s3.config=/config/s3.json \
      -master.volumeSizeLimitMB=1024 -volume.max=0
  fi

  wait_for postgres pg_isready -U mare -d mare
  wait_for redis redis-cli ping
  wait_for s3 wget -q -O /dev/null http://127.0.0.1:9333/cluster/status

  local weed='weed shell -master=localhost:9333'
  if ! container exec "$P-s3" sh -c "echo s3.bucket.list | $weed" 2>/dev/null | grep -q umbrasync; then
    for _ in $(seq 1 15); do
      container exec "$P-s3" sh -c "echo 's3.bucket.create -name umbrasync' | $weed" >/dev/null 2>&1 && break
      sleep 2
    done
    container exec "$P-s3" sh -c "echo s3.bucket.list | $weed" | grep -q umbrasync \
      || die "le bucket umbrasync n'a pas pu être créé (./dev-container.sh logs s3)"
  fi
}

# ---------- Services UmbraSync : recréés à chaque déploiement ----------

apps() {
  local tag="${1:-$(deployed_tag)}"
  local dotnet=(-e "ConnectionStrings__DefaultConnection=Host=$P-postgres.$DOMAIN;Port=5432;Database=mare;Username=mare;Password=$POSTGRES_PASSWORD"
                -e "MareSynchronos__Jwt=$JWT")
  local c="$DIR/container"

  run server -m 3G -c 2 --env-file "$c/server.env" "${dotnet[@]}" \
    -e "MareSynchronos__CdnFullUrl=http://$PUBLIC_HOST" -v "$P-mcdf:/data/mcdf" "$(image server "$tag")"
  # mare-server applique les migrations EF au démarrage : auth et files attendent qu'il ait fini.
  for _ in $(seq 1 60); do
    container logs "$P-server" 2>/dev/null | grep -q "Application started" && break
    is_running server || die "mare-server s'est arrêté au démarrage (./dev-container.sh logs server)"
    sleep 2
  done
  run auth -m 512M -c 1 --env-file "$c/auth.env" "${dotnet[@]}" "$(image auth "$tag")"
  run files -m 1536M -c 2 --env-file "$c/files.env" "${dotnet[@]}" \
    -e "MareSynchronos__CdnFullUrl=http://$PUBLIC_HOST:8333/umbrasync" \
    -e "MareSynchronos__ScalewayAccessKey=$MINIO_ROOT_USER" -e "MareSynchronos__ScalewaySecretKey=$MINIO_ROOT_PASSWORD" \
    -v "$P-filescache:/data/cache" "$(image files "$tag")"
  sleep 3
  run nginx -m 256M -c 1 -p 80:80 -v "$c/nginx:/etc/nginx/conf.d:ro" nginx:1.27-alpine
  check
}

check() {
  sleep 2
  if curl -s -m 3 -o /dev/null http://127.0.0.1/.well-known/Umbra/client; then
    log "OK : la pile répond sur http://$PUBLIC_HOST"
  else
    log "Attention : le port 80 ne répond pas depuis macOS. Vérifie la permission Réseau local de" \
        "container-runtime-linux (voir README), puis container system stop / start."
    return 1
  fi
}

pull_all() {  # pull_all <tag> : échoue si une des trois images n'est pas (encore) publiée
  for a in "${APPS[@]}"; do container image pull "$(image "$a" "$1")" >/dev/null 2>&1 || return 1; done
}

deploy() {  # deploy <tag>
  local tag="${1:-}"; [ -n "$tag" ] || die "précise un tag, ex. sha-ed1d76d"
  [ "$IMAGE_SOURCE" = "ghcr" ] || die "deploy ne sert qu'avec IMAGE_SOURCE=ghcr"
  pull_all "$tag" || die "images $tag introuvables sur $IMAGE_REPO"
  local prev; prev="$(deployed_tag)"
  [ "$prev" != "$tag" ] && echo "$prev" > "$STATE/previous"
  echo "$tag" > "$STATE/deployed"
  log "Déploiement de $tag (précédent : $prev)"
  ensure_system; infra; apps "$tag"
}

# ---------- Mise à jour automatique ----------

lock() {
  if ! mkdir "$STATE/lock" 2>/dev/null; then
    # verrou de plus de 30 minutes : une exécution précédente a été tuée
    if [ -n "$(find "$STATE/lock" -maxdepth 0 -mmin +30 2>/dev/null)" ]; then rmdir "$STATE/lock"; mkdir "$STATE/lock"
    else exit 0; fi
  fi
  trap 'rmdir "$STATE/lock" 2>/dev/null || true' EXIT
}

update() {
  lock
  ensure_system
  local cur remote
  git -C "$REPO" fetch -q origin "$BRANCH"
  cur="$(git -C "$REPO" rev-parse --short=7 HEAD)"
  remote="$(git -C "$REPO" rev-parse --short=7 FETCH_HEAD)"

  if [ "$cur" != "$remote" ]; then
    if [ "$IMAGE_SOURCE" = "ghcr" ] && ! pull_all "sha-$remote"; then
      log "Nouveau commit $remote : images pas encore publiées, nouvel essai au prochain passage"
    else
      log "Mise à jour $cur -> $remote"
      git -C "$REPO" merge -q --ff-only FETCH_HEAD || die "avance rapide impossible : modifications locales dans $REPO ?"
      git -C "$REPO" submodule update --init -q
      # Le script lui-même a pu changer : on relance sa nouvelle version.
      if [ "$IMAGE_SOURCE" = "ghcr" ]; then "$DIR/dev-container.sh" deploy "sha-$remote"
      else "$DIR/dev-container.sh" build && "$DIR/dev-container.sh" up; fi
      return
    fi
  fi

  # Rien de neuf : on s'assure seulement que tout tourne (après un redémarrage du Mac, par exemple).
  local missing=0
  for s in postgres redis s3 "${APPS[@]}" nginx; do is_running "$s" || missing=1; done
  if [ "$missing" = 1 ]; then log "Conteneurs arrêtés : redémarrage"; infra; apps; fi
}

install_agent() {
  mkdir -p "$HOME/Library/LaunchAgents" "$HOME/Library/Logs"
  cat > "$AGENT_PLIST" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>Label</key><string>$AGENT</string>
  <key>ProgramArguments</key><array><string>$DIR/dev-container.sh</string><string>update</string></array>
  <key>EnvironmentVariables</key><dict><key>PATH</key><string>/usr/local/bin:/opt/homebrew/bin:/usr/bin:/bin:/usr/sbin:/sbin</string></dict>
  <key>StartInterval</key><integer>120</integer>
  <key>RunAtLoad</key><true/>
  <key>StandardOutPath</key><string>$HOME/Library/Logs/umbra-dev-update.log</string>
  <key>StandardErrorPath</key><string>$HOME/Library/Logs/umbra-dev-update.log</string>
</dict></plist>
EOF
  launchctl bootout "gui/$(id -u)/$AGENT" 2>/dev/null || true
  launchctl bootstrap "gui/$(id -u)" "$AGENT_PLIST"
  echo "Mise à jour automatique active. Journal : tail -f ~/Library/Logs/umbra-dev-update.log"
}

uninstall_agent() {
  launchctl bootout "gui/$(id -u)/$AGENT" 2>/dev/null || true
  rm -f "$AGENT_PLIST"
  echo "Mise à jour automatique retirée."
}

# ---------- Préparation et outils ----------

setup() {
  command -v container >/dev/null || die "apple/container n'est pas installé (https://github.com/apple/container/releases)"
  mkdir -p ~/.config/container
  if ! grep -q "domain *= *\"$DOMAIN\"" ~/.config/container/config.toml 2>/dev/null; then
    printf '[dns]\ndomain = "%s"\n' "$DOMAIN" >> ~/.config/container/config.toml
  fi
  container system dns list -q 2>/dev/null | grep -qx "$DOMAIN" || sudo container system dns create "$DOMAIN"

  if [ ! -f "$DIR/.env" ]; then
    local old="/Volumes/UmbraDev/compose/.env"
    if [ -f "$old" ]; then
      # Reprise de l'ancienne installation : même mot de passe Postgres, sinon la base existante est illisible.
      cp "$old" "$DIR/.env"; echo "Secrets repris de $old"
    else
      cat > "$DIR/.env" <<EOF
# Secrets de DEV uniquement. Ne jamais y mettre ceux de la prod. Ce fichier n'est pas versionné.
PUBLIC_HOST=$(hostname -s | tr '[:upper:]' '[:lower:]')
POSTGRES_PASSWORD=$(openssl rand -hex 16)
JWT=$(openssl rand -hex 64)
MINIO_ROOT_USER=umbradev
MINIO_ROOT_PASSWORD=$(openssl rand -hex 16)
EOF
    fi
    cat >> "$DIR/.env" <<'EOF'
# Source des images : ghcr (publiées par GitHub Actions) ou local (./dev-container.sh build)
IMAGE_SOURCE=ghcr
IMAGE_REPO=ghcr.io/ashfall-codex
BRANCH=apple-arm-support
UMBRA_ROOT=/Volumes/UmbraDev
EOF
    chmod 600 "$DIR/.env"
  fi
  load_env
  mkdir -p "$APP_ROOT"
  container system stop 2>/dev/null || true
  container system start --app-root "$APP_ROOT"
  git -C "$REPO" submodule update --init -q
  echo "Préparation terminée. Vérifie PUBLIC_HOST dans $DIR/.env (nom Tailscale), puis ./dev-container.sh up"
}

build() {
  for p in UmbraSyncServer:server UmbraSyncAuthService:auth UmbraSyncStaticFilesServer:files; do
    echo "== Construction de umbra-dev/${p##*:}"
    container build -t "umbra-dev/${p##*:}" --build-arg "PROJECT=${p%%:*}" -f "$DIR/Dockerfile" "$REPO"
  done
}

down() {
  for n in nginx files auth server s3 redis postgres; do
    container stop "$P-$n" >/dev/null 2>&1 && echo "arrêté : $P-$n" || true
    container rm "$P-$n" >/dev/null 2>&1 || true
  done
}

schema() {  # schema <fichier.sql> : charge le schéma de prod (sans données) dans la base de dev
  local f="${1:-}"; [ -f "$f" ] || die "fichier SQL introuvable : $f"
  echo "== Recréation de la base mare à partir de $f"
  container exec "$P-postgres" psql -U mare -d postgres -q -c "DROP DATABASE IF EXISTS mare WITH (FORCE)" -c "CREATE DATABASE mare OWNER mare"
  container exec -i "$P-postgres" psql -U mare -d mare -q -v ON_ERROR_STOP=1 < "$f"
  container exec "$P-postgres" psql -U mare -d mare -Atc "SELECT count(*) || ' migrations, dernière : ' || max(migration_id) FROM _efmigrationshistory"
  echo "Schéma chargé. Relance les services : ./dev-container.sh apps"
}

status() {
  container ls --all | grep -E "^ID|$P-" || true
  echo "Image déployée : $(deployed_tag) ($IMAGE_SOURCE) · code : $(git -C "$REPO" rev-parse --short=7 HEAD) sur $(git -C "$REPO" branch --show-current)"
}

main() {
  local cmd="${1:-}"; [ $# -gt 0 ] && shift
  case "$cmd" in
    setup)           setup ;;
    up)              load_env; ensure_system; infra
                     [ "$IMAGE_SOURCE" = "ghcr" ] && { pull_all "$(deployed_tag)" || die "images $(deployed_tag) introuvables"; }
                     apps; status ;;
    apps)            load_env; apps ;;
    update)          load_env; update ;;
    deploy)          load_env; deploy "${1:-}" ;;
    rollback)        load_env; [ -f "$STATE/previous" ] || die "aucun déploiement précédent"; deploy "$(cat "$STATE/previous")" ;;
    build)           load_env; build ;;
    install-agent)   load_env; install_agent ;;
    uninstall-agent) uninstall_agent ;;
    down)            down ;;
    status)          load_env; status ;;
    logs)            [ -n "${1:-}" ] || die "précise un service : postgres, redis, s3, server, auth, files, nginx"
                     container logs -n 200 "$P-$1" ;;
    schema)          load_env; schema "${1:-}" ;;
    reset)
      read -r -p "Supprimer les conteneurs et toutes les données de dev ? [oui/non] " a
      [ "$a" = "oui" ] || exit 0
      down
      for v in pgdata redisdata s3data mcdf filescache; do container volume delete "$P-$v" 2>/dev/null || true; done
      echo "Données supprimées." ;;
    *) sed -n '6,17p' "$0"; exit 1 ;;
  esac
}

# Tout le script est lu avant de s'exécuter : un git merge qui le réécrit pendant update ne le casse pas.
main "$@"; exit $?
