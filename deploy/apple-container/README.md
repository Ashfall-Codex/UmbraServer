# Pré-prod UmbraSync sur Mac Apple Silicon

Toute la pile UmbraSync dans [apple/container](https://github.com/apple/container) : une micro-VM
Linux arm64 par conteneur, sans Docker ni Compose. Aucune donnée ni aucun secret de prod.

| Conteneur | Rôle | Accès |
|---|---|---|
| umbra-nginx | point d'entrée, mêmes routes que la prod, plus le « CDN » `/umbrasync/` | `http://<PUBLIC_HOST>` (port 80) |
| umbra-server | hub SignalR (`/mare`, `/main`) | via nginx |
| umbra-auth | `/auth`, `/discovery`, `/.well-known` | via nginx |
| umbra-files | `/files`, `/cache`, `/request`, `/dist`, BC7 | via nginx |
| umbra-postgres | PostgreSQL 15, comme en prod | `127.0.0.1:5433`, sur le Mac seulement |
| umbra-redis | présence, sel de découverte | non exposé |
| umbra-s3 | SeaweedFS 4.48, remplace le bucket Scaleway | via nginx (`/umbrasync/`) ; `:8333` pour le débogage |

Les conteneurs se joignent par `umbra-<service>.test`. Les configurations sont dans `container/*.env`,
les secrets dans `.env` (non versionné).

## Cycle de mise à jour

```
push sur main
   └─ « Synchro main → apple-arm-support » : fusion par l'API GitHub (commit signé par GitHub)
        └─ « Images Apple Silicon » : linux/arm64 → ghcr.io/ashfall-codex/umbra-{server,auth,files}:sha-xxxxxxx
             └─ Mac (agent launchd, toutes les 2 min) : git fetch, attend les trois images du commit,
                avance la branche, recrée server, auth, files et nginx
```

Comptez 2 à 5 minutes entre le push et la pile à jour. La prod reste manuelle (`./deploy.sh` sur `main`) :
on pousse, on teste en pré-prod, puis on déploie.

- Un push direct sur `apple-arm-support` (outillage, essai qui n'ira pas dans `main`) suit le même chemin.
- En cas de conflit entre `main` et `apple-arm-support`, la synchro échoue (notification GitHub) et rien
  n'est poussé : fusionner à la main avec `git merge -S main`.
- Postgres, Redis et SeaweedFS ne sont jamais recréés par une mise à jour, et leurs volumes ne sont
  jamais touchés. Les migrations EF s'appliquent au démarrage de mare-server.
- Les deux workflows doivent exister sur `main` : GitHub n'accepte un `workflow_dispatch` que pour un
  workflow présent sur la branche par défaut.

## Installation sur le Mac

Prérequis : apple/container installé, macOS connecté en session graphique (connexion automatique
conseillée sur un Mac sans écran), volume APFS `/Volumes/UmbraDev`.

```
git clone -b apple-arm-support --recurse-submodules https://github.com/Ashfall-Codex/UmbraServer.git /Volumes/UmbraDev/src/UmbraServer
cd /Volumes/UmbraDev/src/UmbraServer/deploy/apple-container
./dev-container.sh setup           # DNS .test (sudo), service container, .env
nano .env                          # PUBLIC_HOST = nom par lequel les clients joignent le Mac
./dev-container.sh up
./dev-container.sh install-agent   # mise à jour automatique
```

`PUBLIC_HOST` doit être résolu par les clients (nom du réseau privé, par exemple MagicDNS) : c'est lui
que renvoie le well-known et qui sert aux liens de téléchargement. Un alias SSH ne suffit pas.

### Permission Réseau local (obligatoire)

Réglages système → Confidentialité et sécurité → Réseau local → activer `container-runtime-linux`.
Sans elle, les ports publiés acceptent la connexion puis la coupent (« Connection reset by peer »,
et `No route to host` dans `container system logs`). La demande n'apparaît qu'à l'écran : sur un Mac
piloté en SSH, passe par le Partage d'écran, puis `container system stop` et `start`.

### Images GHCR

Un paquet GHCR créé par GitHub Actions est privé au départ, et l'organisation peut interdire les paquets
publics (Settings → Packages → Package creation). Une fois autorisé, passer `umbra-server`, `umbra-auth`
et `umbra-files` en public (Package settings → Change visibility), ou connecter le Mac avec un jeton
`read:packages` : `container registry login ghcr.io`.

## Se connecter avec le plugin

Le plugin ne permet de changer de serveur qu'en mode dev (dev plugin Dalamud) : Réglages → Avancé →
Options avancées → ajouter `ws://<PUBLIC_HOST>`, le sélectionner, puis Compte → s'inscrire. La base de
pré-prod n'a pas les comptes de la prod. Repasser sur le serveur principal après le test.

## Tester sans le jeu

Le banc `UmbraProdTests` (hors dépôt, à côté de `UmbraClient`) joue deux clients complets :

```
dotnet run -c Release -- --server http://<PUBLIC_HOST>                  # tout le banc
dotnet run -c Release -- --server http://<PUBLIC_HOST> --only T07,T16   # fichiers et BC7
```

3 inscriptions par IP toutes les 10 minutes : une exécution en consomme 2. Les deadlocks éventuels
(T16) se lisent dans `./dev-container.sh logs postgres | grep -c "deadlock detected"`.

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
| `install-agent` / `uninstall-agent` | active / retire la mise à jour automatique |
| `down` / `reset` | arrête tout / supprime aussi les données |

Journal de la mise à jour automatique : `tail -f ~/Library/Logs/umbra-dev-update.log`.
Pour l'attendre : `until grep -q "OK : la pile" <(tail -1 ~/Library/Logs/umbra-dev-update.log); do sleep 5; done`.

## Pièges connus

- **SeaweedFS et la mémoire** : écrit en Go, il fait grimper son tas vers 700 Mio pendant un gros envoi.
  Dans une micro-VM sans swap, le noyau évince jusqu'au code du programme, qui relit alors le disque en
  boucle (CPU à 100 %, centaines de Gio lus, plus aucune commande ne répond). `GOMEMLIMIT=400MiB` dans
  une VM de 1 Gio règle le problème ; ne pas le retirer. Le compteur mémoire d'une VM inclut le cache
  disque : un chiffre proche du plafond n'est pas, seul, le signe d'une saturation.
- **Conteneur bloqué** : si `container stop` ne rend pas la main, `ps -ax | grep container-runtime-linux`
  donne le processus de la VM (`--uuid umbra-…`) ; `kill -9` sur ce PID, puis `./dev-container.sh up`.
  Le volume n'est pas touché.
- **Jeton Bearer et CDN** : le plugin envoie son jeton aussi aux liens CDN ; SeaweedFS le prend pour une
  signature S3 et répond 501. nginx retire l'en-tête sur `/umbrasync/`, comme Scaleway Edge en prod.
- **En-têtes nginx** : un `proxy_set_header` dans une `location` annule tous ceux du bloc `server`.
- **Port 8080** : souvent déjà pris sur un Mac de dev (OrbStack, par exemple) ; ne pas l'utiliser.

## Différences avec la prod (voulues)

- HTTP simple, pas de TLS ; ports TCP internes au lieu de sockets Unix.
- arm64 au lieu de x86 : la pré-prod valide le comportement, pas les performances.
- Purge des comptes désactivée ; version client attendue à 0.0.0.0 (aucun build refusé).
- Cache fichiers plafonné à 10 Gio ; SeaweedFS à la place de Scaleway ; pas de Discord ni de GeoIP.
- Les services tournent en root dans leur micro-VM.
- Pas de redémarrage automatique par apple/container : c'est l'agent launchd qui relance la pile
  après un redémarrage du Mac.
- Une VM ne rend pas sa mémoire à macOS : garde les plafonds `-m`.
