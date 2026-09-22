#!/usr/bin/env bash
# Runs the API on the host at :5080 with the development settings (Caddy proxies https://nid.localtest.me to it).
set -euo pipefail
cd "$(dirname "$0")/../../src/Nid.Host"
export ASPNETCORE_URLS=http://0.0.0.0:5080
export ConnectionStrings__nid='Host=localhost;Username=postgres;Password=dev;Database=nid'
export Auth__PublicUrl=https://nid.localtest.me
export Auth__Authority=https://auth.localtest.me
export Auth__ClientSecret=nid-dev-client-secret
export Auth__CsrfKey=dev-csrf-key-0123456789abcdef0123456789
export Auth__TrustAnyIdentityProviderCertificate=true
exec dotnet run
