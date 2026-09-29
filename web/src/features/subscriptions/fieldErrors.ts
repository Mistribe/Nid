import type { TFunction } from 'i18next';
import { toProblem, type FieldError } from '@/api/errors';
import fr from '@/locales/fr.json';

/** 1E2a design §2.1: the fields a 422 names; nothing for any other failure. */
export function fieldErrorsOf(error: unknown): FieldError[] {
  const problem = toProblem(error);
  return problem.status === 422 && Array.isArray(problem.errors) ? problem.errors : [];
}

type FieldErrorKey = keyof typeof fr.field_error;

/** A server code as a translation key: its dots would read as nesting (`request.amount` → `request_amount`). */
const keyOf = (code: string) => code.replaceAll('.', '_');
const isKnown = (key: string): key is FieldErrorKey => Object.hasOwn(fr.field_error, key);

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
