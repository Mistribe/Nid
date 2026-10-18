# syntax=docker/dockerfile:1.7
# nid-api (1F1 design §2.1). Build context: the repository root (docker build -f src/Nid.Host/Dockerfile .).
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS build
ARG TARGETARCH
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props Nid.slnx ./
COPY src/ src/
# Publishing for the platform's RID copies libSkiaSharp.so (1C2c); the OpenAPI document is the developer's, not the image's.
RUN --mount=type=cache,target=/root/.nuget/packages \
    RID=$([ "$TARGETARCH" = "arm64" ] && echo linux-arm64 || echo linux-x64) \
 && dotnet publish src/Nid.Host/Nid.Host.csproj -c Release -r "$RID" --self-contained false -o /app \
      -p:OpenApiGenerateDocumentsOnBuild=false \
 && test -f /app/libSkiaSharp.so
# 1F4: `nid admin …` in the container; the chiseled image has no shell to make this link. Next to the app host: it
# looks for Nid.Host.dll beside the path it was called by, not beside the file a link points to.
RUN ln -s Nid.Host /app/nid

# The folders the named volumes start from: a fresh volume copies their owner and mode (1F1 design §2.1). They are
# copied whole, as /volumes/var/lib/nid, so each folder keeps its own mode (a COPY of a folder's contents would not).
RUN mkdir -p /volumes/var/lib/nid/keys /volumes/var/lib/nid/assets \
 && chown -R 1654:1654 /volumes/var/lib/nid && chmod 0700 /volumes/var/lib/nid/keys

# -extra: ICU and time zone data. Globalization is not invariant (Directory.Build.props), so .NET needs ICU;
# glibc, libstdc++ and libgcc_s, all SkiaSharp needs, are in every chiseled variant.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:3ed6558056b54d5c9dfefb7ff473728e022ff2bf1d976cbf8ee07b05ec594374
WORKDIR /app
COPY --from=build /app ./
COPY --from=build /volumes/ /
ENV PATH="/app:${PATH}" \
    ASPNETCORE_HTTP_PORTS=8080 \
    Assets__Root=/var/lib/nid/assets \
    DataProtection__KeysPath=/var/lib/nid/keys
USER 1654
EXPOSE 8080
ENTRYPOINT ["dotnet", "Nid.Host.dll"]
