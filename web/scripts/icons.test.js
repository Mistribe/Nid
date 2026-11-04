import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';

// The tests run from web/, under jsdom: import.meta.url is no file URL there.
const root = process.cwd();
const read = (path) => readFileSync(join(root, path), 'utf8');

describe('the icons', () => {
  it('the favicon is the nest on a primary tile', () => {
    const svg = read('public/favicon.svg');
    expect(svg).toMatch(/<rect[^>]*\brx="[^"]+"[^>]*fill="#[0-9a-f]{6}"/i);
    expect(svg).toContain('M3 12.5 Q12 23 21 12.5');
    expect(svg).toContain('M6.5 14 Q12 19.5 17.5 14');
    expect(svg).toContain('<ellipse');
    expect(svg).not.toContain('M3.4 18H12');
  });

  it.each([
    ['apple-touch-icon.png', 180],
    ['icon-192.png', 192],
    ['icon-512.png', 512],
    ['icon-maskable-512.png', 512],
  ])('%s is a %i px square PNG', (name, size) => {
    const path = join(root, 'public', name);
    expect(existsSync(path)).toBe(true);
    const bytes = readFileSync(path);
    expect([bytes.readUInt32BE(16), bytes.readUInt32BE(20)]).toEqual([size, size]);
  });

  it('index.html links the touch icon', () =>
    expect(read('index.html')).toContain('<link rel="apple-touch-icon" href="/apple-touch-icon.png" />'));
});
