import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import type { components } from '@/api/schema';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { pick } from '@/test/controls';
import { server } from '@/test/server';
import { todayIso } from '../../subscriptions/today';
import type { PropertyDetail, PropertyFlow } from '../api';
import { categoriesOf } from '../format';
import { PROPERTY, propertyDetail } from '../testData';

type Body = components['schemas']['PropertyFlowBody'];
/** An amount as the server writes it back: two decimals. */
const written = (wire: string) => { const [whole, decimals = ''] = wire.split('.'); return `${whole}.${decimals.padEnd(2, '0')}`; };
const invalidCategory = () => HttpResponse.json(
  { status: 422, code: 'realestate.invalid_category', title: 'x', errors: [{ path: 'category', code: 'realestate.invalid_category', message: 'x' }] },
  { status: 422 });

/**
 * As the server: flows listed oldest first; a category outside its kind's refused at `category`; a correction supersedes
 * the flow (a new id, the old one retired); a retired flow answers 409 `core.fact_retired`, an unknown one 404.
 */
function serve(initial: PropertyDetail = propertyDetail()) {
  let current = initial;
  let next = 3;
  const retired = new Set<string>();
  const sent: { method: string; url: string; body?: Body }[] = [];
  const view = (id: string, b: Body): PropertyFlow =>
    ({ id, occurredOn: b.occurredOn!, kind: b.kind!, amount: written(b.amount!), category: b.category ?? null, note: b.note ?? null });
  const byDate = (flows: PropertyFlow[]) => [...flows].sort((a, b) => a.occurredOn.localeCompare(b.occurredOn));
  const outside = (b: Body) => (b.kind !== 'expense' && b.kind !== 'income') || !categoriesOf(b.kind).some((c) => c === b.category);
  const gone = (id: string) => (retired.has(id)
    ? HttpResponse.json({ status: 409, code: 'core.fact_retired', title: 'Already corrected or voided.' }, { status: 409 })
    : HttpResponse.json({ status: 404, code: 'core.fact_not_found', title: 'Not found' }, { status: 404 }));
  server.use(
    http.get('*/api/v1/realestate/properties/:id', () => HttpResponse.json(current)),
    http.post('*/api/v1/realestate/properties/:id/flows', async ({ request }) => {
      const body = (await request.json()) as Body;
      sent.push({ method: 'POST', url: request.url, body });
      if (outside(body)) return invalidCategory();
      const id = `f${next++}`;
      current = { ...current, flows: byDate([...current.flows, view(id, body)]) };
      return HttpResponse.json({ id }, { status: 201 });
    }),
    http.put('*/api/v1/realestate/properties/:id/flows/:flowId', async ({ request, params }) => {
      const body = (await request.json()) as Body;
      sent.push({ method: 'PUT', url: request.url, body });
      const id = String(params.flowId);
      if (!current.flows.some((f) => f.id === id)) return gone(id);
      if (outside(body)) return invalidCategory();
      const fresh = `f${next++}`;
      retired.add(id);
      current = { ...current, flows: byDate([...current.flows.filter((f) => f.id !== id), view(fresh, body)]) };
      return HttpResponse.json({ id: fresh });
    }),
    http.delete('*/api/v1/realestate/properties/:id/flows/:flowId', ({ request, params }) => {
      sent.push({ method: 'DELETE', url: request.url });
      const id = String(params.flowId);
      if (!current.flows.some((f) => f.id === id)) return gone(id);
      retired.add(id);
      current = { ...current, flows: current.flows.filter((f) => f.id !== id) };
      return HttpResponse.json({ id });
    }),
  );
  /** Corrected by someone else: retired, and back under a new id in the next read. */
  const correctElsewhere = (id: string, over: Partial<PropertyFlow>) => {
    retired.add(id);
    const fresh = `f${next++}`;
    current = { ...current, flows: byDate(current.flows.map((f) => (f.id === id ? { ...f, ...over, id: fresh } : f))) };
  };
  return { sent, correctElsewhere };
}

