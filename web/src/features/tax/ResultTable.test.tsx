import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import type { TaxResult } from '@/api/tax';
import { DeclarantTable, ResultTable, type ResultGroup } from './ResultTable';
import { result } from './taxTestData';

const table = (groups?: ResultGroup[], r: TaxResult = result()) => render(<ResultTable label="Calcul" columns={[{ title: 'Estimation', result: r }]} groups={groups} />);
const row = (name: string) => screen.getByRole('row', { name: new RegExp(name) });

describe('ResultTable', () => {
  it('renders one group without its heading row', () => {
    table(['income_tax']);
    expect(screen.getByText('Impôt au barème')).toBeInTheDocument();
    expect(screen.getByText('Impôt sur le revenu')).toBeInTheDocument();
    expect(screen.queryByText('Revenu net imposable')).toBeNull();
    expect(screen.queryByText('Barème, réductions et crédits')).toBeNull();
  });

  it('folds the zero rows of its own group', async () => {
    table(['income_tax']);
    expect(screen.queryByText('Réduction Pinel')).toBeNull();
    await userEvent.click(screen.getByRole('button', { name: 'Afficher les 4 lignes à 0 €' }));
    expect(screen.getByText('Réduction Pinel')).toBeInTheDocument();
  });

  it('keeps the reference-income note with the income group only', () => {
    const { unmount } = table(['income']);
    expect(screen.getByText(/se compare à l’avis d’imposition/)).toBeInTheDocument();
    unmount();
    table(['left']);
    expect(screen.queryByText(/se compare à l’avis d’imposition/)).toBeNull();
  });

  it('words the balance', () => {
    const { unmount } = table(['left']);
    expect(row('Solde à la déclaration')).toHaveTextContent(/2 973,39 € · Remboursé/);
    expect(screen.queryByText(/négatif/)).toBeNull();
    unmount();
    table(['left'], result({ withholding: { ...result().withholding, balance: '0.00' } }));
    expect(within(row('Solde à la déclaration')).getByText('Rien à régler')).toBeInTheDocument();
    expect(row('Solde à la déclaration')).not.toHaveTextContent('0,00 €');
  });

  it('a negative income tax reads in your favour', () => {
    table(['income_tax'], result({ incomeTax: '-500.00' }));
    expect(row('Impôt sur le revenu')).toHaveTextContent('500,00 € en votre faveur');
  });

  it('the declarant table has no « Infos » header', () => {
    render(<DeclarantTable result={result()} />);
    expect(screen.queryByRole('columnheader', { name: 'Infos' })).toBeNull();
    expect(screen.getByRole('columnheader', { name: 'Ligne' })).toBeInTheDocument();
  });
});
