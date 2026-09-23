// Two checks on the production build: the JavaScript loaded before anything else stays within
// frontend/testing.md §3 (200 kB gzip), and index.html holds no inline script, which the CSP would refuse.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { gzipSync } from 'node:zlib';

const BUDGET_BYTES = 200 * 1024;
const dist = fileURLToPath(new URL('../dist/', import.meta.url));

const html = readFileSync(join(dist, 'index.html'), 'utf8');
const inline = [...html.matchAll(/<script\b(?![^>]*\bsrc=)[^>]*>/g)];
if (inline.length > 0) {
  console.error(`index.html holds ${inline.length} inline script(s); the CSP refuses them: ${inline.map((m) => m[0]).join(' ')}`);
  process.exit(1);
}

const manifest = JSON.parse(readFileSync(join(dist, '.vite', 'manifest.json'), 'utf8'));
const entryKey = Object.keys(manifest).find((key) => manifest[key].isEntry);
if (!entryKey) {
  console.error('No entry chunk in dist/.vite/manifest.json');
  process.exit(1);
}

// The entry and everything it imports statically; dynamic imports load later and are not counted.
const initial = new Set();
const visit = (key) => {
  if (initial.has(key)) return;
  initial.add(key);
  for (const imported of manifest[key].imports ?? []) visit(imported);
};
visit(entryKey);

let gzipped = 0;
for (const key of initial) gzipped += gzipSync(readFileSync(join(dist, manifest[key].file))).length;

const kilobytes = (bytes) => (bytes / 1024).toFixed(1);
console.log(`initial JavaScript: ${kilobytes(gzipped)} kB gzip (budget ${kilobytes(BUDGET_BYTES)} kB)`);
if (gzipped > BUDGET_BYTES) process.exit(1);
