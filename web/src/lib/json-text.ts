/**
 * A JSON reader that keeps what `JSON.parse` loses: a number as it was written, and a member name repeated in one
 * object. The browser forwards entities to the server, which reads amounts as decimals from the text it receives
 * (import-format.md §2); a number that went through a double would reach it rounded. A repeated name is refused, as
 * RFC 8785 and the server's canonicalizer refuse it, instead of silently keeping the last value.
 */

/** A JSON number exactly as written. */
export class JsonNumber {
  constructor(readonly raw: string) {}
}

export type JsonObject = Map<string, JsonValue>;
export type JsonValue = null | boolean | string | JsonNumber | JsonValue[] | JsonObject;

export type JsonTextErrorCode = 'syntax' | 'duplicate_member' | 'too_deep';

export class JsonTextError extends Error {
  constructor(readonly code: JsonTextErrorCode, message: string, readonly line: number, readonly column: number) {
    super(`${message} (line ${line}, column ${column})`);
    this.name = 'JsonTextError';
  }
}

const NUMBER = /-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?/y;
const HEX4 = /^[0-9a-fA-F]{4}$/;
const ESCAPES: Readonly<Record<string, string>> = { '"': '"', '\\': '\\', '/': '/', b: '\b', f: '\f', n: '\n', r: '\r', t: '\t' };

/** Parses RFC 8259 JSON. `maxDepth` bounds nesting as the server does (import-format.md §11.5: 32 levels). */
export function parseJson(text: string, { maxDepth = Number.POSITIVE_INFINITY }: { maxDepth?: number } = {}): JsonValue {
  let at = 0;

  const error = (code: JsonTextErrorCode, message: string, position = at): JsonTextError => {
    const before = text.slice(0, position);
    return new JsonTextError(code, message, before.split('\n').length, position - before.lastIndexOf('\n'));
  };

  const skipSpace = () => {
    while (at < text.length) {
      const c = text.charCodeAt(at);
      if (c !== 0x20 && c !== 0x09 && c !== 0x0a && c !== 0x0d) return;
      at++;
    }
  };

  const literal = <T>(word: string, value: T): T => {
    if (!text.startsWith(word, at)) throw error('syntax', 'Expected a JSON value.');
    at += word.length;
    return value;
  };

  const readString = (): string => {
    at++; // the opening quote
    let out = '';
    let chunk = at;
    for (;;) {
      if (at >= text.length) throw error('syntax', 'Unterminated string.');
      const c = text.charCodeAt(at);
      if (c === 0x22) {
        out += text.slice(chunk, at);
        at++;
        return out;
      }
      if (c < 0x20) throw error('syntax', 'A control character must be escaped in a string.');
      if (c !== 0x5c) {
        at++;
        continue;
      }
      out += text.slice(chunk, at);
      const escape = text[at + 1];
      if (escape === 'u') {
        const hex = text.slice(at + 2, at + 6);
        if (!HEX4.test(hex)) throw error('syntax', 'Invalid \\u escape.');
        out += String.fromCharCode(Number.parseInt(hex, 16));
        at += 6;
      } else {
        const decoded = escape === undefined ? undefined : ESCAPES[escape];
        if (decoded === undefined) throw error('syntax', 'Invalid escape.');
        out += decoded;
        at += 2;
      }
      chunk = at;
    }
  };

  const enter = (depth: number) => {
    if (depth > maxDepth) throw error('too_deep', `Nesting deeper than ${maxDepth} levels.`);
    at++; // the opening bracket or brace
  };

  const readArray = (depth: number): JsonValue[] => {
    enter(depth);
    const items: JsonValue[] = [];
    skipSpace();
    if (text[at] === ']') {
      at++;
      return items;
    }
    for (;;) {
      items.push(readValue(depth));
      skipSpace();
      if (text[at] === ',') {
        at++;
        continue;
      }
      if (text[at] === ']') {
        at++;
        return items;
      }
      throw error('syntax', 'Expected , or ].');
    }
  };

  const readObject = (depth: number): JsonObject => {
    enter(depth);
    const members: JsonObject = new Map();
    skipSpace();
    if (text[at] === '}') {
      at++;
      return members;
    }
    for (;;) {
      skipSpace();
      if (text[at] !== '"') throw error('syntax', 'Expected a member name.');
      const start = at;
      const name = readString();
      if (members.has(name)) throw error('duplicate_member', `Member "${name}" appears twice in one object.`, start);
      skipSpace();
      if (text[at] !== ':') throw error('syntax', 'Expected :.');
      at++;
      members.set(name, readValue(depth));
      skipSpace();
      if (text[at] === ',') {
        at++;
        continue;
      }
      if (text[at] === '}') {
        at++;
        return members;
      }
      throw error('syntax', 'Expected , or }.');
    }
  };

  function readValue(depth: number): JsonValue {
    skipSpace();
    switch (text[at]) {
      case '{': return readObject(depth + 1);
      case '[': return readArray(depth + 1);
      case '"': return readString();
      case 't': return literal('true', true);
      case 'f': return literal('false', false);
      case 'n': return literal('null', null);
      default: {
        NUMBER.lastIndex = at;
        const match = NUMBER.exec(text);
        if (!match) throw error('syntax', 'Expected a JSON value.');
        at = NUMBER.lastIndex;
        return new JsonNumber(match[0]);
      }
    }
  }

  const value = readValue(0);
  skipSpace();
  if (at !== text.length) throw error('syntax', 'Unexpected text after the JSON value.');
  return value;
}

/** Writes a parsed value back: numbers exactly as read, members in their order, strings as `JSON.stringify` escapes them. */
export function serializeJson(value: JsonValue): string {
  if (value === null) return 'null';
  if (typeof value === 'boolean') return value ? 'true' : 'false';
  if (typeof value === 'string') return JSON.stringify(value);
  if (value instanceof JsonNumber) return value.raw;
  if (Array.isArray(value)) return `[${value.map((item) => serializeJson(item)).join(',')}]`;
  return `{${[...value].map(([name, member]) => `${JSON.stringify(name)}:${serializeJson(member)}`).join(',')}}`;
}
