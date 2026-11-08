import type { TFunction } from 'i18next';
import type { FieldValues, Path, UseFormSetError } from 'react-hook-form';
import { present, toProblem, type FieldError, type Presentation } from '@/api/errors';
import forms from '@/locales/fr.forms.json';
import '@/locales/forms';

/** 1E2a design §2.1: the fields a 422 names; nothing for any other failure. */
export function fieldErrorsOf(error: unknown): FieldError[] {
  const problem = toProblem(error);
  return problem.status === 422 && Array.isArray(problem.errors) ? problem.errors : [];
}

type FieldErrorKey = keyof typeof forms.field_error;

/** A server code as a translation key: its dots would read as nesting (`request.amount` → `request_amount`). */
const keyOf = (code: string) => code.replaceAll('.', '_');
const isKnown = (key: string): key is FieldErrorKey => Object.hasOwn(forms.field_error, key);

/** A known code reads in French; an unknown one keeps the server's sentence. */
export function messageFor(t: TFunction, error: FieldError): string {
  const key = keyOf(error.code);
  return isKnown(key) ? t(`field_error.${key}`) : error.message;
}

/** A conflict's own sentence when its code is one `field_error.*` knows (final findings #5); `undefined` otherwise,
 * so the caller falls back to the generic conflict text rather than show the server's raw sentence. */
export function knownConflictMessage(t: TFunction, code: string | null | undefined): string | undefined {
  if (!code) return undefined;
  const key = keyOf(code);
  return isKnown(key) ? t(`field_error.${key}`) : undefined;
}

/** The wizard step that owns a path, so an error on an earlier step brings the user back to it. */
export function stepOf(path: string, steps: Record<number, string[]>): number | undefined {
  for (const [step, prefixes] of Object.entries(steps))
    if (prefixes.some((prefix) => path === prefix || path.startsWith(`${prefix}.`) || path.startsWith(`${prefix}[`))) return Number(step);
  return undefined;
}

/** What `useSubmitErrors` gives back — named here so `placeFieldError` doesn't need to import the hook itself. */
type ErrorPlacement = { onError: (error: unknown) => void; clear: () => void };

/**
 * A failed write, placed on its field (1E2a design §2.1's rule): the server's `errors[]` first; failing that, a pathless
 * Core refusal (`core.date_in_future`, `core.currency_mismatch`, `core.invalid_fact` carry no `errors[]`) attributed to
 * the one field `fallback` says it can concern; anything else is `useSubmitErrors`' own page-level `ProblemMessage`.
 */
export function placeFieldError<T extends FieldValues>(
  error: unknown, t: TFunction, setError: UseFormSetError<T>, errors: ErrorPlacement, fallback: Partial<Record<string, Path<T>>>,
): void {
  const problem = toProblem(error);
  const known = fieldErrorsOf(error);
  const field = known.length === 0 ? fallback[problem.code ?? ''] : undefined;
  if (field) {
    setError(field, { type: 'server', message: messageFor(t, { path: field, code: problem.code ?? '', message: problem.title ?? '' }) });
    errors.clear();
  } else {
    errors.onError(error);
  }
}

/**
 * A 409's own sentence when `messageFor` knows the code (final findings #5: `core.fact_retired`,
 * `core.fact_conflict`, `core.opening_exists`, `investments.envelope_closed`), shown with `Reload`; the generic
 * conflict text otherwise — never the server's raw sentence page-level. Forms and row menus read the mutation's own
 * `error` through this rather than `present(toProblem(error))` directly.
 */
export function presentConflict(t: TFunction, error: unknown): Presentation {
  const problem = toProblem(error);
  const base = present(problem);
  if (base.kind !== 'conflict') return base;
  const detail = knownConflictMessage(t, problem.code);
  return detail ? { ...base, detail } : base;
}
