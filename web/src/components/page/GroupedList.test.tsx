import { createMemoryHistory, createRootRoute, createRoute, createRouter, RouterProvider } from '@tanstack/react-router';
import { render, screen } from '@testing-library/react';
import { Landmark } from 'lucide-react';
import { describe, expect, it } from 'vitest';
import { GroupedList, GroupedRow } from './GroupedList';

async function renderList() {
  const root = createRootRoute();
  const page = createRoute({
    getParentRoute: () => root, path: '/',
    component: () => (
      <>
      <p id="watch">À surveiller</p>
      <GroupedList labelledBy="watch">
        <GroupedRow link={{ to: '/loans' }} tile={{ icon: Landmark, tile: 2 }} title="Prêt A" detail="Fin dans 3 mois" trailing="1 200 €" />
        <GroupedRow link={{ to: '/loans' }} title="Prêt B" />
      </GroupedList>
      </>
    ),
  });
  const loans = createRoute({ getParentRoute: () => root, path: '/loans', component: () => null });
  const router = createRouter({ routeTree: root.addChildren([page, loans]), history: createMemoryHistory({ initialEntries: ['/'] }) });
  render(<RouterProvider router={router} />);
  await router.load();
  await screen.findByRole('list', { name: 'À surveiller' });
}

describe('GroupedList', () => {
  it('a row is one link whose name is its whole text, tile and chevron hidden', async () => {
    await renderList();
    const link = screen.getByRole('link', { name: 'Prêt A Fin dans 3 mois 1 200 €' });
    expect(link).toHaveAttribute('href', '/loans');
    expect(link.querySelector('[data-tile]')?.closest('[aria-hidden="true"]')).not.toBeNull();
    expect(link.querySelector('svg.lucide-chevron-right')?.closest('[aria-hidden="true"]')).not.toBeNull();
    expect(screen.getAllByRole('listitem')).toHaveLength(2);
  });

  it('draws the inset separator from the list, after the tile when a row has one', async () => {
    await renderList();
    const [withTile, without] = screen.getAllByRole('listitem');
    expect(screen.getByRole('list')).toHaveClass('[&>li+li]:before:border-t', '[&>li+li]:before:border-separator');
    expect(withTile).toHaveAttribute('data-tile', '2');
    expect(withTile).toHaveClass('data-tile:before:left-[34px]');
    expect(without).not.toHaveAttribute('data-tile');
    expect(without).toHaveClass('before:left-0');
  });

  it('shows the trailing figure', async () => {
    await renderList();
    expect(screen.getByText('1 200 €')).toBeVisible();
  });

  it('wraps the title to two lines before cutting it, the amount on its first line', async () => {
    await renderList();
    expect(screen.getByText('Prêt A')).toHaveClass('line-clamp-2');
    expect(screen.getByText('Prêt A')).not.toHaveClass('truncate');
    expect(screen.getByText('1 200 €')).toHaveClass('col-start-2', 'row-start-1');
  });
});
