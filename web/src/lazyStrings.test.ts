import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import { describe, expect, it } from 'vitest';
import fr from '@/locales/fr.json';

const root = join(__dirname);
const locales = join(root, 'locales');

function sources(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name);
    return entry.isDirectory() ? sources(path) : [path];
  });
}

const bundles = readdirSync(locales)
  .map((name) => /^fr\.(.+)\.json$/.exec(name)?.[1])
  // Only the bundles merged into `translation` by a loader; `subscriptions-board` is its own namespace.
  .filter((module): module is string => module !== undefined && existsSync(join(locales, `${module}.ts`)))
  .map((module) => ({ module, keys: Object.keys(JSON.parse(readFileSync(join(locales, `fr.${module}.json`), 'utf8')) as object) }));

// A module bundle joins `translation` only once a file importing `@/locales/<module>` loads (frontend/architecture.md §11).
describe('module string bundles', () => {
  it('are imported by every file that uses their keys', () => {
    const files = sources(root).filter((path) => /\.tsx?$/.test(path) && !/\.test\.tsx?$/.test(path) && !path.startsWith(locales));
    const offenders = files.flatMap((path) => {
      const text = readFileSync(path, 'utf8');
      return bundles
        .filter(({ module, keys }) => keys.some((key) => new RegExp(`['"\`]${key}[.'"\`]`).test(text)) && !text.includes(`import '@/locales/${module}';`))
        .map(({ module }) => `${relative(root, path)} → @/locales/${module}`);
    });
    expect(offenders).toEqual([]);
  });

  it('do not reuse a key of the initial bundle or of another module', () => {
    const all = [...Object.keys(fr), ...bundles.flatMap(({ keys }) => keys)];
    expect(all.filter((key, i) => all.indexOf(key) !== i)).toEqual([]);
  });
});
