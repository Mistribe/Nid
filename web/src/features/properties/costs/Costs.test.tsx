import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import type { components } from '@/api/schema';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { pick } from '@/test/controls';
import { server } from '@/test/server';
import { todayIso } from '../../subscriptions/today';
import type { PropertyDetail, PropertyRecurringCost } from '../api';
import { PROPERTY, propertyDetail, recurringCost } from '../testData';

type Body = components['schemas']['PropertyRecurringCostBody'];
/** An amount as the server writes it back: two decimals. */
const written = (wire: string) => { const [whole, decimals = ''] = wire.split('.'); return `${whole}.${decimals.padEnd(2, '0')}`; };
/** The server's monthly equivalent of the costs these tests send: computed there, never here. */
const MONTHLY: Record<string, string> = { '250.00/3/month': '83.33', '1200.00/1/year': '100.00' };
const INSURANCE = recurringCost({
  id: 'c0', version: 2, category: 'insurance', amount: '300.00', startsOn: '2020-07-01', endsOn: '2024-07-01', monthlyEquivalent: '25.00',
  nextDueOn: null,
});

/**
 * As the server: costs listed by start; a stale version is `409 realestate.recurring_cost_conflict`, an unknown cost 404;
 * an ended cost has no next due date.
 */
function serve(initial: PropertyDetail = propertyDetail({ recurringCosts: [INSURANCE, recurringCost()] })) {
  let current = initial;
  let next = 2;
  const sent: { method: string; url: string; body?: Body }[] = [];
  const view = (id: string, b: Body, version: number): PropertyRecurringCost => {
    const amount = written(b.amount!);
    return {
      id, version, category: b.category!, amount, every: { count: b.every!.count, unit: b.every!.unit }, startsOn: b.startsOn!,
      endsOn: b.endsOn ?? null, note: b.note ?? null, monthlyEquivalent: MONTHLY[`${amount}/${b.every!.count}/${b.every!.unit}`] ?? '0.00',
      nextDueOn: b.endsOn && b.endsOn <= todayIso() ? null : '2027-01-01',
    };
  };
  const byStart = (all: PropertyRecurringCost[]) => [...all].sort((a, b) => a.startsOn.localeCompare(b.startsOn));
  const conflict = () => HttpResponse.json({ status: 409, code: 'realestate.recurring_cost_conflict', title: 'Changed.' }, { status: 409 });
  const missing = () => HttpResponse.json({ status: 404, code: null, title: 'Not found' }, { status: 404 });
  server.use(
    http.get('*/api/v1/realestate/properties/:id', () => HttpResponse.json(current)),
    http.post('*/api/v1/realestate/properties/:id/recurring-costs', async ({ request }) => {
      const body = (await request.json()) as Body;
      sent.push({ method: 'POST', url: request.url, body });
      const id = `c${next++}`;
      current = { ...current, recurringCosts: byStart([...current.recurringCosts, view(id, body, 1)]) };
      return HttpResponse.json({ id }, { status: 201 });
    }),
    http.put('*/api/v1/realestate/properties/:id/recurring-costs/:costId', async ({ request, params }) => {
      const body = (await request.json()) as Body;
      sent.push({ method: 'PUT', url: request.url, body });
      const old = current.recurringCosts.find((c) => c.id === params.costId);
      if (!old) return missing();
      if (old.version !== body.version) return conflict();
      current = { ...current, recurringCosts: byStart(current.recurringCosts.map((c) => (c.id === old.id ? view(old.id, body, old.version + 1) : c))) };
      return HttpResponse.json({ id: old.id });
    }),
    http.delete('*/api/v1/realestate/properties/:id/recurring-costs/:costId', ({ request, params }) => {
      sent.push({ method: 'DELETE', url: request.url });
      const old = current.recurringCosts.find((c) => c.id === params.costId);
      if (!old) return missing();
      if (new URL(request.url).searchParams.get('version') !== String(old.version)) return conflict();
      current = { ...current, recurringCosts: current.recurringCosts.filter((c) => c.id !== old.id) };
      return new HttpResponse(null, { status: 204 });
    }),
  );
  return {
    sent,
    change: (id: string, over: Partial<PropertyRecurringCost>) => {
      current = { ...current, recurringCosts: current.recurringCosts.map((c) => (c.id === id ? { ...c, ...over } : c)) };
    },
  };
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
const region = () => screen.findByRole('region', { name: 'Charges prévues' });
const rows = async () => within(await screen.findByRole('table', { name: 'Charges prévues du bien' })).getAllByRole('row').slice(1);

describe('recurring costs', () => {
  // The lazy route chunk loaded cold inside the first test races its timeouts under load.
  beforeAll(async () => { await import('../PropertyDetailPage'); }, 30_000);

  it('lists the planned costs in the server’s order, an ended one « Terminée », under a sentence saying they are forecasts', async () => {
    serve();
    const { container } = await renderApp(PAGE);

    const [insurance, tax] = await rows();
    expect(within(await region()).getByText('Prévisions seulement : la routine proposera de les enregistrer comme dépenses.')).toBeInTheDocument();
    expect(insurance).toHaveTextContent('Assurance');
    expect(insurance).toHaveTextContent('300,00 €');
    expect(insurance).toHaveTextContent('tous les ans');
    expect(insurance).toHaveTextContent('1 juillet 2020');
    expect(insurance).toHaveTextContent('1 juillet 2024');
    expect(insurance).toHaveTextContent('25,00 €');
    expect(insurance).toHaveTextContent('Terminée');
    expect(tax).toHaveTextContent('Taxe foncière');
    expect(tax).toHaveTextContent('1 200,00 €');
    expect(tax).toHaveTextContent('15 octobre 2021');
    expect(tax).toHaveTextContent('100,00 €');
    expect(tax).toHaveTextContent('15 octobre 2026');
    expect(tax).not.toHaveTextContent('Terminée');
    await expectNoAxeViolations(container);
  });

  it('says when there is none', async () => {
    serve(propertyDetail());
    await renderApp(PAGE);

    expect(await within(await region()).findByText('Aucune charge prévue')).toBeInTheDocument();
  });

  it('adds a cost every 3 months, never offering works', async () => {
    const { sent } = serve();
    const { user, container } = await renderApp(PAGE);

    await user.click(await within(await region()).findByRole('button', { name: 'Ajouter une charge prévue' }));
    const form = within(await screen.findByRole('dialog', { name: 'Nouvelle charge prévue' }));
    await waitFor(() => expect(form.getByRole('combobox', { name: 'Catégorie' })).toHaveFocus());
    expect(await choices(user, 'Catégorie')).toEqual(['Taxe foncière', 'Charges de copropriété', 'Assurance', 'Énergie', 'Entretien', 'Frais de gestion', 'Autre']);
    expect(await choices(user, 'Unité')).toEqual(['mois', 'an(s)']);
    expect(form.getByRole('combobox', { name: 'Catégorie' })).toHaveTextContent('Taxe foncière');
    expect(form.getByLabelText('Tous les')).toHaveValue('1');
    expect(form.getByRole('combobox', { name: 'Unité' })).toHaveTextContent('an(s)');
    expect(form.getByLabelText('Début')).toHaveValue(fr(todayIso()));
    await pick(user, 'Catégorie', 'Charges de copropriété');
    await user.type(form.getByLabelText('Montant'), '250');
    await user.clear(form.getByLabelText('Tous les'));
    await user.type(form.getByLabelText('Tous les'), '3');
    await pick(user, 'Unité', 'mois');
    await user.clear(form.getByLabelText('Début'));
    await user.type(form.getByLabelText('Début'), '01/01/2025');
    await expectNoAxeViolations(container);
    await user.click(form.getByRole('button', { name: 'Ajouter' }));

    await waitFor(async () => expect(within(await region()).getByRole('status')).toHaveTextContent('Charge prévue ajoutée.'));
    await waitFor(() => {
      expect(sent).toEqual([expect.objectContaining({
        method: 'POST', body: { category: 'condo_fees', amount: '250', every: { count: 3, unit: 'month' }, startsOn: '2025-01-01' },
      })]);
      expect(screen.getByRole('heading', { name: 'Charges prévues' })).toHaveFocus();
    });
    await waitFor(async () => {
      expect((await rows())[2]).toHaveTextContent('tous les 3 mois');
      expect((await rows())[2]).toHaveTextContent('83,33 €');
    });
    await expectNoAxeViolations(container);
  });

  it('corrects a cost with the version it opened with: ended, it reads « Terminée »', async () => {
    const { sent } = serve();
    const { user, container } = await renderApp(PAGE);

    await rowAction(user, (await rows())[1]!, '15 octobre 2021', 'Corriger');
    const form = within(await screen.findByRole('dialog', { name: 'Corriger la charge prévue' }));
    await expectNoAxeViolations(container);
    expect(form.getByLabelText('Montant')).toHaveValue('1200.00');
    await user.type(form.getByLabelText('Fin'), '31/12/2025');
    await user.click(form.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(async () => expect(within(await region()).getByRole('status')).toHaveTextContent('Charge prévue corrigée.'));
    await waitFor(() => {
      expect(sent[0]!.url).toContain('/recurring-costs/c1');
      expect(sent[0]!.body).toEqual({
        category: 'property_tax', amount: '1200.00', every: { count: 1, unit: 'year' }, startsOn: '2021-10-15', endsOn: '2025-12-31', version: 1,
      });
    });
    await waitFor(async () => expect((await rows())[1]).toHaveTextContent('Terminée'));
    await expectNoAxeViolations(container);
  });

  it('deletes a cost after a confirmation, with the version it read', async () => {
    const { sent } = serve();
    const { user, container } = await renderApp(PAGE);

    await rowAction(user, (await rows())[0]!, '1 juillet 2020', 'Supprimer');
    expect(await screen.findByText('Cette charge prévue sera supprimée.')).toBeInTheDocument();
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));

    await waitFor(async () => expect(within(await region()).getByRole('status')).toHaveTextContent('Charge prévue supprimée.'));
    await waitFor(async () => {
      expect(sent[0]!.url).toContain('/recurring-costs/c0?version=2');
      expect(await rows()).toHaveLength(1);
    });
    await expectNoAxeViolations(container);
  });

  it('says a cost changed meanwhile, and « Recharger » closes the form onto the stored row', async () => {
    const { change } = serve();
    const { user } = await renderApp(PAGE);

    await rowAction(user, (await rows())[1]!, '15 octobre 2021', 'Corriger');
    change('c1', { amount: '1300.00', version: 2 });
    const form = within(await screen.findByRole('dialog', { name: 'Corriger la charge prévue' }));
    await user.click(form.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(form.getByText('Cette charge prévue a changé entre-temps : rechargez la page.')).toBeInTheDocument());
    await user.click(form.getByRole('button', { name: 'Recharger' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    await waitFor(async () => expect((await rows())[1]).toHaveTextContent('1 300,00 €'));
    await waitFor(async () => expect(within((await rows())[1]!).getByRole('button', { name: /^Actions pour le 15 octobre 2021/ })).toHaveFocus());
  });

  it('shows a period the server refuses under « Tous les »', async () => {
    serve();
    server.use(http.post('*/api/v1/realestate/properties/:id/recurring-costs', () => HttpResponse.json(
      { status: 422, code: 'realestate.invalid_property_field', title: 'x', errors: [{ path: 'every.count', code: 'realestate.invalid_property_field', message: 'x' }] },
      { status: 422 })));
    const { user } = await renderApp(PAGE);

    await user.click(await within(await region()).findByRole('button', { name: 'Ajouter une charge prévue' }));
    const form = within(await screen.findByRole('dialog', { name: 'Nouvelle charge prévue' }));
    await user.type(form.getByLabelText('Montant'), '1200');
    await user.click(form.getByRole('button', { name: 'Ajouter' }));

    await waitFor(() => {
      expect(form.getByText('Cette valeur n’est pas acceptée.')).toBeInTheDocument();
      expect(form.getByLabelText('Tous les')).toHaveAttribute('aria-invalid', 'true');
      expect(form.queryByRole('alert')).not.toBeInTheDocument();
    });
  });

  it('refuses a period that is not a whole number of 1 to 120 before sending', async () => {
    const { sent } = serve();
    const { user } = await renderApp(PAGE);

    await user.click(await within(await region()).findByRole('button', { name: 'Ajouter une charge prévue' }));
    const form = within(await screen.findByRole('dialog', { name: 'Nouvelle charge prévue' }));
    await user.type(form.getByLabelText('Montant'), '1200');
    for (const count of ['0', '121', '1,5']) {
      await user.clear(form.getByLabelText('Tous les'));
      await user.type(form.getByLabelText('Tous les'), count);
      await user.click(form.getByRole('button', { name: 'Ajouter' }));
      await waitFor(() => {
        expect(form.getByText('Un nombre entier de 1 à 120.')).toBeInTheDocument();
        expect(form.getByLabelText('Tous les')).toHaveAttribute('aria-invalid', 'true');
      });
    }
    expect(sent).toEqual([]);
  });

  it('gives a reader the planned costs without any action', async () => {
    serve(propertyDetail({ canWrite: false, recurringCosts: [INSURANCE, recurringCost()] }));
    await renderApp(PAGE);

    const section = await region();
    await rows();
    expect(within(section).queryByRole('button')).not.toBeInTheDocument();
    expect(within(section).queryByRole('columnheader', { name: 'Actions' })).not.toBeInTheDocument();
  });
});
