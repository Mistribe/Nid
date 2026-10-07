import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import type { components } from '@/api/schema';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { todayIso } from '../../subscriptions/today';
import type { IncomeSourceDetail } from '../api';
import { detail, eur, SALARY } from '../testData';

type Body = components['schemas']['IncomeOneOffBody'];
const PROJECT = { id: 'o1', occurredOn: '2026-03-10', amount: eur('400.00'), note: 'Prime de projet' };
const OLDER = { id: 'o0', occurredOn: '2025-12-24', amount: eur('150.00'), note: null };

function serve(initial: IncomeSourceDetail = detail({ oneOffs: [OLDER, PROJECT] })) {
  let current = initial;
  let next = 2;
  const sent: { method: string; url: string; body?: Body }[] = [];
  const view = (id: string, b: Body) => ({ id, occurredOn: b.occurredOn!, amount: { amount: b.amount!.amount, currency: 'EUR' }, note: b.note ?? null });
  server.use(
    http.get('*/api/v1/income/sources/:id', () => HttpResponse.json(current)),
    http.post('*/api/v1/income/sources/:id/one-offs', async ({ request }) => {
      const body = (await request.json()) as Body;
      sent.push({ method: 'POST', url: request.url, body });
      const id = `o${next++}`;
      current = { ...current, oneOffs: [...current.oneOffs, view(id, body)] };
      return HttpResponse.json({ id }, { status: 201 });
    }),
    http.put('*/api/v1/income/sources/:id/one-offs/:flowId', async ({ request, params }) => {
      const body = (await request.json()) as Body;
      sent.push({ method: 'PUT', url: request.url, body });
      if (!current.oneOffs.some((f) => f.id === params.flowId))
        return HttpResponse.json({ status: 409, code: 'core.fact_retired', title: 'Retired.' }, { status: 409 });
      const id = `o${next++}`;
      current = { ...current, oneOffs: current.oneOffs.map((f) => (f.id === params.flowId ? view(id, body) : f)) };
      return HttpResponse.json({ id });
    }),
    http.delete('*/api/v1/income/sources/:id/one-offs/:flowId', ({ request, params }) => {
      sent.push({ method: 'DELETE', url: request.url });
      if (!current.oneOffs.some((f) => f.id === params.flowId))
        return HttpResponse.json({ status: 404, code: null, title: 'Not found' }, { status: 404 });
      current = { ...current, oneOffs: current.oneOffs.filter((f) => f.id !== params.flowId) };
      return new HttpResponse(null, { status: 204 });
    }),
  );
  return { sent, retire: (id: string) => { current = { ...current, oneOffs: current.oneOffs.filter((f) => f.id !== id) }; } };
}

const region = () => screen.findByRole('region', { name: 'Revenus exceptionnels' });
const rows = async () => within(await screen.findByRole('table', { name: 'Revenus exceptionnels des 12 derniers mois' })).getAllByRole('row').slice(1);

