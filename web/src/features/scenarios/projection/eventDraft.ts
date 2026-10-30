import type { Path } from 'react-hook-form';
import { toWireAmount } from '../../subscriptions/edits/amount';
import type { EventDate, EventKind, ProjectionEvent } from './api';

/** A date as typed: a day, or a person's age (spec §4); an end may also be none. Every figure a string, as typed. */
export type DateDraft = { mode: 'on' | 'age' | 'none'; on: string; person: string; age: string };

/** One event as its dialog types it; only the fields of its kind are sent. */
export type EventDraft = {
  label: string; start: DateDraft; until: DateDraft; monthlyAmount: string;
  amount: string; split: string; loan: string; effect: string; penalty: string;
  property: string; price: string; costs: string; scenario: string;
};

/** Today as the API reads a day (yyyy-mm-dd, local): the date a purchase event carries, which the server replaces by its own. */
export function today(): string {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

const dateDraft = (date: EventDate | null | undefined, none: boolean): DateDraft => {
  if (date?.person) return { mode: 'age', on: '', person: date.person, age: date.age == null ? '' : String(date.age) };
  if (date?.on) return { mode: 'on', on: date.on, person: '', age: '' };
  return { mode: none ? 'none' : 'on', on: '', person: '', age: '' };
};

/** The dialog's draft of an event, or a blank one (repayment defaults: highest rate first, shorter term, no penalty). */
export function fromEvent(event?: ProjectionEvent): EventDraft {
  return {
    label: event?.label ?? '', start: dateDraft(event?.start, false), until: dateDraft(event?.until, true),
    monthlyAmount: event?.monthlyAmount ?? '', amount: event?.amount ?? '', split: event?.split ?? 'highest_rate', loan: event?.loan ?? '',
    effect: event?.effect ?? 'reduce_term', penalty: event?.penalty ?? '0', property: event?.property ?? '', price: event?.price ?? '',
    costs: event?.costs ?? '0', scenario: event?.scenario ?? '',
  };
}

// An age is a count of years, not an amount.
const wireDate = (d: DateDraft): EventDate => (d.mode === 'age' ? { person: d.person, age: Number.parseInt(d.age, 10) } : { on: d.on });

/**
 * The wire event (spec §4): the fields of its kind only, the server refusing any other; amounts as typed with a dot. A purchase keeps
 * the date it had, or today: the server dates it by the purchase scenario's own.
 */
export function toEvent(kind: EventKind, d: EventDraft, previous?: ProjectionEvent): ProjectionEvent {
  const id = previous?.id ? { id: previous.id } : {};
  switch (kind) {
    case 'income_change':
    case 'savings_change':
      return {
        ...id, kind, ...(d.label.trim() ? { label: d.label.trim() } : {}), start: wireDate(d.start),
        ...(d.until.mode === 'none' ? {} : { until: wireDate(d.until) }), monthlyAmount: toWireAmount(d.monthlyAmount),
      };
    case 'early_repayment':
      return {
        ...id, kind, start: wireDate(d.start), amount: toWireAmount(d.amount), split: d.split,
        ...(d.split === 'chosen' ? { loan: d.loan } : {}), effect: d.effect, penalty: toWireAmount(d.penalty),
      };
    case 'property_sale':
      return {
        ...id, kind, property: d.property, start: wireDate(d.start),
        ...(d.price.trim() ? { price: toWireAmount(d.price) } : {}), costs: toWireAmount(d.costs),
      };
    case 'property_purchase':
      return { ...id, kind, scenario: d.scenario, start: previous?.start ?? { on: today() } };
  }
}

/**
 * A server path inside the event (`amount`, `start.age`, `until`) as the draft's field, when the dialog shows one; a bare date
 * lands on the part its mode shows.
 */
export function eventField(path: string, d: EventDraft): Path<EventDraft> | undefined {
  const [root, part] = path.split('.');
  if (root === 'start' || root === 'until') {
    const date = d[root];
    if (part === 'on' || part === 'person' || part === 'age') return `${root}.${part}`;
    return date.mode === 'age' ? `${root}.age` : `${root}.on`;
  }
  const fields = ['label', 'monthlyAmount', 'amount', 'split', 'loan', 'effect', 'penalty', 'property', 'price', 'costs', 'scenario'] as const;
  return fields.find((f) => f === root);
}
