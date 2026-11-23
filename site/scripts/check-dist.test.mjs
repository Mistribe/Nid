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
