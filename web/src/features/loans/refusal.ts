import type { TFunction } from 'i18next';
import { toProblem, type Presentation } from '@/api/errors';
import { presentConflict } from '../investments/entries/entries';
import { knownConflictMessage } from '../subscriptions/fieldErrors';

/**
 * A loan write's refusal no field owns, in French as the settings form reads it: a conflict by its own sentence, a 422
 * by its code's (`realestate.payment_below_interest`…); anything else as `present` reads it. Kept out of `format.ts`,
 * which the eager list imports.
 */
export function presentRefusal(t: TFunction, error: unknown): Presentation {
  const base = presentConflict(t, error);
  return base.kind === 'validation' ? { ...base, detail: knownConflictMessage(t, toProblem(error).code) } : base;
}
