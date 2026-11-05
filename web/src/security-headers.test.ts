import { describe, expect, it } from 'vitest';
import { contentSecurityPolicy, securityHeaders } from './security-headers';

describe('security headers', () => {
  it('is the reference policy of frontend/architecture.md §9, word for word', () =>
    expect(contentSecurityPolicy).toBe(
      "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; " +
      "font-src 'self'; connect-src 'self'; worker-src 'self'; frame-src 'self'; manifest-src 'self'; frame-ancestors 'none'; " +
      "base-uri 'none'; form-action 'self'; object-src 'none'"));

  it('sends the other headers with or without the CSP', () => {
    for (const headers of [securityHeaders(), securityHeaders({ csp: false })]) {
      expect(headers['Referrer-Policy']).toBe('no-referrer');
      expect(headers['X-Content-Type-Options']).toBe('nosniff');
      expect(headers['Permissions-Policy']).toBe('camera=(), microphone=(), geolocation=()');
      expect(headers['Cross-Origin-Opener-Policy']).toBe('same-origin');
    }
    expect(securityHeaders()['Content-Security-Policy']).toBe(contentSecurityPolicy);
    expect(securityHeaders({ csp: false })['Content-Security-Policy']).toBeUndefined();
  });
});
