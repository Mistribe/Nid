import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import type { components } from '@/api/schema';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { toasts } from '@/test/toasts';
import type { IncomeSourceDetail } from '../api';
import { detail, phase, SALARY } from '../testData';

type PhaseBody = components['schemas']['IncomePhaseCorrectionBody'];

function serveStateful(initial: IncomeSourceDetail = detail()) {
  let current = initial;
  const sent: { method: string; url: string; body?: unknown }[] = [];
  server.use(
    http.get('*/api/v1/income/sources/:id', () => HttpResponse.json(current)),
    http.post('*/api/v1/income/sources/:id/phases', async ({ request }) => {
      const body = (await request.json()) as PhaseBody;
      sent.push({ method: 'POST', url: request.url, body });
      current = { ...current, phases: [...current.phases, phase({ id: 'p9', label: body.label, startsOn: body.startsOn, netAmount: body.netAmount!, every: body.every!, status: 'upcoming' })] };
      return HttpResponse.json({ id: 'p9' }, { status: 201 });
    }),
    http.put('*/api/v1/income/sources/:id/phases/:phaseId', async ({ request, params }) => {
      const body = (await request.json()) as PhaseBody;
      sent.push({ method: 'PUT', url: request.url, body });
      const target = current.phases.find((p) => p.id === params.phaseId);
      if (!target || body.version !== target.version)
        return HttpResponse.json({ status: 409, code: 'income.phase_conflict', title: 'Changed meanwhile.' }, { status: 409 });
      current = { ...current, phases: current.phases.map((p) => (p.id === target.id ? { ...p, netAmount: body.netAmount!, version: p.version + 1 } : p)) };
      return HttpResponse.json({ id: target.id });
    }),
    http.delete('*/api/v1/income/sources/:id/phases/:phaseId', ({ request, params }) => {
      sent.push({ method: 'DELETE', url: request.url });
      const target = current.phases.find((p) => p.id === params.phaseId);
      if (!target || new URL(request.url).searchParams.get('version') !== String(target.version))
        return HttpResponse.json({ status: 409, code: 'income.phase_conflict', title: 'Changed meanwhile.' }, { status: 409 });
      current = { ...current, phases: current.phases.filter((p) => p.id !== target.id) };
      return new HttpResponse(null, { status: 204 });
    }),
  );
  return { sent, bump: (id: string, netAmount?: string) => { current = { ...current, phases: current.phases.map((p) => (p.id === id ? { ...p, version: p.version + 1, ...(netAmount ? { netAmount: { amount: netAmount, currency: 'EUR' } } : {}) } : p)) }; } };
}

/** The `⋯` of a label's `row`-th phase in the timeline, newest first. */
const menuOf = (label: string, row = 0) => screen.getAllByRole('button', { name: new RegExp(`^Actions de la période ${label}`) })[row]!;
const findMenu = (label: string) => screen.findAllByRole('button', { name: new RegExp(`^Actions de la période ${label}`) });
/** The header's `⋯`, then « Ajouter une composante ». */
const openAdd = async (user: Awaited<ReturnType<typeof renderApp>>['user']) => {
  await user.click(await screen.findByRole('button', { name: 'Autres actions' }));
  await user.click(await screen.findByRole('menuitem', { name: 'Ajouter une composante' }));
};
/** The row's `⋯` menu, then its item. */
const choose = async (user: Awaited<ReturnType<typeof renderApp>>['user'], label: string, item: string, row = 0) => {
  await user.click(menuOf(label, row));
  await user.click(await screen.findByRole('menuitem', { name: item }));
};

