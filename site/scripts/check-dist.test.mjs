import { test } from 'node:test';
import assert from 'node:assert/strict';
import { findProblems } from './check-dist.mjs';

test('an external script, stylesheet, font or image is a problem', () => {
  const html =
    '<script src="https://cdn.example.com/a.js"></script><link rel="stylesheet" href="https://fonts.googleapis.com/x"><img src="https://x.org/a.png" alt="a">';
  assert.equal(findProblems([{ path: 'index.html', html }], []).length, 3);
});

test('an outbound link, a canonical or a sitemap link is not a problem', () => {
  const html =
    '<a href="https://github.com/Mistribe/Nid">GitHub</a><link rel="canonical" href="https://nid.mistribe.com/"><link rel="sitemap" href="/sitemap-index.xml">';
  assert.deepEqual(findProblems([{ path: 'index.html', html }], []), []);
});

test('a CSS url() to another site inside a style element is a problem', () => {
  const html = '<style>body{background:url(https://x.org/bg.png)}</style>';
  assert.equal(findProblems([{ path: 'index.html', html }], []).length, 1);
});

test('an image without alt, or with an empty alt, is a problem', () => {
  assert.equal(findProblems([{ path: 'a.html', html: '<img src="/a.png"><img src="/b.png" alt="">' }], []).length, 2);
});

test('a French page without its English twin is a problem, and the reverse', () => {
  const problems = findProblems([], ['installer/proxy.md', 'en/installer/proxy.md', 'impot.mdx', 'en/garde.mdx']);
  assert.deepEqual(problems.sort(), ['missing en/impot.mdx', 'missing garde.mdx']);
});

test('connection hints and shortcut icons to another site are problems', () => {
  const html =
    '<link rel="preconnect" href="https://fonts.gstatic.com"><link rel="dns-prefetch" href="//cdn.example.com"><link rel="shortcut icon" href="https://x.org/i.ico">';
  assert.equal(findProblems([{ path: 'index.html', html }], []).length, 3);
});

test('single-quoted sources, video posters and objects are checked too', () => {
  const html = "<img src='https://x.org/a.png' alt='a'><video poster=\"https://x.org/p.jpg\"></video><object data=\"https://x.org/o.svg\"></object>";
  assert.equal(findProblems([{ path: 'index.html', html }], []).length, 3);
});

test('a url() in a code sample is text, not a request; in a style attribute it is', () => {
  assert.deepEqual(findProblems([{ path: 'a.html', html: '<pre><code>background: url(https://x.org/bg.png)</code></pre>' }], []), []);
  assert.equal(findProblems([{ path: 'a.html', html: '<div style="background:url(https://x.org/bg.png)"></div>' }], []).length, 1);
});

test('a stylesheet that imports or points to another site is a problem', () => {
  const css = "@import url(https://fonts.googleapis.com/css2?family=X);@import 'https://x.org/a.css';.a{background:url(/ok.png)}";
  assert.equal(findProblems([{ path: '_astro/common.css', html: css }], []).length, 2);
});

test('an alt made of spaces is as good as none', () => {
  assert.equal(findProblems([{ path: 'a.html', html: '<img src="/a.png" alt="  ">' }], []).length, 1);
});
