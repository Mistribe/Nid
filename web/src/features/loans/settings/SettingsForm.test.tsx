import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { delay } from 'msw/utils/delay';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { toasts } from '@/test/toasts';
import { PROPERTY, propertyList, propertySummary } from '../../properties/testData';
import type { LoanDetail } from '../api';
import { LOAN, loanDetail, schedule } from '../testData';

function serve(bodies: unknown[], answer: () => Response = () => HttpResponse.json({ id: LOAN }), detail: LoanDetail = loanDetail()) {
  const reads = { detail: 0 };
  server.use(
    http.get('*/api/v1/realestate/loans/:id', () => { reads.detail += 1; return HttpResponse.json(detail); }),
    http.get('*/api/v1/realestate/loans/:id/schedule', () => HttpResponse.json(schedule())),
    http.patch('*/api/v1/realestate/loans/:id', async ({ request }) => { bodies.push(await request.json()); return answer(); }),
  );
  return reads;
}

const PAGE = `/loans/${LOAN}?tab=contract`;
const settings = () => screen.getByRole('form', { name: 'Réglages' });
const save = () => within(settings()).getByRole('button', { name: 'Enregistrer les réglages' });

describe('loan settings', () => {
  beforeAll(async () => { await import('../LoanDetailPage'); }, 30_000);

  it('sends only what changed, every clear flag stated, says it is saved and reads the loan again', async () => {
    const bodies: unknown[] = [];
    const reads = serve(bodies);
    const { user, container } = await renderApp(PAGE);

    await screen.findByRole('heading', { name: 'Prêt maison', level: 1 });
    await user.clear(within(settings()).getByLabelText('Prêteur'));
    await user.type(within(settings()).getByLabelText('Note'), 'taux renégocié');
    await expectNoAxeViolations(container);
    await user.click(save());

    await waitFor(() => expect(toasts()).toHaveTextContent('Réglages enregistrés.'));
    expect(bodies).toEqual([{ note: 'taux renégocié', clearLender: true, clearNote: false, clearProperty: false }]);
    await waitFor(() => expect(reads.detail).toBeGreaterThan(1));
  });

  it('says there is nothing to save rather than sending an empty change', async () => {
    const bodies: unknown[] = [];
    serve(bodies);
    const { user } = await renderApp(PAGE);

    await screen.findByRole('heading', { name: 'Prêt maison', level: 1 });
    await user.click(save());

    await waitFor(() => expect(toasts()).toHaveTextContent('Aucune modification à enregistrer.'));
    expect(bodies).toEqual([]);
  });

  it('shows a server refusal under the field it names', async () => {
    serve([], () => HttpResponse.json(
      { status: 422, code: 'realestate.invalid_loan', title: 'x', errors: [{ path: 'lender', code: 'realestate.invalid_loan', message: 'x' }] },
      { status: 422 }));
    const { user } = await renderApp(PAGE);

    await screen.findByRole('heading', { name: 'Prêt maison', level: 1 });
    await user.type(within(settings()).getByLabelText('Prêteur'), ' SA');
    await user.click(save());

    expect(await within(settings()).findByText('Cette valeur n’est pas acceptée.')).toBeInTheDocument();
    expect(within(settings()).queryByRole('alert')).not.toBeInTheDocument();
  });

  it('refuses an empty name before sending', async () => {
    const bodies: unknown[] = [];
    serve(bodies);
    const { user } = await renderApp(PAGE);

    await screen.findByRole('heading', { name: 'Prêt maison', level: 1 });
    await user.clear(within(settings()).getByLabelText('Nom'));
    await user.click(save());

    expect(await within(settings()).findByText('Ce champ est obligatoire.')).toBeInTheDocument();
    expect(bodies).toEqual([]);
  });

  const STUDIO = '0192f0bb-0000-7000-8000-00000000e002';
  const linked = () => loanDetail({ propertyId: PROPERTY, propertyName: 'Maison de Nantes' });
  const twoProperties = () => {
    let reads = 0;
    server.use(http.get('*/api/v1/realestate/properties', () => {
      reads += 1;
      return HttpResponse.json(propertyList([propertySummary(), propertySummary({ id: STUDIO, name: 'Studio' })]));
    }));
    return () => reads;
  };
  const saved = () => waitFor(() => expect(toasts()).toHaveTextContent('Réglages enregistrés.'));

  it('prefills « Bien financé », changes the link, and reads the loans and the properties again', async () => {
    const bodies: unknown[] = [];
    serve(bodies, undefined, linked());
    const propertyReads = twoProperties();
    const { user, container } = await renderApp(PAGE);

    await screen.findByRole('heading', { name: 'Prêt maison', level: 1 });
    await waitFor(() => expect(propertyReads()).toBeGreaterThan(0));
    expect(within(settings()).getByLabelText('Bien financé')).toHaveTextContent('Maison de Nantes');
    await pick(user, 'Bien financé', 'Studio');
    await expectNoAxeViolations(container);
    await user.click(save());

    await saved();
    expect(bodies).toEqual([{ propertyId: STUDIO, clearLender: false, clearNote: false, clearProperty: false }]);
    await waitFor(() => expect(propertyReads()).toBeGreaterThan(1));
  });

  it('removes the link with « Aucun », which sends clearProperty', async () => {
    const bodies: unknown[] = [];
    serve(bodies, undefined, linked());
    twoProperties();
    const { user } = await renderApp(PAGE);

    await screen.findByRole('heading', { name: 'Prêt maison', level: 1 });
    await pick(user, 'Bien financé', 'Aucun');
    await user.click(save());

    await saved();
    expect(bodies).toEqual([{ clearLender: false, clearNote: false, clearProperty: true }]);
  });

  it('keeps a linked property the reader cannot read when saving something else', async () => {
    const bodies: unknown[] = [];
    serve(bodies, undefined, loanDetail({ propertyId: PROPERTY, propertyName: null }));
    const { user } = await renderApp(PAGE);

    await screen.findByRole('heading', { name: 'Prêt maison', level: 1 });
    expect(within(settings()).getByLabelText('Bien financé')).toHaveTextContent('Un bien que vous ne pouvez pas consulter');
    await user.type(within(settings()).getByLabelText('Note'), 'taux renégocié');
    await user.click(save());

    await saved();
    expect(bodies).toEqual([{ note: 'taux renégocié', clearLender: false, clearNote: false, clearProperty: false }]);
  });

  it('shows the linked property while the list still loads, and a save then sends no clearProperty', async () => {
    const bodies: unknown[] = [];
    serve(bodies, undefined, linked());
    server.use(http.get('*/api/v1/realestate/properties', async () => { await delay('infinite'); return HttpResponse.json(propertyList([])); }));
    const { user } = await renderApp(PAGE);

    await screen.findByRole('heading', { name: 'Prêt maison', level: 1 });
    expect(within(settings()).getByLabelText('Bien financé')).toHaveTextContent('Maison de Nantes');
    await user.type(within(settings()).getByLabelText('Note'), 'x');
    await user.click(save());

    await saved();
    expect(bodies).toEqual([{ note: 'x', clearLender: false, clearNote: false, clearProperty: false }]);
  });

  it('shows realestate.invalid_property under « Bien financé »', async () => {
    serve([], () => HttpResponse.json(
      { status: 422, code: 'realestate.invalid_property', title: 'x', errors: [{ path: 'propertyId', code: 'realestate.invalid_property', message: 'x' }] },
      { status: 422 }));
    twoProperties();
    const { user } = await renderApp(PAGE);

    await screen.findByRole('heading', { name: 'Prêt maison', level: 1 });
    await pick(user, 'Bien financé', 'Studio');
    await user.click(save());

    expect(await within(settings()).findByText('Ce bien n’existe pas ou n’est pas accessible.')).toBeInTheDocument();
    expect(within(settings()).queryByRole('alert')).not.toBeInTheDocument();
  });
});