describe('phase actions', () => {
  beforeAll(async () => { await import('../IncomeSourceDetailPage'); }, 30_000);

  it('adds a phase from the header menu and announces it', async () => {
    const { sent } = serveStateful();
    const { user, container } = await renderApp(`/income/${SALARY}`);

    await openAdd(user);
    const form = screen.getByRole('form', { name: 'Ajouter une période' });
    await user.type(within(form).getByLabelText('Libellé'), 'Prime de vacances');
    await user.type(within(form).getByLabelText('Montant net'), '500');
    await pick(user, 'Fréquence', 'Chaque année');
    await user.clear(within(form).getByLabelText('Début'));
    await user.type(within(form).getByLabelText('Début'), '2026-07-01');
    await expectNoAxeViolations(container);
    await user.click(within(form).getByRole('button', { name: 'Ajouter la période' }));

    await waitFor(() => expect(toasts()).toHaveTextContent('Période ajoutée.'));
    expect(sent[0]).toMatchObject({ method: 'POST', body: { label: 'Prime de vacances', startsOn: '2026-07-01', netAmount: { amount: '500', currency: 'EUR' }, every: { count: 1, unit: 'year' } } });
    expect(screen.getByRole('heading', { name: 'Changements' })).toHaveFocus();
  });

  it('corrects a phase with the version it read, then announces it', async () => {
    const { sent } = serveStateful();
    const { user, container } = await renderApp(`/income/${SALARY}`);

    await findMenu('Prime annuelle');
    await choose(user, 'Prime annuelle', 'Corriger');
    const form = screen.getByRole('form', { name: 'Corriger la période' });
    expect(within(form).getByLabelText('Montant net')).toHaveValue('1800');
    expect(within(form).getByLabelText('Fréquence')).toHaveTextContent('Chaque année');
    await user.clear(within(form).getByLabelText('Montant net'));
    await user.type(within(form).getByLabelText('Montant net'), '1900');
    await expectNoAxeViolations(container);
    await user.click(within(form).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(toasts()).toHaveTextContent('Période corrigée.'));
    expect(sent[0]).toMatchObject({ method: 'PUT', body: { label: 'Prime annuelle', version: 0, netAmount: { amount: '1900', currency: 'EUR' } } });
  });

  it('gives the focus back to the row menu when the correction is cancelled', async () => {
    serveStateful();
    const { user } = await renderApp(`/income/${SALARY}`);

    await findMenu('Prime annuelle');
    await choose(user, 'Prime annuelle', 'Corriger');
    await user.click(within(screen.getByRole('form', { name: 'Corriger la période' })).getByRole('button', { name: 'Annuler' }));

    await waitFor(() => expect(menuOf('Prime annuelle')).toHaveFocus());
  });

  it('deletes a phase after a confirmation, with the version it read', async () => {
    const { sent } = serveStateful();
    const { user } = await renderApp(`/income/${SALARY}`);

    await findMenu('Prime annuelle');
    await choose(user, 'Prime annuelle', 'Supprimer');
    expect(await screen.findByText('Cette période sera retirée.')).toBeInTheDocument();
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));

    await waitFor(() => expect(toasts()).toHaveTextContent('Période supprimée.'));
    await waitFor(() => expect(screen.queryByRole('button', { name: /^Actions de la période Prime annuelle/ })).not.toBeInTheDocument());
    expect(new URL(sent[0]!.url).searchParams.get('version')).toBe('0');
  });

  it('shows a correction refused because the phase changed meanwhile, with « Recharger »', async () => {
    const { bump } = serveStateful();
    const { user } = await renderApp(`/income/${SALARY}`);

    await findMenu('Prime annuelle');
    bump('p2');
    await choose(user, 'Prime annuelle', 'Corriger');
    const form = screen.getByRole('form', { name: 'Corriger la période' });
    await user.click(within(form).getByRole('button', { name: 'Enregistrer' }));

    expect(await within(form).findByText('Cette période a changé entre-temps : rechargez la page.')).toBeInTheDocument();
    expect(within(form).getByRole('button', { name: 'Recharger' })).toBeInTheDocument();
  });

  it('reloads a refused correction with the server values, not the stale ones', async () => {
    const { bump } = serveStateful();
    const { user } = await renderApp(`/income/${SALARY}`);

    await findMenu('Prime annuelle');
    await choose(user, 'Prime annuelle', 'Corriger');
    const form = screen.getByRole('form', { name: 'Corriger la période' });
    bump('p2', '2000.00');
    await user.click(within(form).getByRole('button', { name: 'Enregistrer' }));
    expect(await within(form).findByText('Cette période a changé entre-temps : rechargez la page.')).toBeInTheDocument();
    await user.click(within(form).getByRole('button', { name: 'Recharger' }));

    const reloaded = await screen.findByRole('form', { name: 'Corriger la période' });
    await waitFor(() => expect(within(reloaded).getByLabelText('Montant net')).toHaveValue('2000'));
    expect(screen.queryByText('Cette période a changé entre-temps : rechargez la page.')).not.toBeInTheDocument();
  });

  it('shows a deletion refused because the phase changed meanwhile, with « Recharger »', async () => {
    const { bump } = serveStateful();
    const { user } = await renderApp(`/income/${SALARY}`);

    await findMenu('Prime annuelle');
    bump('p2');
    await choose(user, 'Prime annuelle', 'Supprimer');
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));

    expect(await screen.findByText('Cette période a changé entre-temps : rechargez la page.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Recharger' })).toBeInTheDocument();
  });

  it('shows a deletion of a phase deleted meanwhile as not found, with « Recharger »', async () => {
    serveStateful();
    server.use(http.delete('*/api/v1/income/sources/:id/phases/:phaseId', () => HttpResponse.json({ status: 404, title: 'Not found' }, { status: 404 })));
    const { user } = await renderApp(`/income/${SALARY}`);

    await findMenu('Prime annuelle');
    await choose(user, 'Prime annuelle', 'Supprimer');
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));

    expect(await screen.findByText('Cet élément est introuvable.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Recharger' })).toBeInTheDocument();
  });

  it('puts the focus in the add form on « Libellé » when « Renseigner la suite » opens it', async () => {
    serveStateful(detail({
      phases: [phase({ endsOn: '2026-12-31' })],
      gapSoon: [{ label: 'Salaire de base', endsOn: '2026-12-31' }],
    }));
    const { user } = await renderApp(`/income/${SALARY}`);

    await user.click(await screen.findByRole('button', { name: 'Renseigner la suite' }));

    expect(within(screen.getByRole('form', { name: 'Ajouter une période' })).getByLabelText('Libellé')).toHaveFocus();
  });

  it('opens the add form prefilled from a gap: same label, starting on its end, same cadence and amount', async () => {
    serveStateful(detail({
      phases: [phase({ endsOn: '2026-12-31', every: { count: 3, unit: 'month' }, netAmount: { amount: '900.00', currency: 'EUR' } })],
      gapSoon: [{ label: 'Salaire de base', endsOn: '2026-12-31' }],
    }));
    const { user } = await renderApp(`/income/${SALARY}`);

    await user.click(await screen.findByRole('button', { name: 'Renseigner la suite' }));
    const form = screen.getByRole('form', { name: 'Ajouter une période' });
    expect(within(form).getByLabelText('Libellé')).toHaveValue('Salaire de base');
    expect(within(form).getByLabelText('Début')).toHaveValue('31/12/2026');
    expect(within(form).getByLabelText('Montant net')).toHaveValue('900');
    expect(within(form).getByLabelText('Fréquence')).toHaveTextContent('Chaque trimestre');
  });

  it('shows a server overlap under the start date of the add form', async () => {
    serveStateful();
    server.use(http.post('*/api/v1/income/sources/:id/phases', () => HttpResponse.json(
      { status: 422, code: 'income.phase_overlap', title: 'x', errors: [{ path: 'startsOn', code: 'income.phase_overlap', message: 'x' }] }, { status: 422 })));
    const { user } = await renderApp(`/income/${SALARY}`);

    await openAdd(user);
    const form = screen.getByRole('form', { name: 'Ajouter une période' });
    await user.type(within(form).getByLabelText('Libellé'), 'Salaire de base');
    await user.type(within(form).getByLabelText('Montant net'), '2400');
    await user.click(within(form).getByRole('button', { name: 'Ajouter la période' }));

    expect(await within(form).findByText('Une autre période de ce libellé couvre déjà une partie de ces dates.')).toBeInTheDocument();
    expect(within(form).queryByRole('alert')).not.toBeInTheDocument();
  });

  it.each([
    ['the other adult', { canWrite: false, visibility: 'shared' }, ['Afficher en tableau']],
    ['an ended source', { ended: true, endedOn: '2025-06-30' }, ['Rouvrir', 'Afficher en tableau']],
  ] as const)('gives %s no form and no phase action', async (_who, over, buttons) => {
    serveStateful(detail({ ...over, gapSoon: [{ label: 'Salaire de base', endsOn: '2026-12-31' }] }));
    const { container } = await renderApp(`/income/${SALARY}`);

    await screen.findByRole('heading', { name: 'Salaire Alice', level: 1 });
    await screen.findByRole('list', { name: 'Changements, du plus récent au plus ancien' });
    const article = screen.getByRole('article');
    // Only the chart's table toggle (and « Rouvrir » on an ended source).
    expect(within(article).queryAllByRole('button').map((b) => b.textContent)).toEqual(buttons);
    expect(within(article).queryByRole('textbox')).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });
});
