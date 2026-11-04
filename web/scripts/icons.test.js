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

  it('the tile colour is the same in the favicon and the rendered icons', () => {
    const tile = /const TILE = '(#[0-9a-f]{6})'/i.exec(read('scripts/render-icons.mjs'))?.[1];
    expect(tile).toBeDefined();
    expect(read('public/favicon.svg')).toMatch(new RegExp(`<rect[^>]*fill="${tile}"`, 'i'));
  });

  it('the favicon mark stays inside its safe zone', () => {
    const svg = read('public/favicon.svg');
    // The mark is the 24 px nest translated by (4 4) on the 32 px tile: it must keep a margin of at least 3 px on each side.
    const [dx, dy] = /translate\((\d+(?:\.\d+)?) (\d+(?:\.\d+)?)\)/.exec(svg).slice(1).map(Number);
    const stroke = Number(/stroke-width="([\d.]+)"/.exec(svg)[1]);
    // The arcs run x 3…21 and down to y ≈ 17.75 (the curve's low point); the egg top is y 5.2.
    const [minX, maxX, minY, maxY] = [3 - stroke / 2, 21 + stroke / 2, 9 - 3.8, 17.75 + stroke / 2];
    for (const margin of [dx + minX, 32 - (dx + maxX), dy + minY, 32 - (dy + maxY)]) expect(margin).toBeGreaterThanOrEqual(3);
  });

  it('the favicon geometry is the Logo component\'s: same arcs and egg', () => {
    const svg = read('public/favicon.svg');
    const logo = read('src/components/Logo.tsx');
    const paths = [...svg.matchAll(/<path d="([^"]+)"/g)].map((m) => m[1]);
    expect(paths.length).toBe(2);
    for (const d of paths) expect(logo).toContain(`d="${d}"`);
    const egg = (text) => /<ellipse cx="([\d.]+)" cy="([\d.]+)" rx="([\d.]+)" ry="([\d.]+)"/.exec(text).slice(1).join(' ');
    expect(egg(svg)).toBe(egg(logo));
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
