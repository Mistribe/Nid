import { createMemoryHistory, createRootRoute, createRoute, createRouter, RouterProvider } from '@tanstack/react-router';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it, vi } from 'vitest';
import { detailSearch } from '@/app/router';
import { ApiContext } from '@/api/ApiProvider';
import { createApi } from '@/api/client';
import { createQueryClient } from '@/app/bootstrap';
import { QueryClientProvider } from '@tanstack/react-query';
import { server } from '@/test/server';
import { expectNoAxeViolations } from '@/test/axe';
import { expectLabelBefore } from '@/test/filters';
import { OwnerChips } from '../OwnerChips';
import { Segmented } from '../form/Segmented';
import { SelectField } from '../form/SelectField';
import { DetailTabs, useDetailTab } from './DetailTabs';
import { FormActions, FormPage } from './FormPage';
import { FilterField, FilterRow } from './FilterRow';
import { PageHeader } from './PageHeader';
import { RowMenu } from './RowMenu';
import { Section } from './Section';
import { SectionCard } from './SectionCard';
import { StatLine } from './StatLine';
import { SortableTable } from './SortableTable';
import { StepForm } from './StepForm';

const IDS = ['overview', 'values'] as const;

function TabsPage() {
  const [tab, setTab] = useDetailTab(IDS);
  return (
    <DetailTabs value={tab} onChange={setTab} tabs={[
      { id: 'overview', label: 'Vue d’ensemble', content: <p>Contenu A</p> },
      { id: 'values', label: 'Valeurs', content: <p>Contenu B</p> },
    ]} />
  );
}

async function renderTabs(path: string) {
  const root = createRootRoute();
  const detail = createRoute({ getParentRoute: () => root, path: '/investments/$id', validateSearch: detailSearch, component: TabsPage });
  const router = createRouter({ routeTree: root.addChildren([detail]), history: createMemoryHistory({ initialEntries: [path] }) });
  render(<RouterProvider router={router} />);
  await router.load();
  return router;
}

