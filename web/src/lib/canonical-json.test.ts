import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { CanonicalError, canonicalizeText, formatNumber } from './canonical-json';
import { sha256Hex } from './hash';
import { JsonTextError } from './json-text';

/**
 * The vectors the C# canonicalizer passes (tests/vectors/rfc8785/SOURCE.md): the same files test both languages.
 * Built with `node:path` rather than `new URL(template, import.meta.url)`: Vite statically rewrites that pattern
 * into a glob asset import over the containing directory, which the dev server's default fs allow-list (scoped to
 * `web/`) then denies because the vectors live at the repo root, outside `web/`.
 */
const vectorsRoot = join(dirname(fileURLToPath(import.meta.url)), '../../../tests/vectors/rfc8785');
const vectors = (path: string) => join(vectorsRoot, path);

const codeOf = (run: () => unknown): string => {
  try {
    run();
  } catch (error) {
    if (error instanceof CanonicalError || error instanceof JsonTextError) return error.code;
    throw error;
  }
  return 'accepted';
};

describe('the official vectors (cyberphone/json-canonicalization)', () => {
  for (const name of readdirSync(vectors('official/input')).filter((n) => n.endsWith('.json')).sort()) {
    it(`reproduces ${name} byte for byte`, () => {
      const canonical = canonicalizeText(readFileSync(vectors(`official/input/${name}`), 'utf8'));
      // Array.from, not a direct Uint8Array-to-Uint8Array toEqual: under the `jsdom` test environment,
      // `TextEncoder().encode()` returns an instance from a different realm than this module's `Uint8Array`
      // identifier, so two byte-identical typed arrays compare unequal ("no visual difference", still failing).
      // Plain arrays carry no such realm identity, so the same byte comparison is realm-safe.
      expect(Array.from(new TextEncoder().encode(canonical))).toEqual(Array.from(readFileSync(vectors(`official/output/${name}`))));
    });
  }
});

describe('RFC 8785 Appendix B, number serialization', () => {
  const lines = readFileSync(vectors('numbers.txt'), 'utf8').split('\n').filter((line) => line.length > 0);

  it('covers the whole appendix', () => {
    expect(lines.length).toBe(26);
  });

  it.each(lines.map((line) => line.split(',') as [string, string]))('0x%s is %s', (hex, expected) => {
    const view = new DataView(new ArrayBuffer(8));
    view.setBigUint64(0, BigInt(`0x${hex}`));
    const value = view.getFloat64(0);
    if (expected === 'error') expect(() => formatNumber(value)).toThrow(CanonicalError);
    else expect(formatNumber(value)).toBe(expected);
  });
});

describe('the cross-implementation corpus', () => {
  type Entry = { input: string; canonical: string; sha256: string };
  const corpus = JSON.parse(readFileSync(vectors('corpus.json'), 'utf8')) as Entry[];

  it('holds its 405 entries', () => {
    expect(corpus.length).toBe(405);
  });

  it('reproduces every canonical form and hash of the C# canonicalizer', async () => {
    const mismatches: string[] = [];
    for (const [i, entry] of corpus.entries()) {
      const canonical = canonicalizeText(entry.input);
      if (canonical !== entry.canonical) mismatches.push(`#${i} canonical: ${JSON.stringify(canonical)} ≠ ${JSON.stringify(entry.canonical)}`);
      else if ((await sha256Hex(canonical)) !== entry.sha256) mismatches.push(`#${i} sha256`);
    }
    expect(mismatches).toEqual([]);
  });
});

describe('canonicalizeText', () => {
  it('escapes only what RFC 8785 requires', () => {
    expect(canonicalizeText(String.raw`["\u0001\b\t\n\f\r\"\\\/\u007f é"]`)).toBe('["\\u0001\\b\\t\\n\\f\\r\\"\\\\/\u007f é"]');
  });

  it('sorts members by UTF-16 code units, not by code points', () => {
    expect(canonicalizeText(String.raw`{"￿":2,"😀":1}`)).toBe('{"😀":1,"￿":2}');
  });

  it('formats numbers as ECMAScript does', () => {
    expect(canonicalizeText('[17.20, 1E21, 1e-7, -0, 0.000001, 333333333.33333329]')).toBe('[17.2,1e+21,1e-7,0,0.000001,333333333.3333333]');
  });

  it('refuses a repeated member name, a lone surrogate in a value or a name, and a number beyond the double range', () => {
    expect(codeOf(() => canonicalizeText('{"a":1,"a":2}'))).toBe('duplicate_member');
    expect(codeOf(() => canonicalizeText(String.raw`["\ud800"]`))).toBe('lone_surrogate');
    expect(codeOf(() => canonicalizeText(String.raw`{"\udc00":1}`))).toBe('lone_surrogate');
    expect(codeOf(() => canonicalizeText('[1e400]'))).toBe('not_finite');
  });

  it('hashes an amount written as a number like its canonical form', async () => {
    const written = canonicalizeText('{ "amount" : 12.990, "currency": "EUR" }');
    expect(written).toBe('{"amount":12.99,"currency":"EUR"}');
    expect(await sha256Hex(written)).toMatch(/^[0-9a-f]{64}$/);
  });
});
