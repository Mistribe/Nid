import { render } from '@testing-library/react';
import { CalendarClock, Percent } from 'lucide-react';
import { describe, expect, it } from 'vitest';
import { event } from '../dashboard/testData';
import { EventText, eventIcon } from './events';

// #645: the server's event kinds, prefixed by their module; each reads as words.
const KINDS: [string, string | null][] = [
  ['subscriptions.renewal', null], ['subscriptions.expiry', null], ['subscriptions.cancel_deadline', null],
  ['subscriptions.trial_end', null], ['subscriptions.price_increase', null], ['income.phase_end', 'CDD'],
  ['realestate.loan_phase_end', '1'], ['realestate.loan_end', null], ['realestate.valuation_due', null],
  ['realestate.property_cost_due', 'energy'], ['realestate.tenancy_end', 'T2'], ['realestate.property_tax_due', null],
  ['investments.holding_maturity', 'Autocall'], ['investments.holding_entitlement', 'SCPI A'], ['investments.tax_milestone', 'pea_5y'],
  ['tax.per_deadline', null], ['tax.per_ceiling_expiring', null], ['tax.filing_deadline', null], ['tax.new_rate', null], ['tax.balance_due', null],
];

describe('EventText', () => {
  it.each(KINDS)('words %s', (kind, label) => {
    const { container } = render(<EventText e={event({ kind, label })} />);
    expect(container.textContent).not.toBe('');
  });

  it('gives a prefixed kind its icon', () => {
    expect(eventIcon(event({ kind: 'tax.new_rate' }))).toBe(Percent);
  });

  it('says nothing of a kind it does not know, a legacy name included', () => {
    const { container } = render(<EventText e={event({ kind: 'subscription_renewal' })} />);
    expect(container.textContent).toBe('');
    expect(eventIcon(event({ kind: 'subscription_renewal' }))).toBe(CalendarClock);
  });
});
