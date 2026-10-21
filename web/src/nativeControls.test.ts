import { readdirSync, readFileSync } from 'node:fs';
import { join, relative, sep } from 'node:path';
import { describe, expect, it } from 'vitest';

const root = join(__dirname);
const native = /<select\b|<dialog\b|<details\b|<table\b|type=\{?["']?(date|checkbox|radio)\b|NativeSelect/;
// Colours come from theme tokens (design-system.md §2): a Tailwind palette class bypasses the themes and their contrast test.
const palette = /\b(?:bg|text|border|ring|outline|fill|stroke|from|via|to|divide|decoration|shadow|accent|caret|placeholder)-(?:slate|gray|zinc|neutral|stone|red|orange|amber|yellow|lime|green|emerald|teal|cyan|sky|blue|indigo|violet|purple|fuchsia|pink|rose)-\d{2,3}\b/;

function sources(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name);
    return entry.isDirectory() ? sources(path) : [path];
  });
}

describe('native controls', () => {
  it('are used nowhere outside components/ui', () => {
    const offenders = sources(root)
      .map((path) => relative(root, path))
      .filter((path) => path.endsWith('.tsx') && !path.endsWith('.test.tsx') && !path.startsWith(`components${sep}ui${sep}`))
      .filter((path) => native.test(readFileSync(join(root, path), 'utf8')));
    expect(offenders).toEqual([]);
  });
});

describe('palette colours', () => {
  it('are used nowhere outside components/ui', () => {
    const offenders = sources(root)
      .map((path) => relative(root, path))
      .filter((path) => /\.tsx?$/.test(path) && !/\.test\.tsx?$/.test(path) && !path.startsWith(`components${sep}ui${sep}`))
      .filter((path) => palette.test(readFileSync(join(root, path), 'utf8')));
    expect(offenders).toEqual([]);
  });
});
