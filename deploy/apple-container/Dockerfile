# Image d'un service UmbraSync (server, auth ou files) pour Mac Apple Silicon (linux/arm64).
# Contexte de build : la racine du dépôt, sous-module UmbraAPI initialisé.
# PROJECT = UmbraSyncServer | UmbraSyncAuthService | UmbraSyncStaticFilesServer
#
# L'étape de compilation tourne sur l'architecture de la machine qui construit (BUILDPLATFORM)
# et produit des binaires pour l'architecture cible (TARGETARCH). Sur un runner GitHub x64,
# on obtient donc une image arm64 sans émulation ; sur le Mac, tout reste natif.

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT
ARG TARGETARCH
RUN test -n "$PROJECT" || (echo "build arg PROJECT manquant" >&2 && exit 1)
WORKDIR /src
COPY . .
RUN arch="$TARGETARCH"; [ "$arch" = "amd64" ] && arch=x64; \
    dotnet publish "UmbraSyncServer/${PROJECT}/${PROJECT}.csproj" \
      -c Release -a "${arch:-arm64}" -o /out --no-self-contained \
      -p:UseAppHost=false -p:TreatWarningsAsErrors=false

# Aucune instruction RUN ici : l'étape finale ne s'exécute jamais, pas besoin d'émuler arm64.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG PROJECT
ENV UMBRA_DLL=${PROJECT}.dll \
    DOTNET_gcServer=1 \
    ASPNETCORE_ENVIRONMENT=Dev
WORKDIR /app
COPY --from=build /out .
# Root dans la micro-VM : les volumes montés par apple/container y appartiennent à root.
# En prod, les services tournent sous l'utilisateur mare : ne pas reprendre ce choix tel quel.
ENTRYPOINT ["sh", "-c", "exec dotnet /app/$UMBRA_DLL"]
