import { describe, expect, it } from 'vitest';
import { securityHeaders } from '../src/security-headers.ts';
import { securityHeadersJson } from './security-headers.mjs';

describe('securityHeadersJson', () => {
  it('writes every security header of the one source, CSP included', () => {
    const headers = JSON.parse(securityHeadersJson(securityHeaders()));
    expect(headers).toEqual(securityHeaders());
    expect(headers['Content-Security-Policy']).toMatch(/^default-src 'none'; /);
  });

  it('keeps a double quote intact', () =>
    expect(JSON.parse(securityHeadersJson({ X: 'a"b' }))).toEqual({ X: 'a"b' }));
});
