import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import '@/locales/tax';
import { TaxNotices } from './TaxNotices';

describe('TaxNotices', () => {
  it('says the advice once, folds past five rows and shows the rest on demand', async () => {
    const notices = Array.from({ length: 7 }, (_, i) => ({ code: 'other_currency', item: `i${i}`, name: `Compte ${i}`, amount: '1.00' }));
    render(<TaxNotices notices={notices} year={2026} />);
    expect(screen.getAllByText('Revenus hors euro : non comptés.')).toHaveLength(1);
    expect(screen.queryByText('Compte 6')).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Afficher les 2 autres' }));
    expect(screen.getByText('Compte 5')).toBeVisible();
    expect(screen.getByText('Compte 6')).toBeVisible();
    expect(screen.getAllByText('Revenus hors euro : non comptés.')).toHaveLength(1);
  });
});
