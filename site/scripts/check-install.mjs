// Keeps the install guide in step with deploy/README.md, the full reference: every shell block of an install page must
// appear there verbatim, as whole lines (compared trimmed). Untagged blocks (a Caddyfile) count too; YAML and nginx do not. Run by `pnpm --dir site check`: node scripts/check-install.mjs
import { readdir, readFile } from 'node:fs/promises';
import { join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const FENCE = /^[ \t]*```([\w-]*)[^\n]*\n([\s\S]*?)^[ \t]*```/gm;
const CHECKED = new Set(['', 'sh', 'bash', 'shell']);

const normalise = (text) => text.split('\n').map((line) => line.trim()).filter((line) => line !== '').join('\n');

/** @param {Array<{ path: string, text: string }>} pages @param {string} readme @returns {string[]} */
export function staleBlocks(pages, readme) {
  // Framed by newlines so a block only matches whole lines, not the end of one and the start of another.
  const reference = `\n${normalise(readme)}\n`;
  const problems = [];
  for (const { path, text } of pages) {
    let index = 0;
    for (const [, lang, body] of text.matchAll(FENCE)) {
      if (!CHECKED.has(lang)) continue;
      index += 1;
      if (!reference.includes(`\n${normalise(body)}\n`)) problems.push(`${path}: block ${index} not in deploy/README.md`);
    }
  }
  return problems;
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const site = fileURLToPath(new URL('..', import.meta.url));
  const docs = join(site, 'src/content/docs');
  const files = (await readdir(docs, { recursive: true, withFileTypes: true }))
    .filter((e) => e.isFile() && /\.mdx?$/.test(e.name))
    .map((e) => join(e.parentPath, e.name))
    .filter((f) => /(^|\/)installer\//.test(relative(docs, f)));
  const pages = await Promise.all(files.map(async (f) => ({ path: relative(docs, f), text: await readFile(f, 'utf8') })));
  const problems = staleBlocks(pages, await readFile(join(site, '../deploy/README.md'), 'utf8'));
  for (const problem of problems) console.error(problem);
  if (problems.length > 0) process.exit(1);
  console.log(`check-install: ${pages.length} install pages match deploy/README.md`);
}
