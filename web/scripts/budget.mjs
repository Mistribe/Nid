// Checks on the production build (frontend/testing.md §3): the JavaScript loaded before anything else stays under its ceiling,
// and, given `--base <dist>` (the build of main, web.yml), grows by at most GROWTH_BYTES in one pull request; index.html holds
// no inline script, which the CSP would refuse.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { gzipSync } from 'node:zlib';

const BUDGET_BYTES = 250 * 1024;
const GROWTH_BYTES = 5 * 1024;
const dist = fileURLToPath(new URL('../dist/', import.meta.url));
const baseIndex = process.argv.indexOf('--base');
const baseDist = baseIndex > 0 ? process.argv[baseIndex + 1] : undefined;

const html = readFileSync(join(dist, 'index.html'), 'utf8');
const inline = [...html.matchAll(/<script\b(?![^>]*\bsrc=)[^>]*>/g)];
if (inline.length > 0) {
  console.error(`index.html holds ${inline.length} inline script(s); the CSP refuses them: ${inline.map((m) => m[0]).join(' ')}`);
  process.exit(1);
}

// The entry and everything it imports statically, gzipped; dynamic imports load later and are not counted.
const initialBytes = (dir) => {
  const manifest = JSON.parse(readFileSync(join(dir, '.vite', 'manifest.json'), 'utf8'));
  const entryKey = Object.keys(manifest).find((key) => manifest[key].isEntry);
  if (!entryKey) {
    console.error(`No entry chunk in ${join(dir, '.vite', 'manifest.json')}`);
    process.exit(1);
  }
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
};

const kilobytes = (bytes) => (bytes / 1024).toFixed(1);
const gzipped = initialBytes(dist);
console.log(`initial JavaScript: ${kilobytes(gzipped)} kB gzip (ceiling ${kilobytes(BUDGET_BYTES)} kB)`);
let failed = gzipped > BUDGET_BYTES;

if (baseDist) {
  const growth = gzipped - initialBytes(baseDist);
  console.log(`growth against the base: ${growth >= 0 ? '+' : ''}${kilobytes(growth)} kB (at most +${kilobytes(GROWTH_BYTES)} kB)`);
  if (growth > GROWTH_BYTES) {
    console.error('The initial bundle grows too much in one pull request: load the new part lazily (a route, a dynamic import).');
    failed = true;
  }
}
if (failed) process.exit(1);
