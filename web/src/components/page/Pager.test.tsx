import { createMemoryHistory, createRootRoute, createRoute, createRouter, RouterProvider } from '@tanstack/react-router';
import { render, renderHook, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { pageOf } from '@/lib/search';
import { Pager, useListPage, useStepBack } from './Pager';

describe('Pager', () => {
  it('is hidden when everything fits on one page', () => {
    render(<Pager page={1} total={50} onPage={() => {}} />);
    expect(screen.queryByRole('navigation')).toBeNull();
  });

  it('says where it is and steps back', async () => {
    const onPage = vi.fn();
    render(<Pager page={2} total={101} onPage={onPage} />);
    expect(screen.getByRole('navigation', { name: 'Pages' })).toBeVisible();
    expect(screen.getByText('Page 2 sur 3')).toBeVisible();
    await userEvent.setup().click(screen.getByRole('button', { name: 'Précédent' }));
    expect(onPage).toHaveBeenCalledWith(1);
  });

  it('disables « Suivant » on the last page', () => {
    render(<Pager page={3} total={101} onPage={() => {}} />);
    expect(screen.getByRole('button', { name: 'Suivant' })).toBeDisabled();
  });

  it('reads the page of the URL: a whole number from 2', () => {
    expect([pageOf({ page: 3 }), pageOf({ page: '4' }), pageOf({ page: 1 }), pageOf({ page: 'x' }), pageOf({ page: 2.5 }), pageOf({})])
      .toEqual([3, 4, undefined, undefined, undefined, undefined]);
  });

  it('steps back once to the last page when the page is past the end', () => {
    const toPage = vi.fn();
    renderHook(() => useStepBack(5, { items: [], total: 101 }, toPage));
    expect(toPage.mock.calls).toEqual([[3]]);
  });

  it('does not step back on the last page itself', () => {
    const toPage = vi.fn();
    renderHook(() => useStepBack(3, { items: [], total: 101 }, toPage));
    expect(toPage).not.toHaveBeenCalled();
  });

  it('steps back to the first page when the whole list emptied', () => {
    const toPage = vi.fn();
    renderHook(() => useStepBack(2, { items: [], total: 0 }, toPage));
    expect(toPage.mock.calls).toEqual([[1]]);
  });
});

describe('useListPage', () => {
  function Probe() {
    const [page, toPage] = useListPage();
    return <><span>page {page}</span><button onClick={() => toPage(3)}>three</button><button onClick={() => toPage(1)}>one</button></>;
  }
  it('sets page from 2 and drops it on 1', async () => {
    const user = userEvent.setup();
    const root = createRootRoute();
    const route = createRoute({ getParentRoute: () => root, path: '/', validateSearch: (s: Record<string, unknown>) => ({ page: pageOf(s) }), component: Probe });
    const router = createRouter({ routeTree: root.addChildren([route]), history: createMemoryHistory({ initialEntries: ['/'] }) });
    render(<RouterProvider router={router} />);
    await user.click(await screen.findByRole('button', { name: 'three' }));
    await waitFor(() => expect(router.state.location.search).toEqual({ page: 3 }));
    expect(screen.getByText('page 3')).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'one' }));
    await waitFor(() => expect(router.state.location.search).toEqual({}));
  });
});
