import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { Input } from '@/components/ui/input';
import { expectNoAxeViolations } from '@/test/axe';
import { DeclarantGrid, type GridRow } from './DeclarantGrid';

const ALICE = { id: 'a', displayName: 'Alice' };
const BOB = { id: 'b', displayName: 'Bob' };
const rate = (over: Partial<GridRow> = {}): GridRow => ({
  key: 'rate', label: 'Taux de cotisations', hint: 'Fiche de paie', cell: (_, c) => <Input {...c} />, ...over,
});

describe('DeclarantGrid', () => {
  it('names each field after its row and its declarant', async () => {
    const { container } = render(<DeclarantGrid label="Revenus d’activité" adults={[ALICE, BOB]} rows={[rate()]} />);

    expect(screen.getByRole('table', { name: 'Revenus d’activité' })).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: 'Taux de cotisations Alice' })).toHaveAccessibleDescription('Fiche de paie');
    expect(screen.getByRole('textbox', { name: 'Taux de cotisations Bob' })).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('ties an error to the declarant’s field only', () => {
    render(<DeclarantGrid label="Revenus d’activité" adults={[ALICE, BOB]} rows={[rate({ error: (id) => (id === 'a' ? 'Format' : undefined) })]} />);

    const alice = screen.getByRole('textbox', { name: 'Taux de cotisations Alice' });
    expect(alice).toHaveAttribute('aria-invalid', 'true');
    expect(alice).toHaveAccessibleDescription('Fiche de paie Format');
    expect(screen.getByRole('textbox', { name: 'Taux de cotisations Bob' })).not.toHaveAttribute('aria-invalid');
  });

  it('has one column per adult', () => {
    render(<DeclarantGrid label="Revenus d’activité" adults={[ALICE]} rows={[rate()]} />);

    expect(screen.getAllByRole('columnheader')).toHaveLength(2);
  });
});
