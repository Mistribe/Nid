import { JsonNumber, parseJson, type JsonValue } from './json-text';

/**
 * The JSON Canonicalization Scheme (RFC 8785), as `Nid.Platform.Canonical.Rfc8785` implements it on the server:
 * members sorted by UTF-16 code units, minimal escaping, no whitespace, numbers as ECMAScript prints a double.
 * It identifies batches; it never computes with a value.
 */
export class CanonicalError extends Error {
  constructor(readonly code: 'lone_surrogate' | 'not_finite', message: string) {
    super(message);
    this.name = 'CanonicalError';
  }
}

// Code-unit regex (no `u` flag): a high surrogate not followed by a low one, or a low one not preceded by a high one.
const LONE_SURROGATE = /[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/;

/** RFC 8785 has no representation for a lone surrogate (UTF-8 cannot encode one); `TextEncoder` would replace it silently. */
export const hasLoneSurrogate = (text: string): boolean => LONE_SURROGATE.test(text);

/** ECMAScript `Number.prototype.toString` (RFC 8785 §3.2.2.3); `-0` prints as `0`. */
export function formatNumber(value: number): string {
  if (!Number.isFinite(value)) throw new CanonicalError('not_finite', 'NaN and infinities have no JSON representation.');
  return String(value);
}

// For a well-formed string, JSON.stringify escapes exactly what RFC 8785 §3.2.2.2 requires, and nothing else.
function writeString(text: string): string {
  if (hasLoneSurrogate(text)) throw new CanonicalError('lone_surrogate', 'A string holds a lone surrogate, which RFC 8785 cannot represent.');
  return JSON.stringify(text);
}

export function canonicalize(value: JsonValue): string {
  if (value === null) return 'null';
  if (typeof value === 'boolean') return value ? 'true' : 'false';
  if (typeof value === 'string') return writeString(value);
  // Through a double, as the server does (Rfc8785.cs: double.Parse of the raw text): both round correctly.
  if (value instanceof JsonNumber) return formatNumber(Number(value.raw));
  if (Array.isArray(value)) return `[${value.map((item) => canonicalize(item)).join(',')}]`;
  // The default sort compares UTF-16 code units, which RFC 8785 §3.2.3 requires (not localeCompare, not code points).
  const names = [...value.keys()].sort();
  return `{${names.map((name) => `${writeString(name)}:${canonicalize(value.get(name) ?? null)}`).join(',')}}`;
}

export const canonicalizeText = (text: string): string => canonicalize(parseJson(text));
