var builder = DistributedApplication.CreateBuilder(args);

// Throwaway development secrets (nothing here is a real secret). Overridable through user-secrets.
var csrfKey = builder.AddParameter("csrf-key", "dev-csrf-key-0123456789abcdef0123456789", secret: true);
// Fixed, or the generated password would change on every run while WithDataVolume keeps the password
// baked in at initdb, and PostgreSQL would refuse the AppHost from the second run on.
var postgresPassword = builder.AddParameter("postgres-password", "nid-dev-postgres-password", secret: true);

var postgres = builder.AddPostgres("postgres", password: postgresPassword)
    .WithDataVolume("nid-dev-pgdata");
var nidDb = postgres.AddDatabase("nid");

// The identity provider. Its API key is a throwaway one: the bootstrap below and the end-to-end journeys use it to create
// users and to sign them in (deploy/local/bootstrap-pocket-id.mjs, web/e2e/session.ts). Port 1411 is published for them.
var pocketId = builder.AddContainer("pocket-id", "ghcr.io/pocket-id/pocket-id", "v2.18.0")
    .WithVolume("nid-dev-pocket-id", "/app/data")
    .WithEnvironment("APP_URL", "https://auth.localtest.me")
    .WithEnvironment("ENCRYPTION_KEY", "dev-pocket-id-encryption-key-0123456789")
    .WithEnvironment("STATIC_API_KEY", "nid-local-smoke-api-key-0123456789abcdef")
    .WithEnvironment("TRUST_PROXY", "true")
    .WithEnvironment("ANALYTICS_DISABLED", "true")
    .WithEnvironment("VERSION_CHECK_DISABLED", "true")
    .WithHttpEndpoint(port: 1411, targetPort: 1411, name: "http", isProxied: false);

// Pocket ID chooses Nid's client secret and shows it once: this one-shot creates the groups, the users (alice, bob, carol)
// and the client, then writes the secret where nid-api reads its key-per-file secrets (Secrets:Path, HostingSetup).
var secretsPath = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", ".data", "secrets"));
var bootstrap = builder.AddExecutable("pocket-id-bootstrap", "node", Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "deploy", "local")), "bootstrap-pocket-id.mjs")
    .WithEnvironment("POCKET_ID_URL", "http://localhost:1411")
    .WithEnvironment("POCKET_ID_API_KEY", "nid-local-smoke-api-key-0123456789abcdef")
    .WithEnvironment("NID_URL", "https://nid.localtest.me")
    .WithEnvironment("NID_CLIENT_SECRET_FILE", Path.Combine(secretsPath, "Auth__ClientSecret"))
    .WaitFor(pocketId);

// The mail catcher: nid-api sends its notification emails to it (SMTP 1025) and the end-to-end journeys read them from its API
// (web/e2e/notifications-email.spec.ts, port 8025, also its web UI). Nothing leaves the machine. The tag is a release at least two weeks old.
builder.AddContainer("mailpit", "axllent/mailpit", "v1.31.4")
    .WithEndpoint(port: 1025, targetPort: 1025, name: "smtp", isProxied: false)
    .WithHttpEndpoint(port: 8025, targetPort: 8025, name: "http", isProxied: false);

var api = builder.AddProject<Projects.Nid_Host>("nid-api")
    .WithHttpEndpoint(port: 5080, name: "http", isProxied: false)
    .WithReference(nidDb)                       // injects ConnectionStrings__nid
    .WaitFor(nidDb)
    .WithEnvironment("Auth__PublicUrl", "https://nid.localtest.me")
    .WithEnvironment("Auth__Authority", "https://auth.localtest.me")
    .WithEnvironment("Secrets__Path", secretsPath)
    .WithEnvironment("Auth__CsrfKey", csrfKey)
    .WithEnvironment("Auth__TrustAnyIdentityProviderCertificate", "true")
    // 1C2c: the asset store of development, git-ignored; production mounts a volume at /var/lib/nid/assets (1F).
    .WithEnvironment("Assets__Root", Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", ".data", "assets")))
    .WithEnvironment("Documents__Storage__Path", Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", ".data", "documents")))
    // Notifications: a count requests a refresh at most every 5 s here (900 s in production), so the end-to-end journey sees its data evaluated.
    .WithEnvironment("Notifications__RefreshThrottleSeconds", "5")
    // Email through the mail catcher (nid-api runs on the host: the container's published port is on localhost).
    .WithEnvironment("Nid__Smtp__Host", "localhost")
    .WithEnvironment("Nid__Smtp__Port", "1025")
    .WithEnvironment("Nid__Smtp__From", "nid@localtest.me")
    .WithEnvironment("Nid__Smtp__Security", "None")
    // Aspire derives ASPNETCORE_URLS=http://localhost:5080 from the endpoint above, which binds loopback
    // only and refuses Caddy's host.docker.internal connection. Bind every interface instead.
    .WithEnvironment("ASPNETCORE_URLS", "http://0.0.0.0:5080")
    .WaitForCompletion(bootstrap);

// The SPA is started by Vite on port 5173; Caddy's catch-all route forwards everything outside
// /api, /auth, /mcp and /health there.
var webPath = Path.Combine(builder.AppHostDirectory, "..", "..", "web");
var web = Directory.Exists(webPath)
    ? builder.AddViteApp("nid-web", webPath).WithPnpm().WithHttpEndpoint(port: 5173, env: "PORT", isProxied: false)
        // Bare Vite has no /auth or /api proxy: the app only works through Caddy, so keep this link out of the grid.
        .WithUrlForEndpoint("http", url => url.DisplayLocation = UrlDisplayLocation.DetailsOnly)
    : null;

// TLS reverse proxy, single origin. Reaches the host processes through host.docker.internal.
var caddy = builder.AddContainer("caddy", "caddy", "2")
    .WithBindMount("../../../deploy/dev/caddy/Caddyfile", "/etc/caddy/Caddyfile", isReadOnly: true)
    .WithContainerRuntimeArgs("--add-host=host.docker.internal:host-gateway")
    .WithEndpoint(port: 443, targetPort: 443, name: "https", scheme: "https", isProxied: false)
    // The public origin (Pocket ID's passkeys need its own host name), not https://localhost:443.
    .WithUrlForEndpoint("https", url => { url.Url = "https://nid.localtest.me"; url.DisplayText = "Nid"; })
    .WaitFor(api).WaitFor(pocketId);
if (web is not null) caddy.WaitFor(web);

builder.Build().Run();
