import { createMemoryHistory, createRootRoute, createRouter, RouterProvider } from '@tanstack/react-router';
import { act, render, screen } from '@testing-library/react';
import { useState, type ReactNode } from 'react';
import { describe, expect, it } from 'vitest';
import { PageHeader } from './PageHeader';
import { TrailProvider, useTrail } from './trail';

function Probe() {
  return <output data-testid="trail">{JSON.stringify(useTrail())}</output>;
}

let setUi: (ui: ReactNode) => void = () => {};
function Harness() {
  const [ui, set] = useState<ReactNode>(null);
  setUi = set;
  return <TrailProvider><Probe />{ui}</TrailProvider>;
}

async function mount(ui: ReactNode) {
  const root = createRootRoute({ component: Harness });
  const router = createRouter({ routeTree: root, history: createMemoryHistory({ initialEntries: ['/'] }) });
  render(<RouterProvider router={router} />);
  await screen.findByTestId('trail');
  act(() => setUi(ui));
}
const trail = () => JSON.parse(screen.getByTestId('trail').textContent) as { crumbs?: { label: string }[]; current?: string } | null;
const crumbs = [{ label: 'Placements', to: '/investments' as const }];

describe('trail', () => {
  it('a page header hands its crumbs and its title to the trail', async () => {
    await mount(<PageHeader crumbs={crumbs} title="PEA Boursorama" />);
    expect(trail()?.crumbs?.[0]?.label).toBe('Placements');
    expect(trail()?.current).toBe('PEA Boursorama');
  });

  it('a title that is not text needs crumbLabel', async () => {
    await mount(<PageHeader title={<span>job-42</span>} crumbLabel="job-42" />);
    expect(trail()?.current).toBe('job-42');
    act(() => setUi(<PageHeader title={<span>job-42</span>} />));
    expect(trail()?.current).toBeUndefined();
  });

  it('unmounting the header clears its trail', async () => {
    await mount(<PageHeader title="A" />);
    expect(trail()?.current).toBe('A');
    act(() => setUi(null));
    expect(trail()).toBeNull();
  });

  it('a later header wins, and the earlier one leaving keeps it', async () => {
    await mount(<><div key="a"><PageHeader title="A" /></div><PageHeader key="b" title="B" /></>);
    expect(trail()?.current).toBe('B');
    act(() => setUi(<PageHeader key="b" title="B" />));
    expect(trail()?.current).toBe('B');
  });

  it('a fresh but equal crumbs array does not loop', async () => {
    let renders = 0;
    function Counted() { renders++; return <PageHeader crumbs={[{ label: 'Placements', to: '/investments' }]} title="X" />; }
    await mount(<Counted />);
    const before = renders;
    act(() => setUi(<Counted />));
    expect(renders - before).toBeLessThan(5);
    expect(trail()?.current).toBe('X');
  });

  it('without a provider the header renders and registers nothing', async () => {
    const root = createRootRoute({ component: () => <PageHeader title="X" /> });
    render(<RouterProvider router={createRouter({ routeTree: root, history: createMemoryHistory({ initialEntries: ['/'] }) })} />);
    expect((await screen.findByRole('heading', { level: 1 })).textContent).toBe('X');
  });

  it('the header draws no breadcrumb itself', async () => {
    await mount(<PageHeader crumbs={crumbs} title="PEA Boursorama" />);
    const header = document.querySelector('header[data-page-header]')!;
    expect(header.querySelector('nav')).toBeNull();
  });
});
