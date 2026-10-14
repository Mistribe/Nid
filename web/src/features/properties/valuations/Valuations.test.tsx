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

const region = () => screen.findByRole('region', { name: 'Estimations' });
const rows = async () => within(await screen.findByRole('table', { name: 'Estimations du bien' })).getAllByRole('row').slice(1);
const form = () => screen.getByRole('form', { name: 'Nouvelle estimation' });

describe('estimates', () => {
  beforeAll(async () => { await import('../PropertyDetailPage'); }, 30_000);

  it('lists the estimates newest first, with their date, value and note', async () => {
    serve();
    const { container } = await renderApp(`/properties/${PROPERTY}`);

    const [last, first] = await rows();
    expect(last).toHaveTextContent('1 juin 2026');
    expect(last).toHaveTextContent('290 000,00 €');
    expect(last).toHaveTextContent('agence');
    expect(first).toHaveTextContent('1 juin 2025');
    expect(first).toHaveTextContent('280 000,00 €');
    await expectNoAxeViolations(container);
  });

  it('says when there is none', async () => {
    serve(propertyDetail({ valuations: [] }));
    await renderApp(`/properties/${PROPERTY}`);

    expect(await within(await region()).findByText('Aucune estimation')).toBeInTheDocument();
  });

  it('adds an estimate, dated today unless changed, announces it and focuses the section', async () => {
    const { sent } = serve();
    const { user, container } = await renderApp(`/properties/${PROPERTY}`);

    await user.click(await within(await region()).findByRole('button', { name: 'Ajouter une estimation' }));
    const date = within(form()).getByLabelText('Date');
    expect(date).toHaveValue(todayIso());
    expect(date).toHaveFocus();
    await user.clear(date);
    await user.type(date, '2026-09-30');
    await user.type(within(form()).getByLabelText('Valeur'), '295000,5');
    await user.type(within(form()).getByLabelText('Note'), 'notaire');
    await expectNoAxeViolations(container);
    await user.click(within(form()).getByRole('button', { name: 'Ajouter' }));

    await waitFor(async () => expect(within(await region()).getByRole('status')).toHaveTextContent('Estimation ajoutée.'));
    expect(sent).toEqual([expect.objectContaining({ method: 'POST', body: { asOf: '2026-09-30', amount: '295000.5', note: 'notaire' } })]);
    expect(screen.getByRole('heading', { name: 'Estimations' })).toHaveFocus();
    await waitFor(async () => expect((await rows())[0]).toHaveTextContent('295 000,50 €'));
  });

  it('says before sending that an estimate on a date already estimated replaces it, and keeps one row for that date', async () => {
    const { sent } = serve();
    const { user } = await renderApp(`/properties/${PROPERTY}`);

    await user.click(await within(await region()).findByRole('button', { name: 'Ajouter une estimation' }));
    expect(within(form()).queryByText('Une estimation existe déjà à cette date : elle sera remplacée.')).not.toBeInTheDocument();
    await user.clear(within(form()).getByLabelText('Date'));
    await user.type(within(form()).getByLabelText('Date'), '2026-06-01');
    expect(within(form()).getByText('Une estimation existe déjà à cette date : elle sera remplacée.')).toBeInTheDocument();
    await user.type(within(form()).getByLabelText('Valeur'), '300000');
    await user.click(within(form()).getByRole('button', { name: 'Ajouter' }));

    await waitFor(async () => expect(within(await region()).getByRole('status')).toHaveTextContent('Estimation remplacée.'));
    expect(sent[0]!.body).toEqual({ asOf: '2026-06-01', amount: '300000' });
    await waitFor(async () => expect((await rows())[0]).toHaveTextContent('300 000,00 €'));
    expect(await rows()).toHaveLength(2);
  });

  it('refuses a date after today and a zero value before sending', async () => {
    const { sent } = serve();
    const { user } = await renderApp(`/properties/${PROPERTY}`);

    await user.click(await within(await region()).findByRole('button', { name: 'Ajouter une estimation' }));
    await user.clear(within(form()).getByLabelText('Date'));
    await user.type(within(form()).getByLabelText('Date'), '2999-01-01');
    await user.type(within(form()).getByLabelText('Valeur'), '0');
    await user.click(within(form()).getByRole('button', { name: 'Ajouter' }));

    expect(await within(form()).findByText('Cette date est dans le futur.')).toBeInTheDocument();
    expect(within(form()).getByText('Un montant positif, avec deux décimales au plus, comme 1500,50.')).toBeInTheDocument();
    expect(sent).toEqual([]);
  });

  it('gives « Annuler » back the focus to « Ajouter une estimation »', async () => {
    serve();
    const { user } = await renderApp(`/properties/${PROPERTY}`);

    const add = await within(await region()).findByRole('button', { name: 'Ajouter une estimation' });
    await user.click(add);
    await user.click(within(form()).getByRole('button', { name: 'Annuler' }));

    expect(screen.queryByRole('form', { name: 'Nouvelle estimation' })).not.toBeInTheDocument();
    expect(add).toHaveFocus();
  });

  it('deletes an estimate after a confirmation', async () => {
    const { sent } = serve();
    const { user } = await renderApp(`/properties/${PROPERTY}`);

    await user.click(within((await rows())[0]!).getByRole('button', { name: 'Supprimer' }));
    expect(screen.getByText('Cette estimation sera supprimée.')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Confirmer' }));

    await waitFor(async () => expect(within(await region()).getByRole('status')).toHaveTextContent('Estimation supprimée.'));
    expect(sent[0]!.url).toContain('/valuations/v2');
    await waitFor(async () => expect(await rows()).toHaveLength(1));
  });

  it('says an estimate deleted elsewhere is gone, and « Recharger » reads the estimates again', async () => {
    const { voidElsewhere } = serve();
    const { user } = await renderApp(`/properties/${PROPERTY}`);

    const row = (await rows())[0]!;
    voidElsewhere('v2');
    await user.click(within(row).getByRole('button', { name: 'Supprimer' }));
    await user.click(screen.getByRole('button', { name: 'Confirmer' }));

    expect(await within(row).findByText('Cette ligne a déjà été corrigée ou supprimée.')).toBeInTheDocument();
    await user.click(within(row).getByRole('button', { name: 'Recharger' }));

    await waitFor(async () => expect(await rows()).toHaveLength(1));
    await waitFor(() => expect(screen.getByRole('heading', { name: 'Estimations' })).toHaveFocus());
  });

  it('gives a reader the estimates without any action', async () => {
    serve(propertyDetail({ canWrite: false }));
    await renderApp(`/properties/${PROPERTY}`);

    const section = await region();
    await rows();
    expect(within(section).queryByRole('button')).not.toBeInTheDocument();
    expect(within(section).queryByRole('columnheader', { name: 'Actions' })).not.toBeInTheDocument();
  });
});