const PAGE = `/properties/${PROPERTY}?tab=expenses`;
const fr = (iso: string) => iso.split('-').reverse().join('/');
type User = Awaited<ReturnType<typeof renderApp>>['user'];
/** The row's ⋯ menu, opened, then the item picked. */
const rowAction = async (user: User, row: HTMLElement, date: string, item: string) => {
  await user.click(within(row).getByRole('button', { name: new RegExp(`^Actions pour le ${date}`) }));
  await user.click(await screen.findByRole('menuitem', { name: item }));
};
const choices = async (user: User, label: string) => {
  await user.click(screen.getByRole('combobox', { name: label }));
  const names = within(await screen.findByRole('listbox')).getAllByRole('option').map((o) => o.textContent);
  await user.keyboard('{Escape}');
  return names;
};
const region = () => screen.findByRole('region', { name: 'Dépenses et revenus' });
const rows = async () => within(await screen.findByRole('table', { name: 'Dépenses et revenus du bien' })).getAllByRole('row').slice(1);

describe('flows', () => {
  beforeAll(async () => { await import('../PropertyDetailPage'); }, 30_000);

  it('lists the flows newest first: date, kind, category, amount and note', async () => {
    serve();
    const { container } = await renderApp(PAGE);

    const [tax, works] = await rows();
    expect(tax).toHaveTextContent('15 octobre 2025');
    expect(tax).toHaveTextContent('Dépense');
    expect(tax).toHaveTextContent('Taxe foncière');
    expect(tax).toHaveTextContent('1 500,00 €');
    expect(works).toHaveTextContent('10 mars 2024');
    expect(works).toHaveTextContent('Travaux');
    expect(works).toHaveTextContent('12 000,00 €');
    expect(works).toHaveTextContent('toiture');
    await expectNoAxeViolations(container);
  });

  it('names each row menu after its category when two flows share a date', async () => {
    const flow = (id: string, category: string): PropertyFlow =>
      ({ id, occurredOn: '2025-10-15', kind: 'expense', amount: '10.00', category, note: null });
    serve(propertyDetail({ flows: [flow('a1', 'insurance'), flow('a2', 'energy')] }));
    await renderApp(PAGE);

    const table = await screen.findByRole('table', { name: 'Dépenses et revenus du bien' });
    const names = within(table).getAllByRole('button', { name: /^Actions pour le/ }).map((b) => b.getAttribute('aria-label') ?? b.textContent);
    expect(new Set(names).size).toBe(2);
  });

  it('says when there is none', async () => {
    serve(propertyDetail({ flows: [] }));
    await renderApp(PAGE);

    expect(await within(await region()).findByText('Aucune dépense ni aucun revenu')).toBeInTheDocument();
  });

  it('offers the categories of the kind chosen first, the category following the kind, and adds an income', async () => {
    const { sent } = serve();
    const { user, container } = await renderApp(PAGE);

    await user.click(await within(await region()).findByRole('button', { name: 'Ajouter une dépense ou un revenu' }));
    const form = within(await screen.findByRole('dialog', { name: 'Nouvelle dépense ou nouveau revenu' }));
    const category = () => form.getByRole('combobox', { name: 'Catégorie' });
    await waitFor(() => expect(form.getByRole('combobox', { name: 'Type' })).toHaveFocus());
    expect(form.getByRole('combobox', { name: 'Type' })).toHaveTextContent('Dépense');
    expect(form.getByLabelText('Date')).toHaveValue(fr(todayIso()));
    expect(await choices(user, 'Catégorie')).toEqual(['Travaux', 'Taxe foncière', 'Charges de copropriété', 'Assurance', 'Énergie', 'Entretien', 'Autre']);
    expect(category()).toHaveTextContent('Travaux');
    await pick(user, 'Catégorie', 'Énergie');
    await pick(user, 'Type', 'Revenu');
    expect(await choices(user, 'Catégorie')).toEqual(['Loyer', 'Autre revenu']);
    expect(category()).toHaveTextContent('Loyer');
    await pick(user, 'Type', 'Dépense');
    expect(category()).toHaveTextContent('Travaux');
    await pick(user, 'Type', 'Revenu');
    await user.type(form.getByLabelText('Montant'), '850');
    await user.clear(form.getByLabelText('Date'));
    await user.type(form.getByLabelText('Date'), '01/10/2026');
    await expectNoAxeViolations(container);
    await user.click(form.getByRole('button', { name: 'Ajouter' }));

    await waitFor(async () => expect(within(await region()).getByRole('status')).toHaveTextContent('Ligne ajoutée.'));
    expect(sent).toEqual([expect.objectContaining({ method: 'POST', body: { occurredOn: '2026-10-01', kind: 'income', category: 'rent', amount: '850' } })]);
    await waitFor(() => expect(screen.getByRole('heading', { name: 'Dépenses et revenus' })).toHaveFocus());
    await waitFor(() => expect(within(screen.getByRole('table', { name: 'Dépenses et revenus du bien' })).getAllByRole('row')[1]).toHaveTextContent('Loyer'));
  });

  it('corrects a flow inline; the correction comes back as the stored row', async () => {
    const { sent } = serve();
    const { user } = await renderApp(PAGE);

    await rowAction(user, (await rows())[1]!, '10 mars 2024', 'Corriger');
    const form = within(await screen.findByRole('dialog', { name: 'Corriger la ligne' }));
    expect(form.getByLabelText('Montant')).toHaveValue('12000.00');
        expect(form.getByRole('combobox', { name: 'Catégorie' })).toHaveTextContent('Travaux');
    await user.clear(form.getByLabelText('Montant'));
    await user.type(form.getByLabelText('Montant'), '12500');
    await user.clear(form.getByLabelText('Note'));
    await user.click(form.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(async () => expect(within(await region()).getByRole('status')).toHaveTextContent('Ligne corrigée.'));
    expect(sent[0]).toMatchObject({ method: 'PUT', body: { occurredOn: '2024-03-10', kind: 'expense', category: 'works', amount: '12500' } });
    expect(sent[0]!.body).not.toHaveProperty('note');
    expect(sent[0]!.url).toContain('/flows/f1');
    await waitFor(() => expect(within(screen.getByRole('table', { name: 'Dépenses et revenus du bien' })).getAllByRole('row')[2]).toHaveTextContent('12 500,00 €'));
  });

  it('deletes a flow after a confirmation', async () => {
    const { sent } = serve();
    const { user } = await renderApp(PAGE);

    await rowAction(user, (await rows())[0]!, '15 octobre 2025', 'Supprimer');
    expect(await screen.findByText('Cette ligne sera supprimée.')).toBeInTheDocument();
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));

    await waitFor(async () => expect(within(await region()).getByRole('status')).toHaveTextContent('Ligne supprimée.'));
    expect(sent[0]!.url).toContain('/flows/f2');
    await waitFor(() => expect(within(screen.getByRole('table', { name: 'Dépenses et revenus du bien' })).getAllByRole('row')).toHaveLength(2));
  });

  it('shows a category the server refuses under the category', async () => {
    serve();
    server.use(http.post('*/api/v1/realestate/properties/:id/flows', () => invalidCategory()));
    const { user } = await renderApp(PAGE);

    await user.click(await within(await region()).findByRole('button', { name: 'Ajouter une dépense ou un revenu' }));
    const form = within(await screen.findByRole('dialog', { name: 'Nouvelle dépense ou nouveau revenu' }));
    await user.type(form.getByLabelText('Montant'), '300');
    await user.click(form.getByRole('button', { name: 'Ajouter' }));

    expect(await form.findByText('Cette catégorie ne correspond pas à ce type de ligne.')).toBeInTheDocument();
    expect(form.getByRole('combobox', { name: 'Catégorie' })).toHaveAttribute('aria-invalid', 'true');
    expect(form.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('says a flow corrected elsewhere is retired, and « Recharger » closes the form onto the stored row', async () => {
    const { correctElsewhere } = serve();
    const { user } = await renderApp(PAGE);

    await rowAction(user, (await rows())[1]!, '10 mars 2024', 'Corriger');
    correctElsewhere('f1', { amount: '13000.00' });
    const form = within(await screen.findByRole('dialog', { name: 'Corriger la ligne' }));
    await user.click(form.getByRole('button', { name: 'Enregistrer' }));

    expect(await form.findByText('Cette ligne a déjà été corrigée ou supprimée.')).toBeInTheDocument();
    await user.click(form.getByRole('button', { name: 'Recharger' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    await waitFor(() => expect(within(screen.getByRole('table', { name: 'Dépenses et revenus du bien' })).getAllByRole('row')[2]).toHaveTextContent('13 000,00 €'));
    await waitFor(() => expect(screen.getByRole('heading', { name: 'Dépenses et revenus' })).toHaveFocus());
  });

  it('gives a reader the flows without any action', async () => {
    serve(propertyDetail({ canWrite: false }));
    await renderApp(PAGE);

    const section = await region();
    await rows();
    expect(within(section).queryByRole('button')).not.toBeInTheDocument();
    expect(within(section).queryByRole('columnheader', { name: 'Actions' })).not.toBeInTheDocument();
  });
});