describe('page grammar', () => {
  it('useDetailTab falls back to the first tab', async () => {
    const user = userEvent.setup();
    const router = await renderTabs('/investments/x?tab=nope');

    expect(await screen.findByText('Contenu A')).toBeVisible();
    await user.click(screen.getByRole('tab', { name: 'Valeurs' }));
    expect(await screen.findByText('Contenu B')).toBeVisible();
    expect(router.state.location.search).toEqual({ tab: 'values' });

    await user.click(screen.getByRole('tab', { name: 'Vue d’ensemble' }));
    await waitFor(() => expect(router.state.location.search).toEqual({}));
  });

  it('useDetailTab keeps the scroll: a tab low on the page stays under the pointer', async () => {
    const user = userEvent.setup();
    const router = await renderTabs('/investments/x');
    const navigate = vi.spyOn(router, 'navigate');
    await user.click(await screen.findByRole('tab', { name: 'Valeurs' }));

    await waitFor(() => expect(router.state.location.search).toEqual({ tab: 'values' }));
    expect(navigate).toHaveBeenCalledWith(expect.objectContaining({ resetScroll: false }));
  });

  it('StepForm stops on the first invalid step', async () => {
    const user = userEvent.setup();
    const trigger = vi.fn<(fields: string[]) => Promise<boolean>>().mockResolvedValueOnce(false).mockResolvedValue(true);
    const onSubmit = vi.fn();
    const { container } = render(
      <StepForm trigger={trigger} onSubmit={onSubmit} submitLabel="Créer" steps={[
        { id: 'a', title: 'Identité', fields: ['name'], content: <input aria-label="Nom" aria-invalid="true" /> },
        { id: 'b', title: 'Détails', fields: ['note'], content: <input aria-label="Note" /> },
      ]} />,
    );

    await user.click(screen.getByRole('button', { name: 'Continuer' }));
    expect(trigger).toHaveBeenCalledWith(['name']);
    await waitFor(() => expect(screen.getByLabelText('Nom')).toHaveFocus());
    expect(screen.queryByLabelText('Note')).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Continuer' }));
    expect(await screen.findByLabelText('Note')).toBeInTheDocument();
    expect(onSubmit).not.toHaveBeenCalled();
    await expectNoAxeViolations(container);

    await user.click(screen.getByRole('button', { name: 'Créer' }));
    expect(onSubmit).toHaveBeenCalledTimes(1);
  });

  it('SortableTable sorts by the column clicked', async () => {
    const user = userEvent.setup();
    const rows = [{ id: '1', name: 'Cécile', n: 2 }, { id: '2', name: 'Alice', n: 3 }, { id: '3', name: 'Bob', n: 1 }];
    const { container } = render(
      <SortableTable caption="Personnes" rows={rows} rowKey={(r) => r.id} mobile={(r) => <span>{r.name}</span>} columns={[
        { id: 'name', header: 'Nom', cell: (r) => r.name, sort: (r) => r.name },
        { id: 'n', header: 'Nombre', cell: (r) => r.n, sort: (r) => r.n, align: 'end' },
      ]} />,
    );
    const table = screen.getByRole('table', { name: 'Personnes' });
    const order = () => within(table).getAllByRole('row').slice(1).map((row) => within(row).getAllByRole('cell')[0]!.textContent);

    await user.click(within(table).getByRole('button', { name: 'Nom' }));
    expect(order()).toEqual(['Alice', 'Bob', 'Cécile']);
    expect(within(table).getByRole('columnheader', { name: 'Nom' })).toHaveAttribute('aria-sort', 'ascending');
    await user.click(within(table).getByRole('button', { name: 'Nom' }));
    expect(order()).toEqual(['Cécile', 'Bob', 'Alice']);
    expect(within(table).getByRole('columnheader', { name: 'Nom' })).toHaveAttribute('aria-sort', 'descending');
    await user.click(within(table).getByRole('button', { name: 'Nombre' }));
    expect(order()).toEqual(['Bob', 'Cécile', 'Alice']);
    expect(within(table).getByRole('columnheader', { name: 'Nom' })).not.toHaveAttribute('aria-sort', 'descending');
    await expectNoAxeViolations(container);
  });

  it('RowMenu renders nothing without items', () => {
    const { container } = render(<RowMenu label="Actions" items={[]} />);
    expect(container).toBeEmptyDOMElement();
  });

  it('RowMenu runs the chosen item', async () => {
    const user = userEvent.setup();
    const onSelect = vi.fn();
    render(<RowMenu label="Actions" items={[{ label: 'Supprimer', onSelect, destructive: true }]} />);
    await user.click(screen.getByRole('button', { name: 'Actions' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Supprimer' }));
    expect(onSelect).toHaveBeenCalledTimes(1);
  });

  it('OwnerChips names each owner for assistive technology', async () => {
    server.use(http.get('*/api/v1/core/persons', () => HttpResponse.json([
      { id: 'p1', kind: 'adult', displayName: 'Alice', email: 'a@example.test' },
      { id: 'p2', kind: 'adult', displayName: 'Bob', email: 'b@example.test' },
    ])));
    const api = createApi({ getCsrf: () => 'csrf', onUnauthenticated: vi.fn() });
    const { container } = render(
      <QueryClientProvider client={createQueryClient()}>
        <ApiContext.Provider value={api}><OwnerChips owners={[{ personId: 'p1', shareBp: 5000 }, { personId: 'p2', shareBp: 5000 }]} /></ApiContext.Provider>
      </QueryClientProvider>,
    );

    expect(await screen.findByText('Alice')).toHaveClass('sr-only');
    expect(screen.getByText('Bob')).toBeInTheDocument();
    expect(screen.getByText('50 %')).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('OwnerChips names the share of each owner when the shares differ', async () => {
    server.use(http.get('*/api/v1/core/persons', () => HttpResponse.json([
      { id: 'p1', kind: 'adult', displayName: 'Alice', email: 'a@example.test' },
      { id: 'p2', kind: 'adult', displayName: 'Bob', email: 'b@example.test' },
    ])));
    const api = createApi({ getCsrf: () => 'csrf', onUnauthenticated: vi.fn() });
    const { container } = render(
      <QueryClientProvider client={createQueryClient()}>
        <ApiContext.Provider value={api}><OwnerChips owners={[{ personId: 'p1', shareBp: 6000 }, { personId: 'p2', shareBp: 4000 }]} /></ApiContext.Provider>
      </QueryClientProvider>,
    );

    expect(await screen.findByText('Alice 60 %')).toHaveClass('sr-only');
    expect(screen.getByText('Bob 40 %')).toHaveClass('sr-only');
    await expectNoAxeViolations(container);
  });

  it('FormPage starts at the left like every page, without a card', () => {
    render(<FormPage crumbs={undefined} title="Nouveau" width="max-w-2xl"><input aria-label="Nom" /></FormPage>);
    const article = screen.getByRole('article');
    expect(article).toHaveClass('max-w-2xl', 'w-full');
    expect(article).not.toHaveClass('mx-auto');
    expect(article.firstElementChild).toContainElement(screen.getByRole('heading', { level: 1 }));
    expect(article.lastElementChild).toBe(screen.getByLabelText('Nom'));
    expect(article.querySelector('.rounded-xl, .border, .bg-card, .shadow-card')).toBeNull();
  });

  it('the form keeps its buttons at its foot, set apart', () => {
    render(<FormActions>ok</FormActions>);
    expect(screen.getByText('ok')).toHaveClass('border-t', 'pt-4', 'justify-end');
  });

  it('PageHeader titles in the large title style', () => {
    render(<PageHeader title="Placements" />);
    const h1 = screen.getByRole('heading', { level: 1, name: 'Placements' });
    expect(h1).toHaveClass('text-large-title', 'break-words');
    expect(h1).not.toHaveClass('text-2xl');
  });

  it.each([['Section', Section], ['SectionCard', SectionCard]] as const)('%s titles are level-2 headings in the title style', (_name, Component) => {
    render(<Component title="Répartition par type">contenu</Component>);
    const heading = screen.getByRole('heading', { level: 2, name: 'Répartition par type' });
    expect(heading).toHaveClass('text-title');
    expect(screen.getByRole('region', { name: 'Répartition par type' })).toBeInTheDocument();
  });

  it('a SectionCard nested in a Section titles at level 3', () => {
    render(<SectionCard title="Alice" level={3}>contenu</SectionCard>);
    expect(screen.getByRole('heading', { level: 3, name: 'Alice' })).toHaveClass('text-title');
  });

  it('StatLine figures carry no colour of their own', () => {
    render(<StatLine items={[
      { label: 'Gain', value: '+5 €' },
      // @ts-expect-error a figure takes no tone: its sense shows in its arrow
      { label: 'Perte', value: '-5 €', tone: 'down' },
    ]} />);
    const dd = screen.getByText('+5 €');
    expect(dd).toHaveClass('text-figure');
    expect(dd.className).not.toMatch(/text-status-|text-destructive/);
    expect(screen.getByText('-5 €').className).not.toMatch(/text-status-|text-destructive/);
  });

  it('StatLine labels wrap, never truncate', () => {
    render(<StatLine items={[{ label: 'Valeur estimée au 20 oct. 2026', value: '1 000 €' }]} />);
    expect(screen.getByText('Valeur estimée au 20 oct. 2026').className).not.toMatch(/truncate|whitespace-nowrap|line-clamp/);
  });

  it('StatLine figures and labels use the scale', () => {
    render(<StatLine items={[{ label: 'Valeur', value: '1 000 €' }]} />);
    expect(screen.getByText('1 000 €')).toHaveClass('text-figure');
    expect(screen.getByText('Valeur')).toHaveClass('text-label');
  });

  it('FilterRow is one labelled group of controls on a wrapping line', () => {
    render(<FilterRow label="Filtres"><button type="button">A</button></FilterRow>);
    expect(screen.getByRole('group', { name: 'Filtres' })).toHaveClass('flex', 'flex-wrap', 'items-center');
  });

  it('FilterField puts its label before its control, not above', () => {
    render(<FilterField label="Période">{(id) => <div role="radiogroup" aria-labelledby={id} />}</FilterField>);
    const control = screen.getByRole('radiogroup', { name: 'Période' });
    const label = screen.getByText('Période');
    expectLabelBefore(label, control);
  });

  it('clicking a FilterField label focuses its control', async () => {
    render(<FilterField label="Recherche" labelFor>{(labelId, controlId) => <input id={controlId} aria-labelledby={labelId} />}</FilterField>);
    await userEvent.click(screen.getByText('Recherche'));
    expect(screen.getByRole('textbox', { name: 'Recherche' })).toHaveFocus();
  });

  // A Radix select would open on a label click (its pointer type starts as touch), a segmented control has no element to label.
  it('a FilterField without labelFor names its control by aria-labelledby only', () => {
    render(<>
      <FilterField label="Type">{(labelId) => <SelectField aria-labelledby={labelId} value="" onValueChange={() => {}} options={[{ value: '', label: 'Tous' }]} />}</FilterField>
      <FilterField label="Période">{(labelId) => <Segmented aria-labelledby={labelId} value="a" onValueChange={() => {}} options={[{ value: 'a', label: 'A' }]} />}</FilterField>
    </>);
    expect(screen.getByRole('combobox', { name: 'Type' })).toBeInTheDocument();
    expect(screen.getByRole('radiogroup', { name: 'Période' })).toBeInTheDocument();
    expect(screen.getByText('Type')).not.toHaveAttribute('for');
    expect(screen.getByText('Période')).not.toHaveAttribute('for');
  });
});
