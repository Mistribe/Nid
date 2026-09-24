import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { canonicalizeText } from '@/lib/canonical-json';
import { sha256Hex } from '@/lib/hash';
import { prepareFile, type Prepared, type ReadyPreparation } from './prepare';

/**
 * Built with `node:path` rather than `new URL(template, import.meta.url)`: Vite statically rewrites that pattern
 * into a glob asset import over the containing directory, which the dev server's default fs allow-list (scoped to
 * `web/`) then denies because the vectors live at the repo root, outside `web/` (see canonical-json.test.ts).
 */
const vectorsRoot = join(dirname(fileURLToPath(import.meta.url)), '../../../../tests/vectors/import');
const fixture = (name: string) => join(vectorsRoot, name);
const bytesOf = (text: string): ArrayBuffer => new TextEncoder().encode(text).buffer;
const fileBytes = (name: string): ArrayBuffer => {
  // Node's Buffer and jsdom's TextDecoder/ArrayBuffer belong to different realms; copy into a fresh
  // same-realm Uint8Array before handing off its buffer, so prepareFile sees a buffer jsdom's APIs accept.
  const buffer = readFileSync(fixture(name));
  const bytes = new Uint8Array(buffer.length);
  bytes.set(buffer);
  return bytes.buffer;
};

const ready = (prepared: Prepared): ReadyPreparation => {
  if (!prepared.ok) throw new Error(`refused: ${JSON.stringify(prepared.problems)}`);
  return prepared;
};
const problemsOf = async (text: string) => {
  const prepared = await prepareFile(bytesOf(text));
  if (prepared.ok) throw new Error('accepted');
  return prepared.problems;
};

type Expected = { file: string; section: string; index: number; count: number; sha256: string };
const expected = JSON.parse(readFileSync(fixture('batches.json'), 'utf8')) as Expected[];
const byName = (a: { section: string; index: number }, b: { section: string; index: number }) =>
  a.section < b.section ? -1 : a.section > b.section ? 1 : a.index - b.index;

describe('prepareFile against the batches the server computes (tests/vectors/import)', () => {
  it.each(['worked-examples.nid.json', 'batching.nid.json'])('cuts %s into the same batches with the same hashes', async (name) => {
    const prepared = ready(await prepareFile(fileBytes(name)));
    const mine = prepared.batches.map(({ section, index, count, hash }) => ({ file: name, section, index, count, sha256: hash })).sort(byName);
    expect(mine).toEqual(expected.filter((b) => b.file === name));
  });

  it('cuts 1 201 entities into 500, 500 and 201, numbered from 0', async () => {
    const prepared = ready(await prepareFile(fileBytes('batching.nid.json')));
    expect(prepared.batches.map((b) => [b.section, b.index, b.count])).toEqual([
      ['subscriptions', 0, 500], ['subscriptions', 1, 500], ['subscriptions', 2, 201],
    ]);
  });

  it('is deterministic: the same file gives the same batches', async () => {
    const once = ready(await prepareFile(fileBytes('worked-examples.nid.json')));
    const twice = ready(await prepareFile(fileBytes('worked-examples.nid.json')));
    expect(twice.batches).toEqual(once.batches);
  });

  it('orders batches by the commit order of import-format.md §11.4', async () => {
    const prepared = ready(await prepareFile(fileBytes('worked-examples.nid.json')));
    expect(prepared.batches.map((b) => b.section)).toEqual(['persons', 'subscriptions', 'billing_phases', 'purchases']);
  });

  it('writes a body whose hash is the hash of its entities', async () => {
    const prepared = ready(await prepareFile(fileBytes('worked-examples.nid.json')));
    for (const batch of prepared.batches) {
      const body = JSON.parse(batch.body) as { hash: string; entities: unknown[] };
      expect(Object.keys(body)).toEqual(['hash', 'entities']);
      expect(body.hash).toBe(batch.hash);
      expect(body.entities.length).toBe(batch.count);
      const entities = batch.body.slice(batch.body.indexOf(',"entities":') + ',"entities":'.length, -1);
      expect(await sha256Hex(canonicalizeText(entities))).toBe(batch.hash);
    }
  });

  it('keeps every number exactly as written in the batch body', async () => {
    const prepared = ready(await prepareFile(fileBytes('worked-examples.nid.json')));
    const phases = prepared.batches.find((b) => b.section === 'billing_phases');
    expect(phases?.body).toContain('"amount":17.20');
    const long = ready(await prepareFile(bytesOf('{"format":"nid.data","version":1,"purchases":[{"id":"p","price":{"amount":12345678901234567.89}}]}')));
    expect(long.batches[0]?.body).toContain('"amount":12345678901234567.89');
  });

  it('summarizes the sections and the adults of the file', async () => {
    const prepared = ready(await prepareFile(fileBytes('worked-examples.nid.json')));
    expect(prepared).toMatchObject({ format: 'nid.data', version: 1 });
    expect(prepared.sections).toEqual([
      { name: 'persons', count: 2 }, { name: 'subscriptions', count: 3 }, { name: 'billing_phases', count: 2 }, { name: 'purchases', count: 3 },
    ]);
    expect(prepared.adults).toEqual([
      { ref: 'adult-1', displayName: 'Adulte 1', email: 'alice@example.test' },
      { ref: 'adult-2', displayName: 'Adulte 2', email: null },
    ]);
  });

  it('accepts an empty section and sends no batch for it', async () => {
    const prepared = ready(await prepareFile(bytesOf('{"format":"nid.data","version":1,"providers":[],"persons":[{"ref":"a","kind":"dependent","display_name":"Léo"}]}')));
    expect(prepared.sections).toEqual([{ name: 'persons', count: 1 }, { name: 'providers', count: 0 }]);
    expect(prepared.batches.map((b) => b.section)).toEqual(['persons']);
  });
});

