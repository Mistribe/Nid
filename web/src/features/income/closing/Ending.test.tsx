import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { todayIso } from '../../subscriptions/today';
import type { IncomeSourceDetail } from '../api';
import { detail, phase, SALARY } from '../testData';

function serve(initial: IncomeSourceDetail = detail()) {
  let current = initial;
  const bodies: { endedOn: string }[] = [];
  server.use(
    http.get('*/api/v1/income/sources/:id', () => HttpResponse.json(current)),
    http.post('*/api/v1/income/sources/:id/close', async ({ request }) => {
      const body = (await request.json()) as { endedOn: string };
      bodies.push(body);
      if (current.phases.some((p) => p.startsOn > body.endedOn))
        return HttpResponse.json({ status: 422, code: 'income.invalid_source', title: 'x', errors: [{ path: 'endedOn', code: 'income.invalid_source', message: 'x' }] }, { status: 422 });
      current = {
        ...current, ended: true, endedOn: body.endedOn,
        phases: current.phases.map((p) => (p.status === 'running' ? { ...p, endsOn: body.endedOn, status: 'past' } : p)),
      };
      return HttpResponse.json({ id: SALARY });
    }),
    http.post('*/api/v1/income/sources/:id/reopen', () => {
      current = { ...current, ended: false, endedOn: null };
      return HttpResponse.json({ id: SALARY });
    }),
  );
  return { bodies, end: () => { current = { ...current, ended: true, endedOn: '2025-06-30' }; } };
}

/** The header's `⋯` menu, then « Terminer cette source ». */
const openEnd = async (user: Awaited<ReturnType<typeof renderApp>>['user']) => {
  await user.click(await screen.findByRole('button', { name: 'Autres actions' }));
  await user.click(await screen.findByRole('menuitem', { name: 'Terminer cette source' }));
};
const frDate = (iso: string) => iso.split('-').reverse().join('/');
const banner = () => screen.getByText('Source terminée', { exact: true }).closest('p')!;

describe('ending and reopening a source', () => {
  beforeAll(async () => { await import('../IncomeSourceDetailPage'); }, 30_000);

  it('ends the source today, says the effect, and lands on the banner', async () => {
    const { bodies } = serve();
    const { user, container } = await renderApp(`/income/${SALARY}`);

    await openEnd(user);
    const dialog = screen.getByRole('dialog', { name: 'Terminer la source' });
    expect(within(dialog).getByText('Les périodes en cours s’arrêteront à cette date ; la source passera dans les sources terminées.')).toBeInTheDocument();
    expect(within(dialog).getByLabelText('Date de fin')).toHaveValue(frDate(todayIso()));
    await expectNoAxeViolations(dialog);
    await user.click(within(dialog).getByRole('button', { name: 'Terminer' }));

    await waitFor(() => expect(banner()).toHaveFocus());
    expect(bodies).toEqual([{ endedOn: todayIso() }]);
    expect(screen.getByText('Source terminée.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Autres actions' })).not.toBeInTheDocument();
    expect(within(banner()).getByRole('button', { name: 'Rouvrir' })).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('refuses a date before a phase’s start under the date, and keeps the dialog open', async () => {
    serve(detail({ phases: [...detail().phases, phase({ id: 'p3', startsOn: '2099-01-01', status: 'upcoming' })] }));
    const { user } = await renderApp(`/income/${SALARY}`);

    await openEnd(user);
    const dialog = screen.getByRole('dialog', { name: 'Terminer la source' });
    await user.click(within(dialog).getByRole('button', { name: 'Terminer' }));

    expect(await within(dialog).findByText('Une période commence après cette date : choisissez une date plus tardive ou supprimez cette période.')).toBeInTheDocument();
    expect(screen.getByRole('dialog', { name: 'Terminer la source' })).toBeInTheDocument();
  });

  it('refuses a date after today before sending', async () => {
    const { bodies } = serve();
    const { user } = await renderApp(`/income/${SALARY}`);

    await openEnd(user);
    const dialog = screen.getByRole('dialog', { name: 'Terminer la source' });
    await user.clear(within(dialog).getByLabelText('Date de fin'));
    await user.type(within(dialog).getByLabelText('Date de fin'), todayIso(new Date(Date.now() + 86_400_000)));
    await user.click(within(dialog).getByRole('button', { name: 'Terminer' }));

    expect(await within(dialog).findByText('Cette date est dans le futur.')).toBeInTheDocument();
    expect(bodies).toEqual([]);
  });

  it('closes a dialog left open when the source turns ended elsewhere, and never loses the focus', async () => {
    const state = serve();
    const { user, queryClient } = await renderApp(`/income/${SALARY}`);

    await user.click(await screen.findByRole('button', { name: 'Autres actions' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Ajouter une composante' }));
    expect(screen.getByRole('dialog', { name: 'Ajouter une période' })).toBeInTheDocument();
    state.end();
    await queryClient.invalidateQueries();

    await waitFor(() => expect(banner()).toHaveFocus());
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it.each([['Annuler'], ['Escape']])('closes on %s without ending, the focus back on the ⋯ menu', async (how) => {
    const { bodies } = serve();
    const { user } = await renderApp(`/income/${SALARY}`);

    await openEnd(user);
    // The browser focuses the first field of a modal dialog itself; jsdom does not, and Escape reaches the dialog only from inside it.
    if (how === 'Escape') { await user.click(screen.getByLabelText('Date de fin')); await user.keyboard('{Escape}'); }
    else await user.click(within(screen.getByRole('dialog', { name: 'Terminer la source' })).getByRole('button', { name: 'Annuler' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(bodies).toEqual([]);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Autres actions' })).toHaveFocus());
  });

  it('reopens an ended source, says the cut phases stay cut, and lands on the periods', async () => {
    serve(detail({ ended: true, endedOn: '2025-06-30' }));
    const { user, container } = await renderApp(`/income/${SALARY}`);

    await user.click(await screen.findByRole('button', { name: 'Rouvrir' }));

    expect(await screen.findByText('Source rouverte. Les périodes arrêtées à la fin restent arrêtées : ajoutez la suite si besoin.')).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole('heading', { name: 'Changements' })).toHaveFocus());
    expect(screen.getByRole('button', { name: 'Autres actions' })).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('offers no « Terminer cette source » to a reader', async () => {
    serve(detail({ canWrite: false, visibility: 'shared' }));
    await renderApp(`/income/${SALARY}`);

    await screen.findByRole('heading', { name: 'Salaire Alice', level: 1 });
    expect(screen.queryByRole('button', { name: 'Terminer cette source' })).not.toBeInTheDocument();
  });

  it('offers no « Rouvrir » to a reader of an ended source', async () => {
    serve(detail({ canWrite: false, visibility: 'shared', ended: true, endedOn: '2025-06-30' }));
    await renderApp(`/income/${SALARY}`);

    await screen.findByText('Source terminée', { exact: true });
    expect(screen.queryByRole('button', { name: 'Rouvrir' })).not.toBeInTheDocument();
  });
});
