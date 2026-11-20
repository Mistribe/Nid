import { randomBytes } from 'node:crypto';
import { mkdirSync, mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { gzipSync } from 'node:zlib';
import { describe, expect, it } from 'vitest';
import { BUDGET_BYTES, GROWTH_BYTES, initialBytes, inlineScripts, problems, run } from './budget.mjs';

const build = (manifest, files, html = '<script type="module" src="/entry.js"></script>') => {
  const dir = mkdtempSync(join(tmpdir(), 'budget-'));
  mkdirSync(join(dir, '.vite'));
  writeFileSync(join(dir, '.vite', 'manifest.json'), JSON.stringify(manifest));
  writeFileSync(join(dir, 'index.html'), html);
  for (const [name, text] of Object.entries(files)) writeFileSync(join(dir, name), text);
  return dir;
};

describe('initialBytes', () => {
  it('counts the entry and its static imports, not the lazy chunks', () => {
    const files = { 'entry.js': 'entry'.repeat(50), 'shared.js': 'shared'.repeat(50), 'lazy.js': 'lazy'.repeat(500) };
    const dir = build({
      'main.tsx': { file: 'entry.js', isEntry: true, imports: ['_shared'], dynamicImports: ['page.tsx'] },
      _shared: { file: 'shared.js', imports: ['main.tsx'] },
      'page.tsx': { file: 'lazy.js', imports: ['_shared'] },
    }, files);
    expect(initialBytes(dir)).toBe(gzipSync(files['entry.js']).length + gzipSync(files['shared.js']).length);
  });

  it('refuses a build without an entry', () =>
    expect(() => initialBytes(build({ 'a.tsx': { file: 'a.js' } }, { 'a.js': '' }))).toThrow('No entry chunk'));
});

describe('problems', () => {
  it('passes under the ceiling, without a base', () => expect(problems(BUDGET_BYTES)).toEqual([]));

  it('fails over the ceiling', () => expect(problems(BUDGET_BYTES + 1)).toHaveLength(1));

  it('lets a pull request grow by the allowance, not more', () => {
    expect(problems(100_000 + GROWTH_BYTES, 100_000)).toEqual([]);
    expect(problems(100_001 + GROWTH_BYTES, 100_000)[0]).toMatch('load the new part lazily');
  });

  it('passes a pull request that shrinks the bundle', () => expect(problems(90_000, 100_000)).toEqual([]));
});

describe('inlineScripts', () => {
  it('finds the scripts without src', () =>
    expect(inlineScripts('<script type="module" src="/a.js"></script><script>alert(1)</script>')).toEqual(['<script>']));
});

describe('run', () => {
  const entry = (text, html) => build({ 'main.tsx': { file: 'entry.js', isEntry: true } }, { 'entry.js': text }, html);
  const quiet = () => {
    const lines = [];
    return { lines, log: (line) => lines.push(line), error: (line) => lines.push(line) };
  };

  it('passes a build alone, and against a base of the same size', () => {
    const out = quiet();
    expect(run(entry('a'), undefined, out)).toBe(0);
    expect(run(entry('a'), entry('a'), out)).toBe(0);
    expect(out.lines.at(-1)).toBe('growth against the base: +0.0 kB (at most +5.0 kB)');
  });

  it('fails a build that grows too much against its base', () => {
    // random bytes barely compress: their gzip stays well over the allowance
    const big = randomBytes(GROWTH_BYTES * 2).toString('base64');
    const out = quiet();
    expect(run(entry(big), entry('a'), out)).toBe(1);
    expect(out.lines.at(-1)).toMatch('load the new part lazily');
  });

  it('fails an index.html with an inline script', () => {
    const out = quiet();
    expect(run(entry('a', '<script>alert(1)</script>'), undefined, out)).toBe(1);
    expect(out.lines[0]).toMatch('the CSP refuses them');
  });
});
