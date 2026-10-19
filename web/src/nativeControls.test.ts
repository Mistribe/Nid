import { readdirSync, readFileSync } from 'node:fs';
import { join, relative, sep } from 'node:path';
import { describe, expect, it } from 'vitest';

const root = join(__dirname);
const native = /<select\b|<dialog\b|<details\b|<table\b|type=\{?["']?(date|checkbox|radio)\b|NativeSelect/;

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
