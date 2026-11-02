import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { detail, SALARY } from '../testData';

function serve(bodies: unknown[], answer: () => Response = () => HttpResponse.json({ id: SALARY })) {
  server.use(
    http.get('*/api/v1/income/sources/:id', () => HttpResponse.json(detail())),
    http.patch('*/api/v1/income/sources/:id', async ({ request }) => { bodies.push(await request.json()); return answer(); }),
  );
}

const settings = () => screen.getByRole('form', { name: 'Réglages' });
const settingsRegion = () => screen.getByRole('region', { name: 'Réglages' });

describe('income settings', () => {
  beforeAll(async () => { await import('../IncomeSourceDetailPage'); }, 30_000);

  it('sends only what changed, every clear flag stated, and says it is saved', async () => {
    const bodies: unknown[] = [];
    serve(bodies);
    const { user, container } = await renderApp(`/income/${SALARY}?tab=settings`);

    await screen.findByRole('heading', { name: 'Salaire Alice', level: 1 });
    await pick(user, 'Type', 'Pension');
    await user.clear(within(settings()).getByLabelText(/Payeur/));
    await expectNoAxeViolations(container);
    await user.click(within(settings()).getByRole('button', { name: 'Enregistrer les réglages' }));

    await waitFor(() => expect(within(settingsRegion()).getByRole('status')).toHaveTextContent('Réglages enregistrés.'));
    expect(bodies).toEqual([{ kind: 'pension', clearPayerLabel: true, clearNote: false }]);
  });

  it('says there is nothing to save rather than sending an empty change', async () => {
    const bodies: unknown[] = [];
    serve(bodies);
    const { user } = await renderApp(`/income/${SALARY}?tab=settings`);

    await screen.findByRole('heading', { name: 'Salaire Alice', level: 1 });
    await user.click(within(settings()).getByRole('button', { name: 'Enregistrer les réglages' }));

    await waitFor(() => expect(within(settingsRegion()).getByRole('status')).toHaveTextContent('Aucune modification à enregistrer.'));
    expect(bodies).toEqual([]);
  });

  it('shows a server refusal under the field it names', async () => {
    serve([], () => HttpResponse.json(
      { status: 422, code: 'income.invalid_source', title: 'x', errors: [{ path: 'payerLabel', code: 'income.invalid_source', message: 'x' }] },
      { status: 422 }));
    const { user } = await renderApp(`/income/${SALARY}?tab=settings`);

    await screen.findByRole('heading', { name: 'Salaire Alice', level: 1 });
    await user.type(within(settings()).getByLabelText(/Payeur/), ' SA');
    await user.click(within(settings()).getByRole('button', { name: 'Enregistrer les réglages' }));

    expect(await within(settings()).findByText('Cette valeur n’est pas acceptée.')).toBeInTheDocument();
    expect(within(settings()).queryByRole('alert')).not.toBeInTheDocument();
  });

  it('refuses an empty name before sending', async () => {
    const bodies: unknown[] = [];
    serve(bodies);
    const { user } = await renderApp(`/income/${SALARY}?tab=settings`);

    await screen.findByRole('heading', { name: 'Salaire Alice', level: 1 });
    await user.clear(within(settings()).getByLabelText('Nom'));
    await user.click(within(settings()).getByRole('button', { name: 'Enregistrer les réglages' }));

    expect(await within(settings()).findByText('Ce champ est obligatoire.')).toBeInTheDocument();
    expect(bodies).toEqual([]);
  });

  it('clears the previous « saved » status when a later save is refused client-side', async () => {
    serve([]);
    const { user } = await renderApp(`/income/${SALARY}?tab=settings`);

    await screen.findByRole('heading', { name: 'Salaire Alice', level: 1 });
    await pick(user, 'Type', 'Pension');
    await user.click(within(settings()).getByRole('button', { name: 'Enregistrer les réglages' }));
    await waitFor(() => expect(within(settingsRegion()).getByRole('status')).toHaveTextContent('Réglages enregistrés.'));
    await user.clear(within(settings()).getByLabelText('Nom'));
    await user.click(within(settings()).getByRole('button', { name: 'Enregistrer les réglages' }));

    expect(await within(settings()).findByText('Ce champ est obligatoire.')).toBeInTheDocument();
    expect(within(settingsRegion()).getByRole('status')).toBeEmptyDOMElement();
  });
});
