import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import type { components } from '@/api/schema';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { PROPERTY, propertyDetail, rentedFigures, tenancy } from '../testData';

const PAGE = `/properties/${PROPERTY}?tab=rental`;
const due: components['schemas']['TenancyRevisionView'] = {
  state: 'due', anniversary: '2026-09-01', fromQuarter: '2025-Q2', fromValue: '145.17', toQuarter: '2026-Q2', toValue: '148.37',
  currentRent: '900.00', newRent: '919.84', defaultEffectiveOn: '2026-09-01', dpeUnknown: false,
  irlChangePct: 3.5, cpi: { percent: 2.1, inflationTo: '2026-06', missing: null },
};

function serve(revision = due, answer?: () => Response) {
  const sent: unknown[] = [];
  server.use(
    http.get('*/api/v1/realestate/properties/:id', () => HttpResponse.json(propertyDetail({
      figures: rentedFigures(), tenancies: [tenancy({ indexed: true, irlReferenceQuarter: 2, revision })],
    }))),
    http.post('*/api/v1/realestate/properties/:id/tenancies/:tenancyId/revision', async ({ request }) => {
      sent.push(await request.json());
      return answer ? answer() : HttpResponse.json({ id: 'new' }, { status: 201 });
    }),
  );
  return sent;
}

const open = async (user: Awaited<ReturnType<typeof renderApp>>['user']) => {
  await user.click(await screen.findByRole('button', { name: 'Réviser le loyer' }));
  return within(await screen.findByRole('dialog', { name: 'Réviser le loyer' }));
};

describe('revision dialog', () => {
  beforeAll(async () => { await import('../PropertyDetailPage'); }, 30_000);

  it('offers the revision on the row, shows the IRL values and the rents as received, and posts the default date', async () => {
    const sent = serve();
    const { user } = await renderApp(PAGE);

    expect(await screen.findByText('Révision possible')).toBeInTheDocument();
    const dialog = await open(user);
    expect(dialog.getByText(/IRL 2025-T2 : 145.17 · IRL 2026-T2 : 148.37/)).toBeInTheDocument();
    expect(dialog.getByText(/Loyer actuel 900,00 € · nouveau loyer 919,84 €/)).toBeInTheDocument();
    expect(dialog.getByLabelText('Date d’effet')).toHaveValue('01/09/2026');
    expect(dialog.getByLabelText('Date d’effet')).toHaveAccessibleDescription('Les loyers dus à partir de cette date passent au nouveau montant.');
    expect(dialog.queryByText(/DPE non renseigné/)).not.toBeInTheDocument();
    await user.click(dialog.getByRole('button', { name: 'Réviser' }));

    await waitFor(() => expect(sent).toEqual([{ effectiveOn: '2026-09-01' }]));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });

  it('compares the IRL with the inflation of the same period, or says why it cannot', async () => {
    serve();
    const first = await renderApp(PAGE);
    expect((await open(first.user)).getByText('IRL +3,5 % · inflation sur la même période +2,1 % (jusqu’à fin juin 2026)')).toBeInTheDocument();
    first.unmount();

    serve({ ...due, cpi: { percent: null, inflationTo: null, missing: 'not_published' } });
    const second = await renderApp(PAGE);
    expect((await open(second.user)).getByText('IRL +3,5 % · inflation pas encore publiée pour cette période')).toBeInTheDocument();
  });

  it('says when the DPE is not given', async () => {
    serve({ ...due, dpeUnknown: true });
    const { user } = await renderApp(PAGE);

    expect((await open(user)).getByText('DPE non renseigné : la révision est interdite en F ou G')).toBeInTheDocument();
  });

  it('shows the server’s refusal of the date on the field', async () => {
    serve(due, () => HttpResponse.json({
      status: 422, code: 'realestate.revision_effective_on', title: 'x',
      errors: [{ path: 'effectiveOn', code: 'realestate.revision_effective_on', message: 'x' }],
    }, { status: 422 }));
    const { user } = await renderApp(PAGE);

    const dialog = await open(user);
    await user.click(dialog.getByRole('button', { name: 'Réviser' }));
    expect(await dialog.findByText(/La date d’effet doit être comprise/)).toBeInTheDocument();
    expect(screen.getByRole('dialog')).toBeInTheDocument();
  });
});
