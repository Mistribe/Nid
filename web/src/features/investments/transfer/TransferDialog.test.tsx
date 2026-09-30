import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { detail, eur, list, PEA, summary } from '../testData';

const LIVRET = '0192f0aa-0000-7000-8000-00000000e002';

type Answer = (body: { from: string; to: string }) => Response;

/** Like the real server: both legs become flows (category « transfer »), so the source's next detail shows its leg. */
function serve(items: ReturnType<typeof summary>[], bodies: unknown[] = [], answer: Answer = () => HttpResponse.json({ id: 't1' }, { status: 201 })) {
  const current = detail();
  server.use(
    http.get('*/api/v1/investments/envelopes', () => HttpResponse.json(list(items))),
    http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json(current)),
    http.get('*/api/v1/investments/envelopes/:id/series', () => HttpResponse.json({ id: PEA, points: [] })),
    http.get('*/api/v1/investments/envelopes/:id/performance', () => HttpResponse.json({ from: '2025-01-01', to: '2025-10-15' })),
    http.post('*/api/v1/investments/transfers', async ({ request }) => {
      const body = await request.json() as { from: string; to: string; occurredOn: string; amount: { amount: string } };
      bodies.push(body);
      const response = answer(body);
      if (response.status === 201) {
        current.flows = [...current.flows, {
          id: 'leg1', occurredOn: body.occurredOn, kind: 'withdrawal', amount: eur(body.amount.amount), category: 'transfer',
          externalRef: null, note: null,
        }];
      }
      return response;
    }),
  );
}

const problem = (status: number, code: string | null) => () =>
  HttpResponse.json({ status, code, title: 'Problem' }, { status, headers: { 'Content-Type': 'application/problem+json' } });

describe('TransferDialog', () => {
  it('offers only open envelopes one can write, in the same currency, other than the source, and sends the transfer', async () => {
    const bodies: unknown[] = [];
    serve([
      summary(),
      summary({ id: LIVRET, displayName: 'Livret A', kind: 'savings' }),
      summary({ id: 'closed', displayName: 'Ancien', closed: true }),
      summary({ id: 'usd', displayName: 'Brokerage', currency: 'USD' }),
      summary({ id: 'ro', displayName: 'De Bob', canWrite: false }),
    ], bodies);
    const { user, container } = await renderApp(`/investments/${PEA}`);

    const open = await screen.findByRole('button', { name: 'Virer vers une autre enveloppe' });
    await user.click(open);
    const dialog = screen.getByRole('dialog', { name: 'Virement' });
    const options = within(await within(dialog).findByLabelText('Vers')).getAllByRole('option').map((o) => o.textContent);
    expect(options).toEqual(['Livret A']);
    await user.type(await within(dialog).findByLabelText('Montant'), '500');
    await expectNoAxeViolations(container);
    await user.click(within(dialog).getByRole('button', { name: 'Virer' }));

    expect(await screen.findByText('Virement enregistré.')).toBeInTheDocument();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(bodies).toEqual([expect.objectContaining({ from: PEA, to: LIVRET, amount: { amount: '500', currency: 'EUR' } })]);
    // The announcement is the Flux section's live region, and the source's refetch shows its withdrawal leg.
    const flows = screen.getByRole('table', { name: 'Flux de l’enveloppe' });
    expect(await within(flows).findByText('Virement')).toBeInTheDocument();
    expect(screen.getByText('Virement enregistré.')).toHaveAttribute('role', 'status');
    await waitFor(() => expect(open).toHaveFocus());
  });

  it('hands the focus back to the button when the dialog is closed', async () => {
    serve([summary(), summary({ id: LIVRET, displayName: 'Livret A' })]);
    const { user } = await renderApp(`/investments/${PEA}`);

    const open = await screen.findByRole('button', { name: 'Virer vers une autre enveloppe' });
    await user.click(open);
    await user.click(within(screen.getByRole('dialog', { name: 'Virement' })).getByRole('button', { name: 'Annuler' }));

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    await waitFor(() => expect(open).toHaveFocus());
  });

  it('says there is nowhere to transfer to instead of an empty list', async () => {
    serve([summary(), summary({ id: 'usd', displayName: 'Brokerage', currency: 'USD' })]);
    const { user } = await renderApp(`/investments/${PEA}`);

    await user.click(await screen.findByRole('button', { name: 'Virer vers une autre enveloppe' }));
    const dialog = screen.getByRole('dialog', { name: 'Virement' });
    expect(await within(dialog).findByText('Aucune autre enveloppe en EUR que vous pouvez modifier.')).toBeInTheDocument();
    expect(within(dialog).queryByRole('button', { name: 'Virer' })).not.toBeInTheDocument();
  });

  it('says a destination that became unavailable', async () => {
    serve([summary(), summary({ id: LIVRET, displayName: 'Livret A' })], [], problem(404, null));
    const { user } = await renderApp(`/investments/${PEA}`);

    await user.click(await screen.findByRole('button', { name: 'Virer vers une autre enveloppe' }));
    const dialog = screen.getByRole('dialog', { name: 'Virement' });
    await user.type(await within(dialog).findByLabelText('Montant'), '10');
    await user.click(within(dialog).getByRole('button', { name: 'Virer' }));

    expect(await within(dialog).findByText('Cette enveloppe n’est plus disponible.')).toBeInTheDocument();
  });

  it('says a closed envelope with its reload', async () => {
    serve([summary(), summary({ id: LIVRET, displayName: 'Livret A' })], [], problem(409, 'investments.envelope_closed'));
    const { user } = await renderApp(`/investments/${PEA}`);

    await user.click(await screen.findByRole('button', { name: 'Virer vers une autre enveloppe' }));
    const dialog = screen.getByRole('dialog', { name: 'Virement' });
    await user.type(await within(dialog).findByLabelText('Montant'), '10');
    await user.click(within(dialog).getByRole('button', { name: 'Virer' }));

    expect(await within(dialog).findByRole('button', { name: 'Recharger' })).toBeInTheDocument();
  });

  it('refuses a zero amount before sending', async () => {
    const bodies: unknown[] = [];
    serve([summary(), summary({ id: LIVRET, displayName: 'Livret A' })], bodies);
    const { user } = await renderApp(`/investments/${PEA}`);

    await user.click(await screen.findByRole('button', { name: 'Virer vers une autre enveloppe' }));
    const dialog = screen.getByRole('dialog', { name: 'Virement' });
    await user.type(await within(dialog).findByLabelText('Montant'), '0');
    await user.click(within(dialog).getByRole('button', { name: 'Virer' }));

    expect(await within(dialog).findByText('Saisissez un montant positif comme 1234,56.')).toBeInTheDocument();
    expect(bodies).toEqual([]);
  });

  it('offers no transfer on an envelope one cannot write', async () => {
    serve([summary()]);
    server.use(http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json(detail({ canWrite: false }))));
    await renderApp(`/investments/${PEA}`);

    await screen.findByRole('heading', { name: 'PEA', level: 1 });
    expect(screen.queryByRole('button', { name: 'Virer vers une autre enveloppe' })).not.toBeInTheDocument();
  });
});