describe('one-off income', () => {
  beforeAll(async () => { await import('../IncomeSourceDetailPage'); }, 30_000);

  it('lists the last twelve months’ one-offs, newest first', async () => {
    serve();
    const { container } = await renderApp(`/income/${SALARY}`);

    const [first, second] = await rows();
    expect(first).toHaveTextContent('10 mars 2026');
    expect(first).toHaveTextContent('400,00 €');
    expect(first).toHaveTextContent('Prime de projet');
    expect(second).toHaveTextContent('24 décembre 2025');
    await expectNoAxeViolations(container);
  });

  it('says when there is none', async () => {
    serve(detail({ oneOffs: [] }));
    await renderApp(`/income/${SALARY}`);

    expect(await within(await region()).findByText('Aucun revenu exceptionnel sur les 12 derniers mois.')).toBeInTheDocument();
  });

  it('adds a one-off dated today by default, announces it and focuses the section', async () => {
    const { sent } = serve();
    const { user, container } = await renderApp(`/income/${SALARY}`);

    await user.click(await within(await region()).findByRole('button', { name: 'Ajouter un revenu exceptionnel' }));
    const form = screen.getByRole('form', { name: 'Nouveau revenu exceptionnel' });
    expect(within(form).getByLabelText('Date')).toHaveValue(todayIso());
    expect(within(form).getByLabelText('Date')).toHaveFocus();
    expect(within(form).getByText('Une prime à venir se saisit une fois versée.')).toBeInTheDocument();
    await user.type(within(form).getByLabelText('Montant'), '250,50');
    await user.type(within(form).getByLabelText('Note'), 'Intéressement');
    await expectNoAxeViolations(container);
    await user.click(within(form).getByRole('button', { name: 'Ajouter' }));

    expect(await within(await region()).findByRole('status')).toHaveTextContent('Revenu exceptionnel ajouté.');
    expect(sent).toEqual([expect.objectContaining({ method: 'POST', body: { occurredOn: todayIso(), amount: { amount: '250.50', currency: 'EUR' }, note: 'Intéressement' } })]);
    expect(screen.getByRole('heading', { name: 'Revenus exceptionnels' })).toHaveFocus();
    await waitFor(async () => expect((await rows())[0]).toHaveTextContent('250,50 €'));
  });

  it('refuses a date after today before sending', async () => {
    const { sent } = serve();
    const { user } = await renderApp(`/income/${SALARY}`);

    await user.click(await within(await region()).findByRole('button', { name: 'Ajouter un revenu exceptionnel' }));
    const form = screen.getByRole('form', { name: 'Nouveau revenu exceptionnel' });
    const tomorrow = todayIso(new Date(Date.now() + 86_400_000));
    await user.clear(within(form).getByLabelText('Date'));
    await user.type(within(form).getByLabelText('Date'), tomorrow);
    await user.type(within(form).getByLabelText('Montant'), '100');
    await user.click(within(form).getByRole('button', { name: 'Ajouter' }));

    expect(await within(form).findByText('Cette date est dans le futur.')).toBeInTheDocument();
    expect(sent).toEqual([]);
  });

  it('corrects a one-off inline: a new id, the change announced', async () => {
    const { sent } = serve();
    const { user } = await renderApp(`/income/${SALARY}`);

    await user.click(within((await rows())[0]!).getByRole('button', { name: 'Corriger' }));
    const form = screen.getByRole('form', { name: 'Corriger le revenu exceptionnel' });
    expect(within(form).getByLabelText('Montant')).toHaveValue('400');
    await user.clear(within(form).getByLabelText('Montant'));
    await user.type(within(form).getByLabelText('Montant'), '450');
    await user.click(within(form).getByRole('button', { name: 'Enregistrer' }));

    expect(await within(await region()).findByRole('status')).toHaveTextContent('Revenu exceptionnel corrigé.');
    await waitFor(async () => expect((await rows())[0]).toHaveTextContent('450,00 €'));
    expect(sent[0]).toMatchObject({ method: 'PUT', body: { occurredOn: '2026-03-10', amount: { amount: '450', currency: 'EUR' }, note: 'Prime de projet' } });
    expect(sent[0]!.url).toContain('/one-offs/o1');
  });

  it('gives the focus back to « Corriger » when the correction is cancelled', async () => {
    serve();
    const { user } = await renderApp(`/income/${SALARY}`);

    await user.click(within((await rows())[0]!).getByRole('button', { name: 'Corriger' }));
    await user.click(within(screen.getByRole('form', { name: 'Corriger le revenu exceptionnel' })).getByRole('button', { name: 'Annuler' }));

    await waitFor(async () => expect(within((await rows())[0]!).getByRole('button', { name: 'Corriger' })).toHaveFocus());
  });

  it('deletes a one-off after a confirmation', async () => {
    serve(detail({ oneOffs: [PROJECT] }));
    const { user } = await renderApp(`/income/${SALARY}`);

    await user.click(within((await rows())[0]!).getByRole('button', { name: 'Supprimer' }));
    expect(screen.getByText('Ce revenu exceptionnel sera retiré.')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Confirmer' }));

    expect(await within(await region()).findByRole('status')).toHaveTextContent('Revenu exceptionnel supprimé.');
    expect(await within(await region()).findByText('Aucun revenu exceptionnel sur les 12 derniers mois.')).toBeInTheDocument();
  });

  it('shows a correction of a one-off changed meanwhile with its own sentence and « Recharger »', async () => {
    const { retire } = serve();
    const { user } = await renderApp(`/income/${SALARY}`);

    await user.click(within((await rows())[0]!).getByRole('button', { name: 'Corriger' }));
    retire('o1');
    const form = screen.getByRole('form', { name: 'Corriger le revenu exceptionnel' });
    await user.click(within(form).getByRole('button', { name: 'Enregistrer' }));

    expect(await within(form).findByText('Cette ligne a déjà été corrigée ou supprimée.')).toBeInTheDocument();
    expect(within(form).getByRole('button', { name: 'Recharger' })).toBeInTheDocument();
  });

  it('gives a reader the list without any action', async () => {
    serve(detail({ canWrite: false, visibility: 'shared', oneOffs: [PROJECT] }));
    await renderApp(`/income/${SALARY}`);

    const section = await region();
    await rows();
    expect(within(section).queryByRole('button')).not.toBeInTheDocument();
    expect(within(section).queryByRole('columnheader', { name: 'Actions' })).not.toBeInTheDocument();
  });
});
