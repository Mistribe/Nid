import type { EstateValues } from './EstateForm';
import type { RoutineValues } from './RoutineForm';

type Fields = Record<string, unknown>;

const listOf = (v: unknown, name: string, ok: (line: Fields) => boolean) => {
  const lines = (v as Fields | null)?.[name];
  return Array.isArray(lines) && lines.every((l: Fields | null) => l !== null && typeof l === 'object' && ok(l));
};
const texts = (l: Fields, ...keys: string[]) => keys.every((k) => typeof l[k] === 'string');
const ticked = (l: Fields) => texts(l, 'key', 'amount', 'date') && typeof l.checked === 'boolean';

const validRoutine = (v: unknown): v is RoutineValues => listOf(v, 'lines', (l) => texts(l, 'id', 'value', 'contribution') && typeof l.skip === 'boolean');
const validEstate = (v: unknown): v is EstateValues =>
  listOf(v, 'estimates', (l) => texts(l, 'property', 'amount')) && listOf(v, 'costs', ticked) && listOf(v, 'rents', ticked);

/** screens.md §5: a draft lives in the tab only, per month; storage may be refused (private mode) and the page works without it. */
function draftStore<T>(prefix: string, valid: (v: unknown) => v is T) {
  const key = (on: string) => `${prefix}:${on}`;
  return {
    load: (on: string): T | null => {
      try {
        const raw = sessionStorage.getItem(key(on));
        if (!raw) return null;
        const parsed: unknown = JSON.parse(raw);
        return valid(parsed) ? parsed : null;
      } catch {
        return null;
      }
    },
    save: (on: string, values: T): void => {
      try { sessionStorage.setItem(key(on), JSON.stringify(values)); } catch { /* the draft is a convenience */ }
    },
    clear: (on: string): void => {
      try { sessionStorage.removeItem(key(on)); } catch { /* nothing to clear */ }
    },
  };
}

const envelopes = draftStore('routine', validRoutine);
export const loadDraft = envelopes.load;
export const saveDraft = envelopes.save;
export const clearDraft = envelopes.clear;

/** Spec 3E2 §4: the properties' draft, beside the envelopes'. */
export const estateDraft = draftStore('routine-estate', validEstate);
