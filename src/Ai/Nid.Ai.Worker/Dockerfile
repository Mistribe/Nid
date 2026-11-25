# The model worker (docs/ai/README.md §3): built from the repository root, like Nid's image.
#   docker build -f src/Ai/Nid.Ai.Worker/Dockerfile -t nid-worker:local .
# The same SDK as Nid's image; the runtime is Ubuntu's aspnet one, for the OCR tools (no ICU: the worker shows no localized text).
FROM mcr.microsoft.com/dotnet/sdk:11.0-resolute@sha256:2794448a07118a6eaf00f29ab5d2bf44928517ce5ff86505a63a69230374f599 AS build
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
FROM mcr.microsoft.com/dotnet/aspnet:11.0-resolute@sha256:ca396ece3f2a2e62c7a03f1d7d710b8bc7e8c9dea5597d091178d9f5bfca13f0
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
