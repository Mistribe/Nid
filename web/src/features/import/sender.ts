import type { Problem } from '@/api/errors';
import type { components } from '@/api/schema';
import type { PreparedBatch } from './prepare';

export type BatchView = components['schemas']['BatchView'];
export type PutResult = { ok: true; view: BatchView } | { ok: false; problem: Problem };
export type SendOutcome =
  | { kind: 'done'; refused: BatchView[] }
  | { kind: 'paused'; refused: BatchView[] }
  | { kind: 'failed'; problem: Problem; refused: BatchView[] };

/** Three retries with a growing delay, on network errors and 5xx only (web design §6.2, step 4). */
export const RETRY_DELAYS_MS: readonly number[] = [1000, 2000, 4000];

const retryable = (problem: Problem): boolean => problem.status === 0 || problem.status >= 500;
const wait = (ms: number) => new Promise<void>((resolve) => { setTimeout(resolve, ms); });

export type SendOptions = {
  batches: readonly PreparedBatch[];
  /** Index in `batches` of the first batch to send: 0, or where a pause or a failure stopped. */
  start: number;
  put: (batch: PreparedBatch) => Promise<PutResult>;
  /** How many batches, counted from index 0, have been answered. */
  onSent: (sent: number) => void;
  onRetry?: (attempt: number, delayMs: number) => void;
  /** Asked between batches, never during a request. */
  shouldPause?: () => boolean;
  delays?: readonly number[];
  sleep?: (ms: number) => Promise<void>;
};

/**
 * Sends batches one at a time, in order. A batch the server refuses (`accepted: false`) is collected and sending
 * goes on, so every batch's issues are seen at once; a request that fails stops at that batch.
 */
export async function sendBatches(options: SendOptions): Promise<SendOutcome> {
  const delays = options.delays ?? RETRY_DELAYS_MS;
  const sleep = options.sleep ?? wait;
  const refused: BatchView[] = [];

  for (let i = options.start; i < options.batches.length; i++) {
    if (options.shouldPause?.()) return { kind: 'paused', refused };
    const batch = options.batches[i]!;

    let result = await options.put(batch);
    for (let attempt = 0; !result.ok && retryable(result.problem) && attempt < delays.length; attempt++) {
      const delay = delays[attempt]!;
      options.onRetry?.(attempt + 1, delay);
      await sleep(delay);
      result = await options.put(batch);
    }

    if (!result.ok) return { kind: 'failed', problem: result.problem, refused };
    if (!result.view.accepted) refused.push(result.view);
    options.onSent(i + 1);
  }
  return { kind: 'done', refused };
}
