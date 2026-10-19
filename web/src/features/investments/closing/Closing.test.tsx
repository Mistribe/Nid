import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { todayIso } from '../../subscriptions/today';
import { detail, eur, figures, list, PEA, summary } from '../testData';

type Answer = (body: { closedOn: string }) => Response | undefined;

/** Like the real server: a close sets `closed` and adds the 0 value on its date; a reopen clears it. */
function serve(initial = detail(), bodies: unknown[] = [], answer: Answer = () => undefined, reopenAnswer: () => Response | undefined = () => undefined) {
  const current = initial;
  server.use(
    http.get('*/api/v1/investments/envelopes', () => HttpResponse.json(list([summary()]))),
    http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json(current)),
    http.get('*/api/v1/investments/envelopes/:id/series', () => HttpResponse.json({ id: PEA, points: [] })),
    http.get('*/api/v1/investments/envelopes/:id/performance', () => HttpResponse.json({ from: '2025-01-01', to: '2025-10-15' })),
    http.post('*/api/v1/investments/envelopes/:id/close', async ({ request }) => {
      const body = await request.json() as { closedOn: string; finalWithdrawal?: { amount: string } };
      bodies.push(body);
      const refused = answer(body);
      if (refused) return refused;
      current.closed = true;
      current.valuations = [...current.valuations, { id: 'zero', asOf: body.closedOn, amount: eur('0.0000'), note: null }];
      if (body.finalWithdrawal) {
        current.flows = [...current.flows, {
          id: 'final', occurredOn: body.closedOn, kind: 'withdrawal', amount: eur(`${body.finalWithdrawal.amount}.0000`), category: null, externalRef: null, note: null,
        }];
      }
      return HttpResponse.json({ id: PEA });
    }),
    http.post('*/api/v1/investments/envelopes/:id/reopen', () => {
      const refused = reopenAnswer();
      if (refused) return refused;
      current.closed = false;
      return HttpResponse.json({ id: PEA });
    }),
  );
}

const problem = (status: number, code: string | null, extra: object = {}) => () =>
  HttpResponse.json({ status, code, title: 'Problem', ...extra }, { status, headers: { 'Content-Type': 'application/problem+json' } });

const frToday = () => todayIso().split('-').reverse().join('/');

const openDialog = async (user: Awaited<ReturnType<typeof renderApp>>['user']) => {
  await user.click(await screen.findByRole('button', { name: 'Autres actions' }));
  await user.click(await screen.findByRole('menuitem', { name: 'Clôturer l’enveloppe' }));
  return screen.findByRole('dialog', { name: 'Clôturer l’enveloppe' });
};

describe('closing an envelope', () => {
  // The lazy route chunk loaded cold inside the first test races its timeouts under load.
  beforeAll(async () => { await import('../EnvelopeDetailPage'); }, 30_000);

  it('closes at today\'s date without a withdrawal, says the effect, and lands on the banner', async () => {
    const bodies: unknown[] = [];
    serve(detail(), bodies);
    const { user, container } = await renderApp(`/investments/${PEA}`);

    const dialog = await openDialog(user);
    expect(within(dialog).getByText('Une valeur de 0 sera enregistrée à cette date, et l’enveloppe quittera la liste. Vous pourrez la rouvrir.')).toBeInTheDocument();
    expect(within(dialog).getByLabelText('Date de clôture')).toHaveValue(frToday());
    await expectNoAxeViolations(document.body);
    await user.click(within(dialog).getByRole('button', { name: 'Clôturer' }));

    expect(await screen.findByText('Enveloppe clôturée.')).toHaveAttribute('role', 'status');
    expect(bodies).toEqual([{ closedOn: todayIso() }]);
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    const banner = await screen.findByText('Enveloppe clôturée');
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Autres actions' })).not.toBeInTheDocument());
    await waitFor(() => expect(banner).toHaveFocus());
    await expectNoAxeViolations(container);
  });

  it('sends the final withdrawal in the envelope currency', async () => {
    const bodies: unknown[] = [];
    serve(detail(), bodies);
    const { user, container } = await renderApp(`/investments/${PEA}`);

    const dialog = await openDialog(user);
    await user.type(within(dialog).getByLabelText('Dernier retrait'), '510');
    await user.click(within(dialog).getByRole('button', { name: 'Clôturer' }));

    await screen.findByText('Enveloppe clôturée.');
    expect(bodies).toEqual([{ closedOn: todayIso(), finalWithdrawal: { amount: '510', currency: 'EUR' } }]);
    await user.click(await screen.findByRole('tab', { name: /^Flux/ }));
    await waitFor(() => expect(screen.getByRole('table', { name: 'Flux de l’enveloppe' })).toHaveTextContent('510,00 €'));
    await expectNoAxeViolations(container);
  });

  it('refuses a zero withdrawal before sending', async () => {
    const bodies: unknown[] = [];
    serve(detail(), bodies);
    const { user } = await renderApp(`/investments/${PEA}`);

    const dialog = await openDialog(user);
    await user.type(within(dialog).getByLabelText('Dernier retrait'), '0');
    await user.click(within(dialog).getByRole('button', { name: 'Clôturer' }));

    expect(await within(dialog).findByText('Saisissez un montant positif comme 1234,56.')).toBeInTheDocument();
    expect(bodies).toEqual([]);
  });

  it('places a closing before the last value or flow on the closing date', async () => {
    serve(detail(), [], () => problem(422, 'investments.invalid_envelope', { errors: [{ path: 'closedOn', code: 'investments.invalid_envelope', message: 'x' }] })());
    const { user } = await renderApp(`/investments/${PEA}`);

    const dialog = await openDialog(user);
    await user.click(within(dialog).getByRole('button', { name: 'Clôturer' }));

    expect(await within(dialog).findByLabelText('Date de clôture')).toBeInvalid();
    expect(within(dialog).getByText('La clôture ne peut pas précéder la dernière valeur ou le dernier flux.')).toBeInTheDocument();
    expect(within(dialog).queryByText('Problem')).not.toBeInTheDocument();
    await expectNoAxeViolations(document.body);
  });

  it('says an envelope already closed, with its reload', async () => {
    serve(detail(), [], problem(409, 'investments.envelope_closed'));
    const { user } = await renderApp(`/investments/${PEA}`);

    const dialog = await openDialog(user);
    await user.click(within(dialog).getByRole('button', { name: 'Clôturer' }));

    expect(await within(dialog).findByText('Cette enveloppe est clôturée.')).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Recharger' })).toBeInTheDocument();
  });

  it('hands the focus back to the ⋯ menu when the dialog is cancelled', async () => {
    serve();
    const { user } = await renderApp(`/investments/${PEA}`);

    const button = await screen.findByRole('button', { name: 'Autres actions' });
    await user.click(button);
    await user.click(await screen.findByRole('menuitem', { name: 'Clôturer l’enveloppe' }));
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Annuler' }));

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    await waitFor(() => expect(button).toHaveFocus());
  });
});

