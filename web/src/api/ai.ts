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
export const CAPABILITIES = '/api/v1/ai/capabilities' as const;
/** #486: each use's mode (any adult) and model (an admin), and the models the workers declare. */
export const USE_MODE = '/api/v1/ai/settings/uses/{use}' as const;
export const USE_MODEL = '/api/v1/ai/settings/uses/{use}/model' as const;
export const DECLARED_MODELS = '/api/v1/ai/models' as const;
export type UseSetting = components['schemas']['UseSettingView'];
/** #487: the cloud profiles, the region, the providers' keys (the key is sent once, never read back). */
export const CLOUD = '/api/v1/ai/cloud' as const;
export const CLOUD_REGION = '/api/v1/ai/cloud/region' as const;
export const CLOUD_ENABLE = '/api/v1/ai/cloud/profiles/{id}/enable' as const;
export const CLOUD_DISABLE = '/api/v1/ai/cloud/profiles/{id}/disable' as const;
export const CLOUD_KEY = '/api/v1/ai/cloud/keys/{provider}' as const;
export type CloudProfile = components['schemas']['CloudProfileView'];
/** #488: the household's monthly budget for paid providers; amounts are the server's decimal strings. */
export const BUDGET = '/api/v1/ai/budget' as const;

/** The statuses the jobs page filters on (the server refuses any other). */
export const JOB_STATUSES = ['queued', 'running', 'succeeded', 'failed', 'dead'] as const;

/**
 * A worker's or a job's `ai.*` code in words; a code not named here keeps the server's sentence. A worker's own failure comes after
 * the code (`ai.worker_failed:oom`): the words are the code's.
 */
export const aiReason = (t: TFunction, code: string | null | undefined): string | null => {
  if (!code) return null;
  const base = code.split(':')[0]!;
  return t(`ai.reason.${base.replace(/^ai\./, '')}` as 'ai.reason.no_worker', { defaultValue: code, nsSeparator: false });
};

/** A job's type in words when Nid names it (« Test d'un worker »), else as the server gives it. */
export const jobType = (t: TFunction, type: string): string => t(`ai.job_type.${type}` as 'ai.job_type.echo', { defaultValue: type });

/** A field the server refused, in words: `missing` for an empty one, the `ai.*` code's words, else the server's sentence. */
export function fieldError(t: TFunction, error: unknown, path: string, missing: string): string | undefined {
  const refused = error ? toProblem(error).errors?.find((e) => e.path === path) : undefined;
  if (!refused) return undefined;
  return refused.code === 'request.required' ? missing : refused.code.startsWith('ai.') ? (aiReason(t, refused.code) ?? refused.message) : refused.message;
}

/** A refused call: the server's `ai.*` code in words in place of its sentence. */
export function aiProblem(t: TFunction, error: unknown): Presentation {
  const problem = toProblem(error);
  const presented = present(problem);
  return problem.code?.startsWith('ai.') ? { ...presented, detail: aiReason(t, problem.code) ?? presented.detail } : presented;
}
