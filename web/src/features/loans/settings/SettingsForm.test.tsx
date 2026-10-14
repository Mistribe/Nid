import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { LOAN, loanDetail, schedule } from '../testData';

function serve(bodies: unknown[], answer: () => Response = () => HttpResponse.json({ id: LOAN })) {
  const reads = { detail: 0 };
  server.use(
    http.get('*/api/v1/realestate/loans/:id', () => { reads.detail += 1; return HttpResponse.json(loanDetail()); }),
    http.get('*/api/v1/realestate/loans/:id/schedule', () => HttpResponse.json(schedule())),
    http.patch('*/api/v1/realestate/loans/:id', async ({ request }) => { bodies.push(await request.json()); return answer(); }),
  );
  return reads;
}

const settings = () => screen.getByRole('form', { name: 'Réglages' });
const settingsRegion = () => screen.getByRole('region', { name: 'Réglages' });
const save = () => within(settings()).getByRole('button', { name: 'Enregistrer les réglages' });

describe('loan settings', () => {
  beforeAll(async () => { await import('../LoanDetailPage'); }, 30_000);

  it('sends only what changed, every clear flag stated, says it is saved and reads the loan again', async () => {
    const bodies: unknown[] = [];
    const reads = serve(bodies);
    const { user, container } = await renderApp(`/loans/${LOAN}`);

    await screen.findByRole('heading', { name: 'Prêt maison', level: 1 });
    await user.clear(within(settings()).getByLabelText('Prêteur'));
    await user.type(within(settings()).getByLabelText('Note'), 'taux renégocié');
    await expectNoAxeViolations(container);
    await user.click(save());

    await waitFor(() => expect(within(settingsRegion()).getByRole('status')).toHaveTextContent('Réglages enregistrés.'));
    expect(bodies).toEqual([{ note: 'taux renégocié', clearLender: true, clearNote: false, clearProperty: false }]);
    await waitFor(() => expect(reads.detail).toBeGreaterThan(1));
  });

  it('says there is nothing to save rather than sending an empty change', async () => {
    const bodies: unknown[] = [];
    serve(bodies);
    const { user } = await renderApp(`/loans/${LOAN}`);

    await screen.findByRole('heading', { name: 'Prêt maison', level: 1 });
    await user.click(save());

    await waitFor(() => expect(within(settingsRegion()).getByRole('status')).toHaveTextContent('Aucune modification à enregistrer.'));
    expect(bodies).toEqual([]);
  });

  it('shows a server refusal under the field it names', async () => {
    serve([], () => HttpResponse.json(
      { status: 422, code: 'realestate.invalid_loan', title: 'x', errors: [{ path: 'lender', code: 'realestate.invalid_loan', message: 'x' }] },
      { status: 422 }));
    const { user } = await renderApp(`/loans/${LOAN}`);

    await screen.findByRole('heading', { name: 'Prêt maison', level: 1 });
    await user.type(within(settings()).getByLabelText('Prêteur'), ' SA');
    await user.click(save());

    expect(await within(settings()).findByText('Cette valeur n’est pas acceptée.')).toBeInTheDocument();
    expect(within(settings()).queryByRole('alert')).not.toBeInTheDocument();
  });

  it('refuses an empty name before sending', async () => {
    const bodies: unknown[] = [];
    serve(bodies);
    const { user } = await renderApp(`/loans/${LOAN}`);

    await screen.findByRole('heading', { name: 'Prêt maison', level: 1 });
    await user.clear(within(settings()).getByLabelText('Nom'));
    await user.click(save());

    expect(await within(settings()).findByText('Ce champ est obligatoire.')).toBeInTheDocument();
    expect(bodies).toEqual([]);
  });
});
