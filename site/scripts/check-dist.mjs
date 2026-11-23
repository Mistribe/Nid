// Checks the built site (issue #545): nothing is fetched from another site at run time, every image has alt text, and every
// page exists in French and in English. Run after `astro build`: node scripts/check-dist.mjs
import { readdir, readFile } from 'node:fs/promises';
import { join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const OWN_HOST = 'nid.mistribe.com';
const isForeign = (url) => /^(https?:)?\/\//i.test(url) && new URL(url, `https://${OWN_HOST}/`).host !== OWN_HOST;

// Only what the browser loads by itself counts: <a href> and rel=canonical/alternate/sitemap are not requests, nor is a url()
// in a code sample. Stylesheets (dist/_astro/*.css) are checked for url() and @import.
const LINK_LOADS = new Set(['stylesheet', 'preload', 'modulepreload', 'prefetch', 'preconnect', 'dns-prefetch', 'icon', 'apple-touch-icon', 'manifest']);
const SOURCE_ATTRIBUTES = { script: ['src'], img: ['src', 'srcset'], source: ['src', 'srcset'], iframe: ['src'], video: ['src', 'poster'], audio: ['src'], track: ['src'], embed: ['src'], object: ['data'] };

const attributesOf = (tag) =>
  Object.fromEntries([...tag.matchAll(/([\w:-]+)\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s>]+))/g)].map(([, name, a, b, c]) => [name.toLowerCase(), a ?? b ?? c]));

const cssUrls = (css) => [
  ...[...css.matchAll(/url\(\s*['"]?([^'")\s]+)/gi)].map((m) => m[1]),
  ...[...css.matchAll(/@import\s+['"]([^'"]+)['"]/gi)].map((m) => m[1]),
];

function loadedUrls(path, text) {
  if (path.endsWith('.css')) return cssUrls(text);
  const urls = [];
  for (const [tag, name] of text.matchAll(/<([a-z]+)\b[^>]*>/gi)) {
    const attributes = attributesOf(tag);
    const element = name.toLowerCase();
    if (attributes.style) urls.push(...cssUrls(attributes.style));
    if (element === 'link') {
      const rel = (attributes.rel ?? '').toLowerCase().split(/\s+/);
      if (rel.some((r) => LINK_LOADS.has(r)) && attributes.href) urls.push(attributes.href);
      continue;
    }
    for (const attribute of SOURCE_ATTRIBUTES[element] ?? []) {
      const value = attributes[attribute];
      if (!value) continue;
      urls.push(...(attribute === 'srcset' ? value.split(',').map((part) => part.trim().split(/\s+/)[0]) : [value]));
    }
  }
  for (const [, css] of text.matchAll(/<style\b[^>]*>([\s\S]*?)<\/style>/gi)) urls.push(...cssUrls(css));
  return urls;
}

/** @param {Array<{ path: string, html: string }>} files HTML pages and stylesheets (.css) @param {string[]} contentPaths @returns {string[]} */
export function findProblems(files, contentPaths) {
  const problems = [];
  for (const { path, html } of files) {
    for (const url of loadedUrls(path, html)) if (url && isForeign(url)) problems.push(`${path}: loads ${url}`);
    if (path.endsWith('.css')) continue;
    for (const [img] of html.matchAll(/<img\b[^>]*>/gi)) {
      if (!/\balt=(?:"[^"]*\S[^"]*"|'[^']*\S[^']*')/i.test(img)) problems.push(`${path}: image without alt text: ${img.slice(0, 80)}`);
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
  const htmlFiles = (await walk(dist)).filter((f) => f.endsWith('.html') || f.endsWith('.css'));
  const files = await Promise.all(htmlFiles.map(async (f) => ({ path: relative(dist, f), html: await readFile(f, 'utf8') })));
  const contentPaths = (await walk(docs)).filter((f) => /\.mdx?$/.test(f)).map((f) => relative(docs, f));
  const problems = findProblems(files, contentPaths);
  for (const problem of problems) console.error(problem);
  if (problems.length > 0) process.exit(1);
  console.log(`check-dist: ${files.length} pages and stylesheets, ${contentPaths.length} sources, no problem`);
}
