# syntax=docker/dockerfile:1.28@sha256:bb22d9815c728170f72750f4e5b0d672e06176142e1d602c7e66c050100b7e5b
# nid: the backend and the web app in one image (single-image design §2). Build context: the repository root (docker build -f src/Hosting/Nid.Host/Dockerfile .).
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
ARG TARGETARCH
# The semver of the release (CI passes it); 0.0.0 for a local build.
ARG VERSION=0.0.0
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props Nid.slnx ./
COPY src/ src/
# Publishing for the platform's RID copies libSkiaSharp.so (1C2c); the OpenAPI document is the developer's, not the image's.
# ReadyToRun: the assemblies ship precompiled for that RID, so the host starts with little left to JIT.
RUN --mount=type=cache,target=/root/.nuget/packages \
    RID=$([ "$TARGETARCH" = "arm64" ] && echo linux-arm64 || echo linux-x64) \
 && dotnet publish src/Hosting/Nid.Host/Nid.Host.csproj -c Release -r "$RID" --self-contained false -o /app \
      -p:PublishReadyToRun=true -p:OpenApiGenerateDocumentsOnBuild=false -p:Version="$VERSION" \
 && test -f /app/libSkiaSharp.so
# 1F4: `nid admin …` in the container; the chiseled image has no shell to make this link. Next to the app host: it
# looks for Nid.Host.dll beside the path it was called by, not beside the file a link points to.
RUN ln -s Nid.Host /app/nid

# The folders the named volumes start from: a fresh volume copies their owner and mode (1F1 design §2.1). They are
# copied whole, as /volumes/var/lib/nid, so each folder keeps its own mode (a COPY of a folder's contents would not).
RUN mkdir -p /volumes/var/lib/nid/keys /volumes/var/lib/nid/assets /volumes/var/lib/nid/documents /volumes/var/lib/nid/logs \
 && chown -R 1654:1654 /volumes/var/lib/nid && chmod 0700 /volumes/var/lib/nid/keys /volumes/var/lib/nid/logs && chmod 0750 /volumes/var/lib/nid/documents

# The web app: its build and the security headers file the host answers the SPA with (frontend/architecture.md §9: one source).
FROM node:26-alpine@sha256:8996bb5821d62a8fd9bb27c1cb07462f3fdce6e47d640c6a881ead0b064ce216 AS web
WORKDIR /web
# Node 25+ no longer ships corepack: install it, it then runs the pnpm of package.json's packageManager. npm ci from a lockfile
# pins its tarball by hash (corepack/package-lock.json, kept current by Renovate).
COPY src/Hosting/Nid.Host/corepack/ /opt/corepack/
RUN npm ci --prefix /opt/corepack --ignore-scripts && /opt/corepack/node_modules/.bin/corepack enable --install-directory /usr/local/bin
COPY web/package.json web/pnpm-lock.yaml web/pnpm-workspace.yaml web/.npmrc ./
RUN --mount=type=cache,target=/root/.local/share/pnpm pnpm install --frozen-lockfile
COPY web/ ./
# The release version, shown in the sidebar (set by the images job of ci.yml).
ARG VERSION=0.0.0
ENV VITE_NID_VERSION=$VERSION
# Node 24 strips the types of src/security-headers.ts.
RUN pnpm build && node scripts/security-headers.mjs > /security-headers.json

# -extra: ICU and time zone data. Globalization is not invariant (Directory.Build.props), so .NET needs ICU;
# glibc, libstdc++ and libgcc_s, all SkiaSharp needs, are in every chiseled variant.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:cae5b962bfb786606def03d250c6a645cc98839095c89e806e4bd8978a68115b
WORKDIR /app
COPY --from=build /app ./
# The SPA the host serves (Web:Root) and its headers (Web:SecurityHeaders), both under the content root.
COPY --from=web /web/dist ./wwwroot
COPY --from=web /security-headers.json ./security-headers.json
COPY --from=build /volumes/ /
ENV PATH="/app:${PATH}" \
    ASPNETCORE_HTTP_PORTS=8080 \
    Assets__Root=/var/lib/nid/assets \
    DataProtection__KeysPath=/var/lib/nid/keys
USER 1654
EXPOSE 8080
# The chiseled image has no shell or wget: the host asks itself (nid healthcheck, Program.cs).
HEALTHCHECK --interval=15s --timeout=5s --start-period=30s CMD ["dotnet", "Nid.Host.dll", "healthcheck"]
ENTRYPOINT ["dotnet", "Nid.Host.dll"]
