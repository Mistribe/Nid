// Checks on the production build (frontend/testing.md §3): the JavaScript loaded before anything else stays under its ceiling,
// and, given `--base <dist>` (the build of main, web.yml), grows by at most GROWTH_BYTES in one pull request; index.html holds
// no inline script, which the CSP would refuse.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { gzipSync } from 'node:zlib';

export const BUDGET_BYTES = 250 * 1024;
export const GROWTH_BYTES = 5 * 1024;

const kilobytes = (bytes) => (bytes / 1024).toFixed(1);

export const inlineScripts = (html) => [...html.matchAll(/<script\b(?![^>]*\bsrc=)[^>]*>/g)].map((m) => m[0]);

// The entry and everything it imports statically, gzipped; dynamic imports load later and are not counted.
export function initialBytes(dir) {
  const manifest = JSON.parse(readFileSync(join(dir, '.vite', 'manifest.json'), 'utf8'));
  const entryKey = Object.keys(manifest).find((key) => manifest[key].isEntry);
  if (!entryKey) throw new Error(`No entry chunk in ${join(dir, '.vite', 'manifest.json')}`);
  const initial = new Set();
  const visit = (key) => {
    if (initial.has(key)) return;
    initial.add(key);
    for (const imported of manifest[key].imports ?? []) visit(imported);
  };
  visit(entryKey);
  let gzipped = 0;
  for (const key of initial) gzipped += gzipSync(readFileSync(join(dir, manifest[key].file))).length;
  return gzipped;
}

// What is wrong with an initial bundle of `bytes`, given the base's when there is one.
export function problems(bytes, baseBytes) {
  const found = [];
  if (bytes > BUDGET_BYTES) found.push(`The initial bundle is over its ceiling of ${kilobytes(BUDGET_BYTES)} kB gzip.`);
  if (baseBytes !== undefined && bytes - baseBytes > GROWTH_BYTES)
    found.push(`The initial bundle grows by more than ${kilobytes(GROWTH_BYTES)} kB in one pull request: load the new part lazily (a route, a dynamic import).`);
  return found;
}

// Runs every check on the build in `dist`, against the one in `baseDist` when given; returns the exit code.
export function run(dist, baseDist, out = console) {
  const inline = inlineScripts(readFileSync(join(dist, 'index.html'), 'utf8'));
  if (inline.length > 0) {
    out.error(`index.html holds ${inline.length} inline script(s); the CSP refuses them: ${inline.join(' ')}`);
    return 1;
  }
  const bytes = initialBytes(dist);
  out.log(`initial JavaScript: ${kilobytes(bytes)} kB gzip (ceiling ${kilobytes(BUDGET_BYTES)} kB)`);
  const baseBytes = baseDist ? initialBytes(baseDist) : undefined;
  if (baseBytes !== undefined) {
    const growth = bytes - baseBytes;
    out.log(`growth against the base: ${growth >= 0 ? '+' : ''}${kilobytes(growth)} kB (at most +${kilobytes(GROWTH_BYTES)} kB)`);
  }
  const found = problems(bytes, baseBytes);
  for (const problem of found) out.error(problem);
  return found.length > 0 ? 1 : 0;
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  const baseIndex = process.argv.indexOf('--base');
  process.exit(run(fileURLToPath(new URL('../dist/', import.meta.url)), baseIndex > 0 ? process.argv[baseIndex + 1] : undefined));
}
