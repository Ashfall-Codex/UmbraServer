# Environnement de dev UmbraSync sur Mac Apple Silicon

Toute la pile UmbraSync dans [apple/container](https://github.com/apple/container) : une micro-VM
Linux arm64 par conteneur, sans Docker ni Compose. Aucune donnée ni aucun secret de prod.

| Conteneur | Rôle | Accès |
|---|---|---|
| umbra-nginx | point d'entrée, mêmes routes que la prod | `http://<PUBLIC_HOST>` (port 80) |
| umbra-server | hub SignalR (`/mare`, `/main`) | via nginx |
| umbra-auth | `/auth`, `/discovery`, `/.well-known` | via nginx |
| umbra-files | `/files`, `/cache`, `/request`, `/dist`, BC7 | via nginx |
| umbra-postgres | PostgreSQL 15, comme en prod | `127.0.0.1:5433`, sur le Mac seulement |
| umbra-redis | présence, sel de découverte | non exposé |
| umbra-s3 | SeaweedFS, remplace le bucket Scaleway et son CDN | `http://<PUBLIC_HOST>:8333/umbrasync` |

Les conteneurs se joignent par `umbra-<service>.test`. Les configurations sont dans `container/*.env`,
les secrets dans `.env` (non versionné).

## Cycle de mise à jour

```
Adhara : git push origin apple-arm-support
   └─ GitHub Actions : images linux/arm64 → ghcr.io/ashfall-codex/umbra-{server,auth,files}:sha-xxxxxxx
        └─ Atrama (launchd, toutes les 2 min) : git fetch, attend les trois images du commit,
           avance la branche, recrée server/auth/files/nginx
```

Postgres, Redis et SeaweedFS ne sont jamais recréés par une mise à jour, et leurs volumes ne sont
jamais touchés. Les migrations EF s'appliquent au démarrage de mare-server.
Seuls les pushs sur `apple-arm-support` produisent des images ; `main` et les autres branches, rien.

Garde la branche à jour avec `main` (`git merge main`), sinon Atrama teste du code ancien.

## Installation sur le Mac

Prérequis : apple/container installé, macOS connecté en session graphique (connexion automatique
conseillée sur un Mac mini sans écran), volume APFS `/Volumes/UmbraDev`.

```
git clone -b apple-arm-support --recurse-submodules https://github.com/Ashfall-Codex/UmbraServer.git /Volumes/UmbraDev/src/UmbraServer
cd /Volumes/UmbraDev/src/UmbraServer/deploy/apple-container
./dev-container.sh setup           # DNS .test (sudo), service container, .env
nano .env                          # PUBLIC_HOST = nom Tailscale du Mac
./dev-container.sh up
./dev-container.sh install-agent   # mise à jour automatique
```

### Permission Réseau local (obligatoire)

Réglages système → Confidentialité et sécurité → Réseau local → activer `container-runtime-linux`.
Sans elle, les ports publiés acceptent la connexion puis la coupent (« Connection reset by peer »,
et `No route to host` dans `container system logs`). La demande n'apparaît qu'à l'écran : sur un Mac
piloté en SSH, passe par le Partage d'écran, puis `container system stop` et `start`.

### Images GHCR

Un paquet GHCR créé par GitHub Actions est privé au départ. Après le premier build, passe les trois
paquets `umbra-server`, `umbra-auth` et `umbra-files` en public (Package settings → Change visibility),
ou connecte le Mac avec un jeton `read:packages` : `container registry login ghcr.io`.

## Commandes

| Commande | Effet |
|---|---|
| `up` | démarre l'infra si besoin, puis recrée les services UmbraSync |
| `apps` | recrée seulement server, auth, files et nginx |
| `status` | conteneurs, tag d'image déployé, commit |
| `logs <service>` | 200 dernières lignes |
| `deploy sha-xxxxxxx` | déploie un commit précis |
| `rollback` | revient au déploiement précédent |
| `build` + `IMAGE_SOURCE=local` | compile sur le Mac au lieu de GHCR |
| `schema <fichier.sql>` | charge un schéma (dump de prod sans données) |
| `down` / `reset` | arrête tout / supprime aussi les données |

Journal de la mise à jour automatique : `tail -f ~/Library/Logs/umbra-dev-update.log`.

## Différences avec la prod (voulues)

- HTTP simple, pas de TLS ; ports TCP internes au lieu de sockets Unix.
- Purge des comptes désactivée ; version client attendue à 0.0.0.0 (aucun build refusé).
- Cache fichiers plafonné à 10 Gio ; SeaweedFS à la place de Scaleway ; pas de Discord ni de GeoIP.
- Les services tournent en root dans leur micro-VM.
- Pas de redémarrage automatique par apple/container : c'est l'agent launchd qui relance la pile
  après un redémarrage du Mac.
- Une VM ne rend pas sa mémoire à macOS : garde les plafonds `-m`.
- Port 8080 occupé par OrbStack sur Atrama : ne pas l'utiliser.
