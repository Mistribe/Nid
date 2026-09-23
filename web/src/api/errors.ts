/** A Problem Details body (RFC 9457) as the API sends it: `code` is the stable identifier. */
export type Problem = { status: number; code?: string | null; title?: string | null; traceId?: string | null };

export type ProblemKind = 'unauthenticated' | 'not_found' | 'forbidden' | 'conflict' | 'validation' | 'bad_request' | 'unexpected';

export type Presentation = { kind: ProblemKind; detail?: string; requestId?: string };

export const isProblem = (value: unknown): value is Problem =>
  typeof value === 'object' && value !== null && typeof (value as { status?: unknown }).status === 'number';

/** Carries the Problem the API answered through a `throw`, where only an Error may travel. */
export class ProblemError extends Error {
  constructor(readonly problem: Problem) {
    super(`The API answered ${problem.status}.`);
    this.name = 'ProblemError';
  }
}

/** Anything that is not a Problem Details body — a network failure, a thrown error — is status 0. */
export const toProblem = (error: unknown): Problem =>
  error instanceof ProblemError ? error.problem : isProblem(error) ? error : { status: 0 };

/** The one place a failure becomes what the user reads (frontend/architecture.md §5). */
export function present(problem: Problem): Presentation {
  const requestId = problem.traceId ?? undefined;
  const detail = problem.title ?? undefined;
  switch (problem.status) {
    case 401: return { kind: 'unauthenticated', requestId };
    case 403: return { kind: 'forbidden', detail, requestId };
    case 404: return { kind: 'not_found', requestId }; // never why: absent and invisible look the same
    case 409: return { kind: 'conflict', requestId };
    case 422: return { kind: 'validation', detail, requestId };
    case 400: return { kind: 'bad_request', detail, requestId };
    default: return { kind: 'unexpected', requestId };
  }
}
