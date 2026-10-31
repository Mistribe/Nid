// frontend/architecture.md §9. One source for every server of the SPA: vite preview now, the nid image's host in 1F.
export const contentSecurityPolicy = [
  "default-src 'none'",
  "script-src 'self'",
  "style-src 'self' 'unsafe-inline'", // Radix and charts position with inline styles
  "img-src 'self' data: blob:",
  "font-src 'self'",
  "connect-src 'self'",
  "worker-src 'self'",
  "manifest-src 'self'",
  "frame-ancestors 'none'",
  "base-uri 'none'",
  "form-action 'self'",
  "object-src 'none'",
].join('; ');

/** `csp: false` for the dev server only: React Refresh injects an inline script the policy refuses. */
export function securityHeaders({ csp = true }: { csp?: boolean } = {}): Record<string, string> {
  return {
    ...(csp ? { 'Content-Security-Policy': contentSecurityPolicy } : {}),
    'Referrer-Policy': 'no-referrer',
    'X-Content-Type-Options': 'nosniff',
    'Permissions-Policy': 'camera=(), microphone=(), geolocation=()',
    'Cross-Origin-Opener-Policy': 'same-origin',
  };
}
