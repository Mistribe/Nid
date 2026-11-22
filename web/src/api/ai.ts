import type { TFunction } from 'i18next';
import { present, toProblem, type Presentation } from './errors';
import type { components } from './schema';
import '@/locales/ai';

export type Worker = components['schemas']['WorkerView'];
export type ModelJob = components['schemas']['ModelJobListItem'];

export const WORKERS = '/api/v1/ai/workers' as const;
export const WORKER = '/api/v1/ai/workers/{id}' as const;
export const WORKER_PAIR = '/api/v1/ai/workers/{id}/pair' as const;
export const WORKER_TEST = '/api/v1/ai/workers/{id}/test' as const;
export const WORKER_SAFE = '/api/v1/ai/workers/{id}/safe' as const;
/** The row menu's admin actions without a body, each answering the worker as it now stands. */
export const WORKER_ACTIONS = {
  refresh: '/api/v1/ai/workers/{id}/refresh',
  disable: '/api/v1/ai/workers/{id}/disable',
  enable: '/api/v1/ai/workers/{id}/enable',
  rotate: '/api/v1/ai/workers/{id}/rotate-key',
  revoke: '/api/v1/ai/workers/{id}/revoke',
} as const;
export const NID_KEY_ROTATE = '/api/v1/ai/key/rotate' as const;
export const AI_SETTINGS = '/api/v1/ai/settings' as const;
export const REMOTE_WORKERS = '/api/v1/ai/settings/remote-workers' as const;
export const JOBS = '/api/v1/ai/jobs' as const;

/** The statuses the jobs page filters on (the server refuses any other). */
export const JOB_STATUSES = ['queued', 'running', 'succeeded', 'failed', 'dead'] as const;

/** A worker's or a job's `ai.*` code in words; a code not named here keeps the server's sentence. */
export const aiReason = (t: TFunction, code: string | null | undefined): string | null =>
  code ? t(`ai.reason.${code.replace(/^ai\./, '')}` as 'ai.reason.no_worker', { defaultValue: code }) : null;

/** A refused call: the server's `ai.*` code in words in place of its sentence. */
export function aiProblem(t: TFunction, error: unknown): Presentation {
  const problem = toProblem(error);
  const presented = present(problem);
  return problem.code?.startsWith('ai.') ? { ...presented, detail: aiReason(t, problem.code) ?? presented.detail } : presented;
}

/** A key fingerprint as the worker's console prints it, in groups of four to compare by eye. */
export const groupedFingerprint = (fingerprint: string) => fingerprint.match(/.{1,4}/g)?.join(' ') ?? fingerprint;
