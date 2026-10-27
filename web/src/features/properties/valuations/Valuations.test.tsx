import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import type { components } from '@/api/schema';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { todayIso } from '../../subscriptions/today';
import type { PropertyDetail } from '../api';
import { PROPERTY, propertyDetail } from '../testData';

type Body = components['schemas']['PropertyValuationBody'];
/** An amount as the server writes it back: two decimals. */
const written = (wire: string) => { const [whole, decimals = ''] = wire.split('.'); return `${whole}.${decimals.padEnd(2, '0')}`; };

/** As the server: one estimate a date (a new one on that date replaces it), listed oldest first; a voided one is retired. */
function serve(initial: PropertyDetail = propertyDetail()) {
  let current = initial;
  let next = 3;
  const voided = new Set<string>();
  const sent: { method: string; url: string; body?: Body }[] = [];
  server.use(
    http.get('*/api/v1/realestate/properties/:id', () => HttpResponse.json(current)),
    http.post('*/api/v1/realestate/properties/:id/valuations', async ({ request }) => {
      const body = (await request.json()) as Body;
      sent.push({ method: 'POST', url: request.url, body });
      const id = `v${next++}`;
      const kept = current.valuations.filter((v) => v.asOf !== body.asOf);
      const added = { id, asOf: body.asOf!, amount: written(body.amount!), note: body.note ?? null };
      current = { ...current, valuations: [...kept, added].sort((a, b) => a.asOf.localeCompare(b.asOf)) };
      return HttpResponse.json({ id }, { status: 201 });
    }),
    http.delete('*/api/v1/realestate/properties/:id/valuations/:valuationId', ({ request, params }) => {
      sent.push({ method: 'DELETE', url: request.url });
      const id = String(params.valuationId);
      if (voided.has(id)) return HttpResponse.json({ status: 409, code: 'core.fact_retired', title: 'Already voided.' }, { status: 409 });
      if (!current.valuations.some((v) => v.id === id)) return HttpResponse.json({ status: 404, code: 'core.fact_not_found', title: 'Not found' }, { status: 404 });
      voided.add(id);
      current = { ...current, valuations: current.valuations.filter((v) => v.id !== id) };
      return HttpResponse.json({ id });
    }),
  );
  /** Deleted by someone else: retired, and gone from the next read. */
  const voidElsewhere = (id: string) => { voided.add(id); current = { ...current, valuations: current.valuations.filter((v) => v.id !== id) }; };
  return { sent, voidElsewhere };
}

const PAGE = `/properties/${PROPERTY}?tab=valuations`;
const fr = (iso: string) => iso.split('-').reverse().join('/');
type User = Awaited<ReturnType<typeof renderApp>>['user'];
/** The row's ⋯ menu, opened, then the item picked. */
const rowAction = async (user: User, row: HTMLElement, date: string, item: string) => {
  await user.click(within(row).getByRole('button', { name: new RegExp(`^Actions pour le ${date}`) }));
  await user.click(await screen.findByRole('menuitem', { name: item }));
};
const add = () => screen.findByRole('button', { name: 'Ajouter une estimation' });
const dialog = async () => within(await screen.findByRole('dialog', { name: 'Nouvelle estimation' }));
const region = () => screen.findByRole('region', { name: 'Estimations' });
const rows = async () => within(await screen.findByRole('table', { name: 'Estimations du bien' })).getAllByRole('row').slice(1);

