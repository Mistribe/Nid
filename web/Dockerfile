# syntax=docker/dockerfile:1.7
# nid-web (1F1 design §2.2). Build context: the repository root (docker build -f web/Dockerfile .).
FROM node:24-alpine@sha256:ebfe2f90462722a7a4de65e91990e97fe0d401c70e0e762c5b53302f905ec1c1 AS build
WORKDIR /web
RUN corepack enable
COPY web/package.json web/pnpm-lock.yaml web/pnpm-workspace.yaml web/.npmrc ./
RUN --mount=type=cache,target=/root/.local/share/pnpm pnpm install --frozen-lockfile
COPY web/ ./
# The headers come from src/security-headers.ts, the one source (frontend/architecture.md §9); Node 24 strips its types.
RUN pnpm build && node scripts/caddy-headers.mjs > /headers.caddy

FROM caddy:2-alpine@sha256:6aeddd44c3078b0f9a35206472a11420648a79c184603ef95957d0a20044cb2b
# The image grants caddy cap_net_bind_service as a file capability; with cap_drop: ALL the kernel then refuses to run
# it. Port 8080 needs no capability, so remove it.
RUN apk add --no-cache libcap && setcap -r /usr/bin/caddy && apk del libcap
COPY web/Caddyfile /etc/caddy/Caddyfile
COPY --from=build /headers.caddy /etc/caddy/headers.caddy
COPY --from=build /web/dist /srv
# Caddy's data and config folders go to /tmp, the only writable place of a read-only container (a tmpfs in the compose).
ENV XDG_DATA_HOME=/tmp/caddy/data XDG_CONFIG_HOME=/tmp/caddy/config
USER 1000:1000
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=3s --start-period=20s CMD wget -q --spider http://127.0.0.1:8080/health/ready || exit 1
