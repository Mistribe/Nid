import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { changelog, problems } from './changelog.mjs';

// The tests run from web/, under jsdom: import.meta.url is no file URL there.
const dir = join(process.cwd(), 'changelog');

describe('the change files', () => {
  it.each(readdirSync(dir).filter((name) => name.endsWith('.json')))('%s has one line per language', (name) =>
    expect(problems(JSON.parse(readFileSync(join(dir, name), 'utf8')))).toEqual([]));

  it('holds only change files', () => expect(readdirSync(dir).filter((name) => !name.endsWith('.json'))).toEqual(['README.md']));
});

describe('problems', () => {
  it('asks for every language', () => expect(problems({ en: 'Right.' })).toEqual(['« fr » is not one line of text']));

  it('refuses a language the app does not know', () => expect(problems({ en: 'a', fr: 'b', de: 'c' })).toEqual(['unknown language « de »']));

  it('takes a release\'s version, as X.Y.Z', () => {
    expect(problems({ en: 'a', fr: 'b', version: '0.13.0' })).toEqual([]);
    expect(problems({ en: 'a', fr: 'b', version: 'v0.13' })).toEqual(['« version » is not X.Y.Z']);
  });
});

describe('changelog', () => {
  it('groups the changes by version, newest first, the version being built included', () =>
    expect(changelog([
      { version: '0.9.0', date: '2026-10-01', text: { en: 'a', fr: 'a' } },
      { version: '0.10.0-ci.4', date: '2026-10-29', text: { en: 'b', fr: 'b' } },
      { version: '0.9.0', date: '2026-10-01', text: { en: 'c', fr: 'c' } },
    ])).toEqual([
      { version: '0.10.0-ci.4', date: '2026-10-29', changes: [{ en: 'b', fr: 'b' }] },
      { version: '0.9.0', date: '2026-10-01', changes: [{ en: 'a', fr: 'a' }, { en: 'c', fr: 'c' }] },
    ]));
});
