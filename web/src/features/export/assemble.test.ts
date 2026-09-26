import { describe, expect, it, vi } from 'vitest';
import { createApi } from '@/api/client';
import { ProblemError } from '@/api/errors';
import { fakeExportServer } from '@/test/exportServer';
import { server } from '@/test/server';
import { exportFileName, runExport, type ExportProgress } from './assemble';

const client = () => createApi({ getCsrf: () => null, onUnauthenticated: vi.fn() }).fetchClient;
const person = { ref: 'adult-1', kind: 'adult', display_name: 'Alice', email: 'alice@example.test' };
const sub = (n: number) => ({ id: `sub-${n}`, name: `Abonnement ${n}`, price: { amount: '9.90', currency: 'EUR' } });
const household = () => ({
  persons: [[person]],
  providers: [[]],
  subscriptions: [[sub(1)], [sub(2)], [sub(3)]],
  billing_phases: [[]],
  purchases: [[]],
});

describe('runExport', () => {
  it('follows each section cursor to the end and writes the nid.data envelope in the manifest order', async () => {
    const fake = fakeExportServer(household());
    server.use(...fake.handlers);
    const progress: ExportProgress[] = [];

    const result = await runExport(client(), {
      signal: new AbortController().signal, onProgress: (p) => progress.push(p), today: new Date(2026, 8, 26),
    });

    expect(fake.state.calls).toEqual([
      'persons:', 'providers:', 'subscriptions:', 'subscriptions:c1', 'subscriptions:c2', 'billing_phases:', 'purchases:',
    ]);
    if (result.kind !== 'done') throw new Error(`expected done, got ${result.kind}`);
    expect(result.fileName).toBe('nid-2026-09-26.nid.json');
    expect(result.count).toBe(4);
    expect(result.blob.type).toBe('application/json');
    const file = JSON.parse(await result.blob.text()) as Record<string, unknown>;
    expect(Object.keys(file)).toEqual([
      'format', 'version', 'exported_at', 'persons', 'providers', 'subscriptions', 'billing_phases', 'purchases',
    ]);
    expect(file).toEqual({
      format: 'nid.data', version: 1, exported_at: '2026-09-26T10:00:00+00:00',
      persons: [person], providers: [], subscriptions: [sub(1), sub(2), sub(3)], billing_phases: [], purchases: [],
    });
    expect(progress[0]).toEqual({ received: 0, total: 4 });
    expect(progress.at(-1)).toEqual({ received: 4, total: 4 });
  });

  it('gives "changed" when a page answers 409 export.revision_changed', async () => {
    const fake = fakeExportServer(household());
    fake.state.failWith = { section: 'subscriptions', page: 1, status: 409, code: 'export.revision_changed' };
    server.use(...fake.handlers);

    expect(await runExport(client(), { signal: new AbortController().signal })).toEqual({ kind: 'changed' });
    expect(fake.state.calls).not.toContain('billing_phases:');
  });

  it('gives "changed", and no file, when the final manifest has another revision', async () => {
    const fake = fakeExportServer(household());
    fake.state.onPage = (section) => { if (section === 'purchases') fake.state.revision = 'r2'; };
    server.use(...fake.handlers);

    expect(await runExport(client(), { signal: new AbortController().signal })).toEqual({ kind: 'changed' });
  });

  // Review Focus 4: data added between the manifest and a page.
  it('never reports more received than its maximum when pages hold more than the manifest said', async () => {
    const pages = household();
    const fake = fakeExportServer(pages);
    fake.state.onPage = (section, page) => {
      if (section === 'subscriptions' && page === 0) { pages.subscriptions.at(-1)?.push(sub(4)); fake.state.revision = 'r2'; }
    };
    server.use(...fake.handlers);
    const progress: ExportProgress[] = [];

    const result = await runExport(client(), { signal: new AbortController().signal, onProgress: (p) => progress.push(p) });

    expect(result).toEqual({ kind: 'changed' });
    expect(progress.every((p) => p.received <= p.total)).toBe(true);
    expect(progress.at(-1)).toEqual({ received: 5, total: 5 });
  });

  it('stops on abort, without a file', async () => {
    server.use(...fakeExportServer(household()).handlers);
    const controller = new AbortController();

    const running = runExport(client(), {
      signal: controller.signal, onProgress: (p) => { if (p.received === 1) controller.abort(); },
    });

    await expect(running).rejects.toMatchObject({ name: 'AbortError' });
  });

  it('throws the Problem of a server error', async () => {
    const fake = fakeExportServer(household());
    fake.state.failWith = { section: 'persons', page: 0, status: 500 };
    server.use(...fake.handlers);

    const running = runExport(client(), { signal: new AbortController().signal });

    await expect(running).rejects.toBeInstanceOf(ProblemError);
    await expect(running).rejects.toMatchObject({ problem: { status: 500 } });
  });

  // Review Focus 5: an unreadable cursor is a failure to show, not a change to retry.
  it('throws on 400 export.bad_cursor instead of reporting a change', async () => {
    const fake = fakeExportServer(household());
    fake.state.failWith = { section: 'subscriptions', page: 1, status: 400, code: 'export.bad_cursor' };
    server.use(...fake.handlers);

    await expect(runExport(client(), { signal: new AbortController().signal }))
      .rejects.toMatchObject({ problem: { status: 400, code: 'export.bad_cursor' } });
  });
});

describe('exportFileName', () => {
  it('uses the local date, zero-padded', () => {
    expect(exportFileName(new Date(2027, 0, 5, 23, 59))).toBe('nid-2027-01-05.nid.json');
  });
});
