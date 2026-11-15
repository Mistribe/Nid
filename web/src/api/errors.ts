/** One refused field of a 422 (1E2a design §2.1): its camelCase body path, a stable code and the server's sentence. */
export type FieldError = { path: string; code: string; message: string };

/**
 * A Problem Details body (RFC 9457), read: `code` is the stable identifier, `detail` the server's sentence about this
 * failure (`title` only names the status), `errors` the refused fields with their paths.
 */
export type Problem = { status: number; code?: string | null; detail?: string | null; traceId?: string | null; errors?: FieldError[] | null };

/** #250: a refused field as the API sends it (RFC 9457 §3): the field as a JSON Pointer, the sentence in `detail`. */
type WireFieldError = { detail?: string | null; pointer?: string | null; code?: string | null };

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

/**
 * #250: a JSON Pointer (`#/payload/movements/3/amount`) back as the path the forms name (`payload.movements[3].amount`);
 * empty for none. A numeric token reads as an index.
 */
export function pathOf(pointer: string | null | undefined): string {
  if (!pointer) return '';
  let path = '';
  for (const raw of pointer.replace(/^#/, '').split('/').slice(1)) {
    const token = decodeURIComponent(raw).replaceAll('~1', '/').replaceAll('~0', '~');
    path += /^\d+$/.test(token) ? `[${token}]` : `${path ? '.' : ''}${token}`;
  }
  return path;
}

/**
 * A pointer cannot tell a numeric key from an index, so a field the page names by a numeric key (`inflationByYear.2026`)
 * comes back as `inflationByYear[2026]`: compare such paths with this, not `===`.
 */
export const samePath = (a: string, b: string): boolean => canonical(a) === canonical(b);

const canonical = (path: string) => path.replace(/\.(\d+)(?=[.[]|$)/g, '[$1]');

/** A refused field as the pages read it; one already read (it has a `path`) stays as it is. */
const fieldOf = (error: FieldError | WireFieldError, code: string): FieldError =>
  'path' in error
    ? error
    : { path: pathOf(error.pointer), code: error.code ?? code, message: error.detail ?? '' };

/** Anything that is not a Problem Details body — a network failure, a thrown error — is status 0. */
export function toProblem(error: unknown): Problem {
  if (error instanceof ProblemError) return error.problem;
  if (!isProblem(error)) return { status: 0 };
  const errors = error.errors as (FieldError | WireFieldError)[] | null | undefined;
  return Array.isArray(errors) ? { ...error, errors: errors.map((e) => fieldOf(e, error.code ?? '')) } : error;
}

/** The one place a failure becomes what the user reads (frontend/architecture.md §5). */
export function present(problem: Problem): Presentation {
  const requestId = problem.traceId ?? undefined;
  const detail = problem.detail ?? undefined;
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
