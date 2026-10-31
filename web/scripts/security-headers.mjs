// frontend/architecture.md §9: the security headers have one source, src/security-headers.ts. This turns it into the
// JSON file Nid.Host loads at startup to answer the SPA with them, at image build (single-image design §3.3).
import { pathToFileURL } from 'node:url';

export function securityHeadersJson(headers) {
  return `${JSON.stringify(headers, null, 2)}\n`;
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  const { securityHeaders } = await import('../src/security-headers.ts');
  process.stdout.write(securityHeadersJson(securityHeaders()));
}