describe('estimates', () => {
  beforeAll(async () => { await import('../PropertyDetailPage'); }, 30_000);

  it('lists the estimates newest first, with their date, value and note', async () => {
    serve();
    const { container } = await renderApp(PAGE);

    const [last, first] = await rows();
    expect(last).toHaveTextContent('1 juin 2026');
    expect(last).toHaveTextContent('290 000,00 €');
    expect(last).toHaveTextContent('agence');
    expect(first).toHaveTextContent('1 juin 2025');
    expect(first).toHaveTextContent('280 000,00 €');
    await expectNoAxeViolations(container);
  });

  it('shows the source document on the estimate that has one only', async () => {
    const base = propertyDetail();
    serve({ ...base, valuations: [{ ...base.valuations[0]!, documentId: 'd1' }, base.valuations[1]!] });
    await renderApp(PAGE);

    const [last, first] = await rows();
    expect(within(first!).getByRole('link', { name: 'Source du chiffre' })).toHaveAttribute('href', '/documents/d1');
    expect(within(last!).queryByRole('link', { name: 'Source du chiffre' })).not.toBeInTheDocument();
  });

  it('says when there is none', async () => {
    serve(propertyDetail({ valuations: [] }));
    await renderApp(PAGE);

    expect(await within(await region()).findByText('Aucune estimation')).toBeInTheDocument();
  });

  it('adds an estimate, dated today unless changed, announces it and focuses the section', async () => {
    const { sent } = serve();
    const { user, container } = await renderApp(PAGE);

    await user.click(await add());
    const form = await dialog();
    const date = form.getByLabelText('Date');
    expect(date).toHaveValue(fr(todayIso()));
    await waitFor(() => expect(date).toHaveFocus());
    await user.clear(date);
    await user.type(date, '30/09/2026');
    await user.type(form.getByLabelText('Valeur'), '295000,5');
    await user.type(form.getByLabelText('Note'), 'notaire');
    await expectNoAxeViolations(container);
    await user.click(form.getByRole('button', { name: 'Ajouter' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    await waitFor(() => expect(screen.getAllByRole('status').map((n) => n.textContent)).toContain('Estimation ajoutée.'));
    expect(sent).toEqual([expect.objectContaining({ method: 'POST', body: { asOf: '2026-09-30', amount: '295000.5', note: 'notaire' } })]);
    expect(await add()).toHaveFocus();
    await waitFor(async () => expect((await rows())[0]).toHaveTextContent('295 000,50 €'));
  });

  it('says before sending that an estimate on a date already estimated replaces it, and keeps one row for that date', async () => {
    const { sent } = serve();
    const { user } = await renderApp(PAGE);

    await user.click(await add());
    const form = await dialog();
    expect(form.queryByText('Une estimation existe déjà à cette date : elle sera remplacée.')).not.toBeInTheDocument();
    await user.clear(form.getByLabelText('Date'));
    await user.type(form.getByLabelText('Date'), '01/06/2026');
    expect(form.getByText('Une estimation existe déjà à cette date : elle sera remplacée.')).toBeInTheDocument();
    await user.type(form.getByLabelText('Valeur'), '300000');
    await user.click(form.getByRole('button', { name: 'Ajouter' }));

    await waitFor(() => expect(screen.getAllByRole('status').map((n) => n.textContent)).toContain('Estimation remplacée.'));
    expect(sent[0]!.body).toEqual({ asOf: '2026-06-01', amount: '300000' });
    await waitFor(async () => expect((await rows())[0]).toHaveTextContent('300 000,00 €'));
    expect(await rows()).toHaveLength(2);
  });

  it('refuses a date after today and a zero value before sending', async () => {
    const { sent } = serve();
    const { user } = await renderApp(PAGE);

    await user.click(await add());
    const form = await dialog();
    await user.clear(form.getByLabelText('Date'));
    await user.type(form.getByLabelText('Date'), '01/01/2999');
    await user.type(form.getByLabelText('Valeur'), '0');
    await user.click(form.getByRole('button', { name: 'Ajouter' }));

    expect(await form.findByText('Cette date est dans le futur.')).toBeInTheDocument();
    expect(form.getByText('Un montant positif, avec deux décimales au plus, comme 1500,50.')).toBeInTheDocument();
    expect(sent).toEqual([]);
  });

  it('gives « Annuler » back the focus to « Ajouter une estimation »', async () => {
    serve();
    const { user } = await renderApp(PAGE);

    const open = await add();
    await user.click(open);
    const form = await dialog();
    await user.click(form.getByRole('button', { name: 'Annuler' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(open).toHaveFocus();
  });

  it('deletes an estimate after a confirmation', async () => {
    const { sent } = serve();
    const { user } = await renderApp(PAGE);

    await rowAction(user, (await rows())[0]!, '1 juin 2026', 'Supprimer');
    expect(await screen.findByText('Cette estimation sera supprimée.')).toBeInTheDocument();
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));

    await waitFor(async () => expect(within(await region()).getByRole('status')).toHaveTextContent('Estimation supprimée.'));
    expect(sent[0]!.url).toContain('/valuations/v2');
    await waitFor(async () => expect(await rows()).toHaveLength(1));
  });

  it('says an estimate deleted elsewhere is gone, and « Recharger » reads the estimates again', async () => {
    const { voidElsewhere } = serve();
    const { user } = await renderApp(PAGE);

    const row = (await rows())[0]!;
    voidElsewhere('v2');
    await rowAction(user, row, '1 juin 2026', 'Supprimer');
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));

    expect(await within(row).findByText('Cette ligne a déjà été corrigée ou supprimée.')).toBeInTheDocument();
    await user.click(within(row).getByRole('button', { name: 'Recharger' }));

    await waitFor(async () => expect(await rows()).toHaveLength(1));
    await waitFor(() => expect(screen.getByRole('heading', { name: 'Estimations' })).toHaveFocus());
  });

  it('gives a reader the estimates without any action', async () => {
    serve(propertyDetail({ canWrite: false }));
    await renderApp(PAGE);

    const section = await region();
    await rows();
    expect(within(section).queryByRole('button')).not.toBeInTheDocument();
    expect(within(section).queryByRole('columnheader', { name: 'Actions' })).not.toBeInTheDocument();
  });
});
