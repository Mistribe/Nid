import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { describe, expect, it } from 'vitest';
import { amountKey } from './sortKey';
import { SortableTable } from './SortableTable';

type Row = { id: string; name: string; amount?: string; kids?: Row[] };

const columns = [
  { id: 'name', header: 'Nom', cell: (r: Row) => r.name },
  { id: 'amount', header: 'Montant', cell: (r: Row) => r.amount ?? '', sort: (r: Row) => amountKey(r.amount), align: 'end' as const },
];

function Table({ rows, expandable = true }: { rows: Row[]; expandable?: boolean }) {
  const common = { caption: 'Lignes', rows, rowKey: (r: Row) => r.id, columns, mobile: (r: Row) => r.name };
  return expandable
    ? <SortableTable {...common} subRows={(r) => r.kids} expandLabel={(r) => `Détail ${r.name}`} />
    : <SortableTable {...common} />;
}

const names = () => within(screen.getByRole('table')).getAllByRole('row').slice(1).map((r) => within(r).getAllByRole('cell')[0]!.textContent);

const group: Row = { id: 'g', name: 'Groupe', amount: '10.00', kids: [
  { id: 'a', name: 'A', amount: '1.00' }, { id: 'b', name: 'B', amount: '5.00' }, { id: 'c', name: 'C', amount: '3.00' },
] };

describe('SortableTable', () => {
  it('Children_are_hidden_until_the_row_is_expanded', async () => {
    const user = userEvent.setup();
    render(<Table rows={[group]} />);
    const button = within(screen.getByRole('table')).getByRole('button', { name: 'Détail Groupe' });
    expect(button).toHaveAttribute('aria-expanded', 'false');
    expect(names()).toEqual(['Groupe']);

    await user.click(button);

    expect(button).toHaveAttribute('aria-expanded', 'true');
    expect(names()).toEqual(['Groupe', 'A', 'B', 'C']);
  });

  it('Children_follow_the_column_sort', async () => {
    const user = userEvent.setup();
    render(<Table rows={[group]} />);
    await user.click(within(screen.getByRole('table')).getByRole('button', { name: 'Détail Groupe' }));
    const header = screen.getByRole('button', { name: 'Montant' });
    await user.click(header);
    await user.click(header);

    expect(names()).toEqual(['Groupe', 'B', 'C', 'A']);
  });

  it('A_null_sort_key_sorts_last_both_ways', async () => {
    const user = userEvent.setup();
    render(<Table expandable={false} rows={[{ id: 'x', name: 'X' }, { id: 'a', name: 'A', amount: '1.00' }, { id: 'b', name: 'B', amount: '5.00' }]} />);
    const header = screen.getByRole('button', { name: 'Montant' });
    await user.click(header);
    expect(names()).toEqual(['A', 'B', 'X']);
    await user.click(header);
    expect(names()).toEqual(['B', 'A', 'X']);
  });

  it('Clicking_the_row_toggles_it_but_not_a_control_inside_it', async () => {
    const user = userEvent.setup();
    const cols = [...columns, { id: 'menu', header: 'Actions', cell: () => <button type="button">Menu</button> }];
    render(<SortableTable caption="Lignes" rows={[group]} rowKey={(r) => r.id} columns={cols} mobile={(r) => r.name}
      subRows={(r) => r.kids} expandLabel={(r) => `Détail ${r.name}`} />);
    const table = within(screen.getByRole('table'));

    await user.click(table.getByRole('button', { name: 'Menu' }));
    expect(names()).toEqual(['Groupe']);
    await user.click(table.getByText('10.00'));
    expect(names()).toEqual(['Groupe', 'A', 'B', 'C']);
  });

  it('A_single_child_gets_no_chevron', () => {
    render(<Table rows={[{ id: 'g', name: 'G', kids: [{ id: 'k', name: 'K' }] }]} />);
    expect(screen.queryByRole('button', { name: /Détail/ })).toBeNull();
  });

  it('Rows_without_sub_rows_render_as_before', () => {
    render(<Table expandable={false} rows={[{ id: 'a', name: 'A' }, { id: 'g', name: 'G', kids: [{ id: 'k', name: 'K' }] }]} />);
    expect(screen.queryByRole('button', { name: /Détail/ })).toBeNull();
    expect(names()).toEqual(['A', 'G']);
  });

  it('Select_all_ticks_the_selectable_rows_and_leaves_the_others', async () => {
    const user = userEvent.setup();
    function Selectable() {
      const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
      return (
        <>
          <SortableTable caption="Lignes" rows={[{ id: 'a', name: 'A' }, { id: 'b', name: 'B' }, { id: 'c', name: 'C' }]} rowKey={(r: Row) => r.id}
            columns={columns} mobile={(r) => r.name}
            selection={{ selected, onChange: setSelected, allLabel: 'Tout', label: (r) => `Choisir ${r.name}`, disabled: (r) => r.id === 'c' }} />
          <output>{[...selected].sort().join(',')}</output>
        </>
      );
    }
    render(<Selectable />);
    const table = screen.getByRole('table');
    const all = within(table).getByRole('checkbox', { name: 'Tout' });

    await user.click(within(table).getByRole('checkbox', { name: 'Choisir B' }));
    expect(all).toHaveAttribute('aria-checked', 'mixed');
    expect(screen.getByRole('status')).toHaveTextContent('b');

    await user.click(all);
    expect(all).toHaveAttribute('aria-checked', 'true');
    expect(screen.getByRole('status')).toHaveTextContent('a,b');
    expect(within(table).getByRole('checkbox', { name: 'Choisir C' })).toBeDisabled();

    await user.click(all);
    expect(screen.getByRole('status')).toBeEmptyDOMElement();
  });
});
