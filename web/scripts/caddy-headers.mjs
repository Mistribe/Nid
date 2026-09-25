// frontend/architecture.md §9: the security headers have one source, src/security-headers.ts. This turns it into the
// header directives of the nid-web image's Caddyfile, at image build (1F1 design §2.2).
import { pathToFileURL } from 'node:url';

export function caddyHeaders(headers) {
  return Object.entries(headers).map(([name, value]) => `header ${name} "${value.replaceAll('"', '\\"')}"\n`).join('');
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  const { securityHeaders } = await import('../src/security-headers.ts');
  process.stdout.write(caddyHeaders(securityHeaders()));
}
