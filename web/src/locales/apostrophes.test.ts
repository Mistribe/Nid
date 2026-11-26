import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';

/** Every string of a JSON tree, with its key path. */
function strings(value: unknown, path: string): [string, string][] {
  if (typeof value === 'string') return [[path, value]];
  if (value && typeof value === 'object') return Object.entries(value).flatMap(([key, v]) => strings(v, path ? `${path}.${key}` : key));
  return [];
}

describe('French strings', () => {
  // #729: the typographic apostrophe everywhere, as the rest of the copy.
  it('use ’, never a straight apostrophe', () => {
    const offenders = readdirSync(__dirname)
      .filter((file) => file.startsWith('fr') && file.endsWith('.json'))
      .flatMap((file) => strings(JSON.parse(readFileSync(join(__dirname, file), 'utf8')), file))
      .filter(([, text]) => text.includes("'"))
      .map(([path]) => path);
    expect(offenders).toEqual([]);
  });
});
