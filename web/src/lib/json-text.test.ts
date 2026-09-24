import { describe, expect, it } from 'vitest';
import { JsonNumber, JsonTextError, parseJson, serializeJson } from './json-text';

const failure = (text: string, maxDepth?: number): JsonTextError => {
  try {
    parseJson(text, maxDepth === undefined ? {} : { maxDepth });
  } catch (error) {
    if (error instanceof JsonTextError) return error;
    throw error;
  }
  throw new Error(`parsed: ${text}`);
};

describe('parseJson', () => {
  it('keeps every number exactly as written', () => {
    const value = parseJson('[17.20, 1E3, -0, 12345678901234567.89]');
    expect(value).toEqual([new JsonNumber('17.20'), new JsonNumber('1E3'), new JsonNumber('-0'), new JsonNumber('12345678901234567.89')]);
  });

  it('reads objects as maps in their order, so no key reaches a prototype', () => {
    const value = parseJson('{"b":1,"__proto__":{"x":true},"a":null}');
    expect(value).toBeInstanceOf(Map);
    expect([...(value as Map<string, unknown>).keys()]).toEqual(['b', '__proto__', 'a']);
    expect(({} as Record<string, unknown>).x).toBeUndefined();
  });

  it('decodes escapes, surrogate pairs included, and lets a lone surrogate through for the canonicalizer to refuse', () => {
    expect(parseJson(String.raw`"\"\\\/\b\f\n\r\té😀"`)).toBe('"\\/\b\f\n\r\té😀');
    expect(parseJson(String.raw`"\ud800"`)).toBe('\ud800');
  });

  it('refuses a member name repeated in one object, at the repetition', () => {
    expect(failure('{"a":1,"a":2}')).toMatchObject({ code: 'duplicate_member', line: 1, column: 8 });
  });

  it('accepts the same name in two different objects', () => {
    expect(() => parseJson('[{"a":1},{"a":2}]')).not.toThrow();
  });

  it('reports a syntax error with its line and column', () => {
    expect(failure('{\n  "a": tru\n}')).toMatchObject({ code: 'syntax', line: 2, column: 8 });
  });

  it.each([
    ['', 'empty text'],
    ['[1,]', 'trailing comma'],
    ['[01]', 'leading zero'],
    ['[1.]', 'dot without digits'],
    ['["a\u0001"]', 'raw control character'],
    ['["\\x"]', 'unknown escape'],
    ['["\\u12"]', 'short unicode escape'],
    ['{"a" 1}', 'missing colon'],
    ['[1] 2', 'text after the value'],
    ['﻿[]', 'byte order mark left in the text'],
    ["{'a':1}", 'single quotes'],
  ])('refuses %j (%s)', (text) => {
    expect(failure(text).code).toBe('syntax');
  });

  it('bounds nesting depth: 32 levels are read, 33 are refused', () => {
    expect(() => parseJson(`${'['.repeat(32)}${']'.repeat(32)}`, { maxDepth: 32 })).not.toThrow();
    expect(failure(`${'['.repeat(33)}${']'.repeat(33)}`, 32).code).toBe('too_deep');
    expect(failure(`{"a":${'{"b":'.repeat(32)}1${'}'.repeat(33)}`, 32).code).toBe('too_deep');
  });
});

describe('serializeJson', () => {
  it('writes the value back with its numbers and member order, without whitespace', () => {
    const text = ' { "b" : [ 1.50 , "é\\n" ] , "a" : null , "c" : true } ';
    expect(serializeJson(parseJson(text))).toBe('{"b":[1.50,"é\\n"],"a":null,"c":true}');
  });

  it('round-trips what it writes', () => {
    const once = serializeJson(parseJson('[{"x":-0.0,"y":"\\u2028"},[],{}]'));
    expect(serializeJson(parseJson(once))).toBe(once);
  });
});
