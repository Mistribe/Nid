import { describe, expect, it } from 'vitest';
import type { Problem } from '@/api/errors';
import type { PreparedBatch } from './prepare';
import { RETRY_DELAYS_MS, sendBatches, type BatchView, type PutResult } from './sender';

const batch = (section: string, index: number): PreparedBatch => ({ section, index, count: 1, hash: `h-${section}-${index}`, body: '{}' });
const batches = [batch('persons', 0), batch('subscriptions', 0), batch('subscriptions', 1)];
const accepted = (b: PreparedBatch, issues: BatchView['issues'] = []): PutResult => ({
  ok: true, view: { section: b.section, index: b.index, count: b.count, accepted: issues.length === 0, issues },
});
const failed = (problem: Problem): PutResult => ({ ok: false, problem });

/** A put that answers from a script, one entry per call, then accepts. */
function scripted(script: Array<(b: PreparedBatch) => PutResult> = []) {
  const calls: string[] = [];
  const put = (b: PreparedBatch) => {
    calls.push(`${b.section}/${b.index}`);
    return Promise.resolve((script.shift() ?? accepted)(b));
  };
  return { put, calls };
}

const noSleep = { sleep: () => Promise.resolve() };

describe('sendBatches', () => {
  it('sends every batch in order and counts them', async () => {
    const { put, calls } = scripted();
    const sent: number[] = [];
    const outcome = await sendBatches({ batches, start: 0, put, onSent: (n) => sent.push(n), ...noSleep });
    expect(outcome).toEqual({ kind: 'done', refused: [] });
    expect(calls).toEqual(['persons/0', 'subscriptions/0', 'subscriptions/1']);
    expect(sent).toEqual([1, 2, 3]);
  });

  it('retries a 5xx and a network failure three times, waiting longer each time', async () => {
    const { put, calls } = scripted([() => failed({ status: 503 }), () => failed({ status: 0 }), () => failed({ status: 502 })]);
    const waited: number[] = [];
    const outcome = await sendBatches({
      batches: batches.slice(0, 1), start: 0, put, onSent: () => {}, sleep: (ms) => { waited.push(ms); return Promise.resolve(); },
    });
    expect(outcome.kind).toBe('done');
    expect(calls).toEqual(['persons/0', 'persons/0', 'persons/0', 'persons/0']);
    expect(waited).toEqual([...RETRY_DELAYS_MS]);
  });

  it('gives up after three retries, on the batch that failed', async () => {
    const { put, calls } = scripted(Array.from({ length: 4 }, () => () => failed({ status: 503 })));
    const outcome = await sendBatches({ batches, start: 0, put, onSent: () => {}, ...noSleep });
    expect(outcome).toEqual({ kind: 'failed', problem: { status: 503 }, refused: [] });
    expect(calls).toHaveLength(4);
  });

  it('never retries a 4xx', async () => {
    const conflict = { status: 409, code: 'import.slot_taken', title: 'taken' };
    const { put, calls } = scripted([() => failed(conflict)]);
    const outcome = await sendBatches({ batches, start: 0, put, onSent: () => {}, ...noSleep });
    expect(outcome).toEqual({ kind: 'failed', problem: conflict, refused: [] });
    expect(calls).toEqual(['persons/0']);
  });

  it('collects refused batches and goes on, so every issue is seen at once', async () => {
    const issue = { severity: 'error' as const, code: 'import.invalid_value', message: 'bad', path: 'subscriptions[0].name' };
    const { put, calls } = scripted([accepted, (b) => accepted(b, [issue])]);
    const outcome = await sendBatches({ batches, start: 0, put, onSent: () => {}, ...noSleep });
    expect(outcome).toEqual({ kind: 'done', refused: [{ section: 'subscriptions', index: 0, count: 1, accepted: false, issues: [issue] }] });
    expect(calls).toHaveLength(3);
  });

  it('pauses between batches and resumes where it stopped', async () => {
    const { put, calls } = scripted();
    let pause = false;
    let sent = 0;
    const first = await sendBatches({
      batches, start: 0, put, shouldPause: () => pause, onSent: (n) => { sent = n; pause = n === 1; }, ...noSleep,
    });
    expect(first).toEqual({ kind: 'paused', refused: [] });
    expect(calls).toEqual(['persons/0']);

    pause = false;
    const second = await sendBatches({ batches, start: sent, put, onSent: (n) => { sent = n; }, ...noSleep });
    expect(second.kind).toBe('done');
    expect(calls).toEqual(['persons/0', 'subscriptions/0', 'subscriptions/1']);
    expect(sent).toBe(3);
  });
});
