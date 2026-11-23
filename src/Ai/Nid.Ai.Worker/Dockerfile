# The model worker (docs/ai/README.md §3): built from the repository root, like Nid's image.
#   docker build -f src/Ai/Nid.Ai.Worker/Dockerfile -t nid-worker:local .
# The same SDK as Nid's image; the runtime is Ubuntu's aspnet one, for the OCR tools (no ICU: the worker shows no localized text).
FROM mcr.microsoft.com/dotnet/sdk:11.0-resolute@sha256:65f3c7c6a25a239ab3e6719f2896147e7b68242ff2d8818dd70498bde6ef8bea AS build
ARG TARGETARCH
ARG VERSION=0.0.0
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props Nid.slnx ./
COPY src/Ai/Nid.Ai.Protocol/ src/Ai/Nid.Ai.Protocol/
COPY src/Ai/Nid.Ai.Worker/ src/Ai/Nid.Ai.Worker/
RUN --mount=type=cache,target=/root/.nuget/packages \
    RID=$([ "$TARGETARCH" = "arm64" ] && echo linux-arm64 || echo linux-x64) \
 && dotnet publish src/Ai/Nid.Ai.Worker/Nid.Ai.Worker.csproj -c Release -r "$RID" --self-contained false -o /app \
      -p:PublishReadyToRun=true -p:Version="$VERSION"
# The state volume: its keys, its certificate and the Nid key it pinned, readable by the worker's user only.
RUN mkdir -p /volumes/data && chown 1654:1654 /volumes/data && chmod 0700 /volumes/data

# Not the chiseled one: document text (#167) runs poppler (the text layer, a page as an image) and Tesseract (fra, eng).
FROM mcr.microsoft.com/dotnet/aspnet:11.0-resolute@sha256:bc0d6653979efba491ab3d6f589d691f96c309c3c3ef81325efa6978e7b7f242
RUN apt-get update \
 && apt-get install -y --no-install-recommends poppler-utils tesseract-ocr tesseract-ocr-eng tesseract-ocr-fra \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
COPY --from=build /volumes/ /
ENV Worker__DataPath=/data \
    Worker__Port=8443
USER 1654
EXPOSE 8443
VOLUME /data
HEALTHCHECK --interval=15s --timeout=5s --start-period=20s CMD ["dotnet", "Nid.Ai.Worker.dll", "healthcheck"]
ENTRYPOINT ["dotnet", "Nid.Ai.Worker.dll"]
