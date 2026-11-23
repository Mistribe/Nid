// Checks the built site (issue #545): nothing is fetched from another site at run time, every image has alt text, and every
// page exists in French and in English. Run after `astro build`: node scripts/check-dist.mjs
import { readdir, readFile } from 'node:fs/promises';
import { join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const OWN_HOST = 'nid.mistribe.com';
const isForeign = (url) => /^(https?:)?\/\//i.test(url) && new URL(url, `https://${OWN_HOST}/`).host !== OWN_HOST;

// Only what the browser loads by itself counts: <a href> and rel=canonical/alternate/sitemap are not requests.
const LOADS = [
  /<script\b[^>]*\bsrc="([^"]+)"/gi,
  /<link\b(?=[^>]*\brel="(?:stylesheet|preload|modulepreload|icon|apple-touch-icon|manifest)")[^>]*\bhref="([^"]+)"/gi,
  /<(?:img|source|iframe|video|audio|embed)\b[^>]*\bsrc="([^"]+)"/gi,
  /\bsrcset="([^"]+)"/gi,
  /url\(\s*['"]?([^'")\s]+)/gi,
];

/** @param {Array<{ path: string, html: string }>} files @param {string[]} contentPaths @returns {string[]} */
export function findProblems(files, contentPaths) {
  const problems = [];
  for (const { path, html } of files) {
    for (const pattern of LOADS) {
      for (const [, value] of html.matchAll(pattern)) {
        for (const url of value.split(',').map((part) => part.trim().split(/\s+/)[0])) {
          if (url && isForeign(url)) problems.push(`${path}: loads ${url}`);
        }
      }
    }
    for (const [img] of html.matchAll(/<img\b[^>]*>/gi)) {
      if (!/\balt="[^"]*\S[^"]*"/i.test(img)) problems.push(`${path}: image without alt text: ${img.slice(0, 80)}`);
    }
  }
  const french = new Set(contentPaths.filter((p) => !p.startsWith('en/')));
  const english = new Set(contentPaths.filter((p) => p.startsWith('en/')).map((p) => p.slice(3)));
  for (const p of french) if (!english.has(p)) problems.push(`missing en/${p}`);
  for (const p of english) if (!french.has(p)) problems.push(`missing ${p}`);
  return problems;
}

async function walk(dir) {
  const entries = await readdir(dir, { withFileTypes: true, recursive: true });
  return entries.filter((e) => e.isFile()).map((e) => join(e.parentPath, e.name));
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const root = fileURLToPath(new URL('..', import.meta.url));
  const dist = join(root, 'dist');
  const docs = join(root, 'src/content/docs');
  const htmlFiles = (await walk(dist)).filter((f) => f.endsWith('.html'));
  const files = await Promise.all(htmlFiles.map(async (f) => ({ path: relative(dist, f), html: await readFile(f, 'utf8') })));
  const contentPaths = (await walk(docs)).filter((f) => /\.mdx?$/.test(f)).map((f) => relative(docs, f));
  const problems = findProblems(files, contentPaths);
  for (const problem of problems) console.error(problem);
  if (problems.length > 0) process.exit(1);
  console.log(`check-dist: ${files.length} pages, ${contentPaths.length} sources, no problem`);
}
