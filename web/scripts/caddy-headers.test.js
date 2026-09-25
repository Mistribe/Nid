import { describe, expect, it } from 'vitest';
import { securityHeaders } from '../src/security-headers.ts';
import { caddyHeaders } from './caddy-headers.mjs';

describe('caddyHeaders', () => {
  it('writes every security header of the one source, CSP included', () => {
    const lines = caddyHeaders(securityHeaders()).trim().split('\n');
    expect(lines).toHaveLength(Object.keys(securityHeaders()).length);
    expect(lines[0]).toMatch(/^header Content-Security-Policy "default-src 'none'; /);
  });

  it('escapes a double quote rather than breaking the directive', () =>
    expect(caddyHeaders({ X: 'a"b' })).toBe('header X "a\\"b"\n'));
});