describe('a correction open when the envelope closes', () => {
  it('goes away with its form, the focus landing on the banner', async () => {
    serve();
    server.use(http.post('*/api/v1/investments/envelopes/:id/valuations', problem(409, 'investments.envelope_closed')));
    const { user } = await renderApp(`/investments/${PEA}?tab=values`);

    const values = await screen.findByRole('table', { name: 'Valeurs de l’enveloppe' });
    await user.click(within(values).getByRole('button', { name: /^Actions pour le 30 septembre 2025/ }));
    await user.click(await screen.findByRole('menuitem', { name: 'Corriger' }));
    const form = await screen.findByRole('form', { name: 'Corriger la valeur' });
    await user.click(within(form).getByRole('button', { name: 'Enregistrer' }));
    server.use(http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json(detail({ closed: true }))));
    await user.click(await within(form).findByRole('button', { name: 'Recharger' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    await waitFor(() => expect(screen.getByText('Enveloppe clôturée')).toHaveFocus());
    expect(screen.queryByRole('button', { name: /^Actions pour/ })).not.toBeInTheDocument();
  });
});

describe('a closed envelope', () => {
  it('shows the banner and no write affordance', async () => {
    serve(detail({ closed: true, figures: figures({ missingOpening: true }) }));
    const { container } = await renderApp(`/investments/${PEA}`);

    const article = await screen.findByRole('article');
    expect(await within(article).findByText('Enveloppe clôturée')).toBeInTheDocument();
    expect(within(article).getByRole('button', { name: 'Rouvrir' })).toBeInTheDocument();
    for (const name of ['Nouvelle valeur', 'Nouveau flux', 'Réglages']) expect(within(article).queryByRole('form', { name })).not.toBeInTheDocument();
    for (const name of ['Saisir une valeur', 'Ajouter un flux', 'Autres actions', 'Saisir les versements antérieurs'])
      expect(within(article).queryByRole('button', { name })).not.toBeInTheDocument();
    expect(within(article).queryByRole('tab', { name: 'Réglages' })).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('reopens and announces it, the focus going to « Saisir une valeur »', async () => {
    serve(detail({ closed: true }));
    const { user, container } = await renderApp(`/investments/${PEA}`);

    await user.click(await screen.findByRole('button', { name: 'Rouvrir' }));

    expect(await screen.findByText('Enveloppe rouverte.')).toHaveAttribute('role', 'status');
    expect(await screen.findByRole('button', { name: 'Autres actions' })).toBeInTheDocument();
    expect(screen.queryByText('Enveloppe clôturée')).not.toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole('button', { name: 'Saisir une valeur' })).toHaveFocus());
    await expectNoAxeViolations(container);
  });

  it('says an envelope that is not closed when reopening', async () => {
    serve(detail({ closed: true }), [], () => undefined, problem(409, 'investments.envelope_open'));
    const { user } = await renderApp(`/investments/${PEA}`);

    await user.click(await screen.findByRole('button', { name: 'Rouvrir' }));

    expect(await screen.findByText('Cette enveloppe n’est pas clôturée.')).toBeInTheDocument();
    expect(screen.queryByText('Problem')).not.toBeInTheDocument();
  });

  it('shows the banner without « Rouvrir » to a reader', async () => {
    serve(detail({ closed: true, canWrite: false }));
    const { container } = await renderApp(`/investments/${PEA}`);

    expect(await screen.findByText('Enveloppe clôturée')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Rouvrir' })).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });
});
