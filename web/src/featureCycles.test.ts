import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join, relative, resolve, sep } from 'node:path';
import { describe, expect, it } from 'vitest';

// frontend/architecture.md §2: features depend on api/, components/ and lib/, and on one another one way only. A cycle between
// features (often a query path or an `invalidate…` helper read from another feature's api.ts) is cut by moving that contract to
// src/api/<domain>.ts, or a shared piece of UI to components/. Tests are left out: they may load another feature's fixtures or page.
const features = join(__dirname, 'features');
const specifier = /\b(?:from|import)\s+['"]([^'"]+)['"]|\bimport\(\s*['"]([^'"]+)['"]\s*\)/g;
// Comments go, string literals stay (a `'/*'` or `'://'` is not a comment).
const STRIP = /("(?:\\.|[^"\\\n])*"|'(?:\\.|[^'\\\n])*'|`(?:\\.|[^`\\])*`)|\/\*[\s\S]*?\*\/|\/\/.*$/gm;

function sources(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name);
    return entry.isDirectory() ? sources(path) : [path];
  });
}

/** The feature a path sits in (`features/<name>/…`), or `undefined` outside features/. */
function featureOf(path: string): string | undefined {
  const parts = relative(features, path).split(sep);
  return parts.length > 1 && parts[0] !== '..' ? parts[0] : undefined;
}

/** Every import of one feature by another: `from → to → the files and specifiers that make it`. */
function featureEdges(): Map<string, Map<string, string[]>> {
  const edges = new Map<string, Map<string, string[]>>();
  for (const file of sources(features).filter((path) => /\.tsx?$/.test(path) && !/\.test\.tsx?$/.test(path))) {
    const from = featureOf(file)!;
    for (const match of readFileSync(file, 'utf8').replace(STRIP, '$1').matchAll(specifier)) {
      const spec = match[1] ?? match[2] ?? '';
      const to = spec.startsWith('@/features/') ? spec.split('/')[2]
        : spec.startsWith('.') ? featureOf(resolve(dirname(file), spec)) : undefined;
      if (!to || to === from) continue;
      const targets = edges.get(from) ?? new Map<string, string[]>();
      targets.set(to, [...(targets.get(to) ?? []), `${relative(features, file).split(sep).join('/')} → ${spec}`]);
      edges.set(from, targets);
    }
  }
  return edges;
}

/** The strongly connected components of more than one feature (Tarjan): each is a cycle, or several sharing features. */
function cycles(edges: Map<string, Map<string, string[]>>): string[][] {
  const index = new Map<string, number>();
  const low = new Map<string, number>();
  const stack: string[] = [];
  const found: string[][] = [];
  const visit = (node: string) => {
    index.set(node, index.size);
    low.set(node, index.get(node)!);
    stack.push(node);
    for (const next of edges.get(node)?.keys() ?? []) {
      if (!index.has(next)) {
        visit(next);
        low.set(node, Math.min(low.get(node)!, low.get(next)!));
      } else if (stack.includes(next)) low.set(node, Math.min(low.get(node)!, index.get(next)!));
    }
    if (low.get(node) !== index.get(node)) return;
    const component: string[] = [];
    let member: string;
    do { member = stack.pop()!; component.push(member); } while (member !== node);
    if (component.length > 1) found.push(component.sort());
  };
  for (const node of edges.keys()) if (!index.has(node)) visit(node);
  return found;
}

describe('features', () => {
  it('import one another without a cycle', () => {
    const edges = featureEdges();
    // Each import inside a cycle, so the failure says which lines to move.
    const offenders = cycles(edges).flatMap((component) => component.flatMap((from) =>
      [...(edges.get(from) ?? new Map<string, string[]>())].filter(([to]) => component.includes(to)).flatMap(([, lines]) => lines)));
    expect(offenders).toEqual([]);
  });

  it('are seen importing one another', () => {
    // The check reads the imports it guards: an empty graph would pass whatever the code does.
    expect(featureEdges().get('properties')?.has('loans')).toBe(true);
  });
});
