import { test } from 'node:test';
import assert from 'node:assert/strict';
import { shots } from './capture.mjs';
import { areas } from '../src/data/areas.ts';

test('two captures per area, light then dark, named after its slug', () => {
  const list = shots('https://demo.example');
  assert.equal(list.length, areas.length * 2);
  assert.deepEqual(list[0], { url: 'https://demo.example/dashboard', file: 'src/assets/screens/tableau-de-bord-light.png', colorScheme: 'light' });
  for (const { slug } of areas) {
    assert.deepEqual(list.filter((s) => s.file.includes(`/${slug}-`)).map((s) => s.colorScheme).sort(), ['dark', 'light']);
  }
});

test('a trailing slash on the base does not double the slash', () => {
  assert.equal(shots('https://demo.example/')[0].url, 'https://demo.example/dashboard');
});
