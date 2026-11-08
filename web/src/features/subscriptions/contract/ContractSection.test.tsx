import { render, screen, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import '@/app/i18n';
import '@/locales/subscriptions';
import type { Detail } from '@/api/subscriptions';
import { detail } from '../testData';
import { ContractSection } from './ContractSection';

const TERMS = { anniversaryOn: '2026-12-01', notice: { count: 2, unit: 'month' }, commitmentEndsOn: null, cancellableFrom: null };

function show(over: Partial<Detail>) {
  render(<ContractSection detail={detail(over)} />);
}

describe('ContractSection', () => {
  it('renders nothing without contract terms', () => {
    const { container } = render(<ContractSection detail={detail({ contractTerms: null })} />);
    expect(container).toBeEmptyDOMElement();
  });

  it('lists the terms and says the last day to cancel', () => {
    show({ contractTerms: TERMS, cancellation: { status: 'deadline', lastDayToCancel: '2026-10-01', effectiveOn: '2026-12-01', freeOn: null } });
    const region = screen.getByRole('region', { name: 'Contrat' });
    expect(within(region).getByText('Date anniversaire').nextElementSibling).toHaveTextContent('1 décembre 2026');
    expect(within(region).getByText('Préavis').nextElementSibling).toHaveTextContent('2 mois');
    expect(region).toHaveTextContent('Pour une résiliation effective le 1 décembre 2026, le dernier jour pour résilier est le 1 octobre 2026.');
  });

  it('says when it is free to cancel from a date', () => {
    show({
      contractTerms: { anniversaryOn: null, notice: null, commitmentEndsOn: null, cancellableFrom: '2027-03-01' },
      cancellation: { status: 'free_from', lastDayToCancel: null, effectiveOn: null, freeOn: '2027-03-01' },
    });
    expect(screen.getByRole('region', { name: 'Contrat' })).toHaveTextContent('Résiliable à tout moment à partir du 1 mars 2027.');
  });

  it('says it is free to cancel now', () => {
    show({
      contractTerms: { anniversaryOn: null, notice: null, commitmentEndsOn: '2026-01-01', cancellableFrom: null },
      cancellation: { status: 'free_now', lastDayToCancel: null, effectiveOn: null, freeOn: null },
    });
    expect(screen.getByRole('region', { name: 'Contrat' })).toHaveTextContent('Résiliable à tout moment.');
  });
});