describe('the local check', () => {
  it('refuses bytes that are not UTF-8', async () => {
    expect(await prepareFile(new Uint8Array([0x7b, 0xff, 0x7d]).buffer)).toEqual({ ok: false, problems: [{ code: 'not_utf8' }] });
  });

  it('reports invalid JSON with its position', async () => {
    expect(await problemsOf('{\n  "format": "nid.data",\n  "version": 1,\n}')).toEqual([{ code: 'not_json', line: 4, column: 1 }]);
  });

  it('reports a repeated member name with its position', async () => {
    expect(await problemsOf('{"format":"nid.data","version":1,"persons":[{"ref":"a","ref":"b"}]}')).toEqual([
      { code: 'duplicate_member', line: 1, column: 56 },
    ]);
  });

  it('refuses nesting deeper than 32 levels', async () => {
    const deep = `{"format":"nid.data","version":1,"persons":[${'['.repeat(31)}${']'.repeat(31)}]}`;
    expect((await problemsOf(deep))[0]?.code).toBe('too_deep');
  });

  it('checks the envelope: an object, format and version', async () => {
    expect(await problemsOf('[]')).toEqual([{ code: 'not_object' }]);
    expect(await problemsOf('{"format":"other","version":2,"persons":[]}')).toEqual([
      { code: 'format', path: 'format' }, { code: 'version', path: 'version' },
    ]);
    expect(await problemsOf('{"format":"nid.data","version":"1","persons":[]}')).toEqual([{ code: 'version', path: 'version' }]);
  });

  it('refuses unknown and reserved sections, and a section that is not a list', async () => {
    expect(await problemsOf('{"format":"nid.data","version":1,"subscription":[],"documents":[],"persons":{}}')).toEqual([
      { code: 'unknown_section', path: 'subscription' },
      { code: 'reserved_section', path: 'documents' },
      { code: 'not_array', path: 'persons' },
    ]);
  });

  it('finds what the server refuses on receipt, with the server path', async () => {
    const text = String.raw`{"format":"nid.data","version":1,"subscriptions":[{"id":"a","name":"ok"},{"id":"b","name":"x\ud800","note":"y\u0000","tags":{"\udc00":1}}],"purchases":[{"id":"p","price":{"amount":1e400}}]}`;
    expect(await problemsOf(text)).toEqual([
      { code: 'lone_surrogate', path: 'subscriptions[1].name' },
      { code: 'nul', path: 'subscriptions[1].note' },
      { code: 'lone_surrogate', path: 'subscriptions[1].tags' },
      { code: 'number_out_of_range', path: 'purchases[0].price.amount' },
    ]);
  });

  it('refuses a batch whose body exceeds 1 MB, naming where it starts', async () => {
    const big = 'x'.repeat(2_200);
    const entities = Array.from({ length: 501 }, (_, i) => `{"id":"s-${i}","note":"${big}"}`).join(',');
    expect(await problemsOf(`{"format":"nid.data","version":1,"subscriptions":[${entities}]}`)).toEqual([
      { code: 'batch_too_large', path: 'subscriptions[0]' },
    ]);
  });

  it('refuses a file with no entity at all', async () => {
    expect(await problemsOf('{"format":"nid.data","version":1,"persons":[]}')).toEqual([{ code: 'empty' }]);
  });
});
