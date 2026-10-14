import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import type { components } from '@/api/schema';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { todayIso } from '../../subscriptions/today';
import type { PropertyDetail, PropertyTenancy } from '../api';
import { PROPERTY, propertyDetail, rentedFigures, tenancy } from '../testData';

type Body = components['schemas']['PropertyTenancyBody'];
/** An amount as the server writes it back: two decimals. */
const written = (wire: string) => { const [whole, decimals = ''] = wire.split('.'); return `${whole}.${decimals.padEnd(2, '0')}`; };
const ENDED = tenancy({
  id: 't1', version: 1, label: 'T2 vide', startsOn: '2021-01-01', endsOn: '2023-01-01', rentMonthly: '800.00', chargesMonthly: '0.00',
  deposit: null, note: null, current: false,
});
const OPEN = '9999-12-31';

/**
 * As the server: tenancies listed by start; a stale version is `409 realestate.tenancy_conflict`, an unknown tenancy 404;
 * two tenancies never overlap, [start, end), answered `422 realestate.tenancy_overlap` at `startsOn`; `current` covers today.
 */
function serve(initial: PropertyDetail = propertyDetail({ figures: rentedFigures(), tenancies: [ENDED, tenancy()] })) {
  let current = initial;
  let next = 3;
  const reads = { detail: 0 };
  const sent: { method: string; url: string; body?: Body }[] = [];
  const view = (id: string, b: Body, version: number): PropertyTenancy => ({
    id, version, label: b.label!, startsOn: b.startsOn!, endsOn: b.endsOn ?? null, rentMonthly: written(b.rentMonthly!),
    chargesMonthly: written(b.chargesMonthly ?? '0'), deposit: b.deposit ? written(b.deposit) : null, note: b.note ?? null,
    current: b.startsOn! <= todayIso() && todayIso() < (b.endsOn ?? OPEN),
  });
  const byStart = (all: PropertyTenancy[]) => [...all].sort((a, b) => a.startsOn.localeCompare(b.startsOn));
  const overlaps = (b: Body, self?: string) => current.tenancies.some((x) => x.id !== self
    && x.startsOn < (b.endsOn ?? OPEN) && b.startsOn! < (x.endsOn ?? OPEN));
  const overlap = () => HttpResponse.json(
    { status: 422, code: 'realestate.tenancy_overlap', title: 'x', errors: [{ path: 'startsOn', code: 'realestate.tenancy_overlap', message: 'x' }] },
    { status: 422 });
  const conflict = () => HttpResponse.json({ status: 409, code: 'realestate.tenancy_conflict', title: 'Changed.' }, { status: 409 });
  const missing = () => HttpResponse.json({ status: 404, code: null, title: 'Not found' }, { status: 404 });
  server.use(
    http.get('*/api/v1/realestate/properties/:id', () => { reads.detail += 1; return HttpResponse.json(current); }),
    http.post('*/api/v1/realestate/properties/:id/tenancies', async ({ request }) => {
      const body = (await request.json()) as Body;
      sent.push({ method: 'POST', url: request.url, body });
      if (overlaps(body)) return overlap();
      const id = `t${next++}`;
      // The first tenancy of a property not rented makes it rented from its start.
      const figures = current.figures.rentedSince ? current.figures : rentedFigures({ rentedSince: body.startsOn! });
      current = { ...current, figures, tenancies: byStart([...current.tenancies, view(id, body, 1)]) };
      return HttpResponse.json({ id }, { status: 201 });
    }),
    http.put('*/api/v1/realestate/properties/:id/tenancies/:tenancyId', async ({ request, params }) => {
      const body = (await request.json()) as Body;
      sent.push({ method: 'PUT', url: request.url, body });
      const old = current.tenancies.find((x) => x.id === params.tenancyId);
      if (!old) return missing();
      if (old.version !== body.version) return conflict();
      if (overlaps(body, old.id)) return overlap();
      current = { ...current, tenancies: byStart(current.tenancies.map((x) => (x.id === old.id ? view(old.id, body, old.version + 1) : x))) };
      return HttpResponse.json({ id: old.id });
    }),
    http.delete('*/api/v1/realestate/properties/:id/tenancies/:tenancyId', ({ request, params }) => {
      sent.push({ method: 'DELETE', url: request.url });
      const old = current.tenancies.find((x) => x.id === params.tenancyId);
      if (!old) return missing();
      if (new URL(request.url).searchParams.get('version') !== String(old.version)) return conflict();
      current = { ...current, tenancies: current.tenancies.filter((x) => x.id !== old.id) };
      return new HttpResponse(null, { status: 204 });
    }),
  );
  return {
    sent, reads,
    change: (id: string, over: Partial<PropertyTenancy>) => {
      current = { ...current, tenancies: current.tenancies.map((x) => (x.id === id ? { ...x, ...over } : x)) };
    },
  };
}

const region = () => screen.findByRole('region', { name: 'Location' });
const rows = async () => within(await screen.findByRole('table', { name: 'Baux du bien' })).getAllByRole('row').slice(1);

describe('tenancies', () => {
  // The lazy route chunk loaded cold inside the first test races its timeouts under load.
  beforeAll(async () => { await import('../PropertyDetailPage'); }, 30_000);

  it('lists the tenancies newest first: label, period, rent, charges, deposit, note, « En cours » on the one covering today', async () => {
    serve();
    const { container } = await renderApp(`/properties/${PROPERTY}`);

    const [running, ended] = await rows();
    expect(running).toHaveTextContent('T2 meublé');
    expect(within(running!).getByText('En cours')).toBeInTheDocument();
    expect(running).toHaveTextContent('depuis le 1 janvier 2023');
    expect(running).toHaveTextContent('900,00 €');
    expect(running).toHaveTextContent('50,00 €');
    expect(running).toHaveTextContent('1 800,00 €');
    expect(running).toHaveTextContent('bail de 3 ans');
    expect(ended).toHaveTextContent('T2 vide');
    expect(ended).toHaveTextContent('du 1 janvier 2021 au 1 janvier 2023');
    expect(ended).toHaveTextContent('800,00 €');
    expect(ended).not.toHaveTextContent('En cours');
    await expectNoAxeViolations(container);
  });

  it('adds a tenancy to a property not rented: the rent typed with a comma goes out with a dot, charges 0, no deposit nor end', async () => {
    const { sent } = serve(propertyDetail());
    const { user, container } = await renderApp(`/properties/${PROPERTY}`);

    const section = await region();
    expect(within(section).getByText('Ce bien n’est pas loué.')).toBeInTheDocument();
    expect(within(section).queryByRole('table')).not.toBeInTheDocument();
    await user.click(within(section).getByRole('button', { name: 'Ajouter un bail' }));
    const form = screen.getByRole('form', { name: 'Nouveau bail' });
    expect(within(form).getByLabelText('Libellé')).toHaveFocus();
    expect(within(form).getByText('Pas de nom de locataire : une colocation est un seul bail.')).toBeInTheDocument();
    expect(within(form).getByLabelText('Début')).toHaveValue(todayIso());
    expect(within(form).getByLabelText('Charges mensuelles')).toHaveValue('0');
    await user.type(within(form).getByLabelText('Libellé'), 'T2 meublé');
    await user.clear(within(form).getByLabelText('Début'));
    await user.type(within(form).getByLabelText('Début'), '2021-01-01');
    await user.type(within(form).getByLabelText('Loyer mensuel'), '900,5');
    await expectNoAxeViolations(container);
    await user.click(within(form).getByRole('button', { name: 'Ajouter' }));

    await waitFor(async () => expect(within(await region()).getByRole('status')).toHaveTextContent('Bail ajouté.'));
    await waitFor(() => {
      expect(sent).toEqual([expect.objectContaining({
        method: 'POST', body: { label: 'T2 meublé', startsOn: '2021-01-01', rentMonthly: '900.5', chargesMonthly: '0' },
      })]);
      expect(screen.getByRole('heading', { name: 'Location' })).toHaveFocus();
    });
    await waitFor(async () => expect((await rows())[0]).toHaveTextContent('900,50 €'));
  });

  it('ends a tenancy by correcting it with an end, the version it opened with, an emptied deposit left out', async () => {
    const { sent } = serve();
    const { user } = await renderApp(`/properties/${PROPERTY}`);

    await user.click(within((await rows())[0]!).getByRole('button', { name: 'Corriger' }));
    const form = screen.getByRole('form', { name: 'Corriger le bail' });
    expect(within(form).getByLabelText('Fin')).toHaveFocus();
    expect(within(form).getByLabelText('Loyer mensuel')).toHaveValue('900.00');
    await user.type(within(form).getByLabelText('Fin'), '2025-12-31');
    await user.clear(within(form).getByLabelText('Dépôt de garantie'));
    await user.click(within(form).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(async () => expect(within(await region()).getByRole('status')).toHaveTextContent('Bail corrigé.'));
    await waitFor(() => {
      expect(sent[0]).toMatchObject({ method: 'PUT' });
      expect(sent[0]!.url).toContain('/tenancies/t2');
      expect(sent[0]!.body).toEqual({
        label: 'T2 meublé', startsOn: '2023-01-01', endsOn: '2025-12-31', rentMonthly: '900.00', chargesMonthly: '50.00',
        note: 'bail de 3 ans', version: 3,
      });
    });
    await waitFor(async () => {
      expect((await rows())[0]).toHaveTextContent('du 1 janvier 2023 au 31 décembre 2025');
      expect((await rows())[0]).not.toHaveTextContent('En cours');
    });
  });

  it('reopening an ended tenancy sends no end, and the overlap it creates reads under « Début »', async () => {
    const { sent } = serve();
    const { user } = await renderApp(`/properties/${PROPERTY}`);

    await user.click(within((await rows())[1]!).getByRole('button', { name: 'Corriger' }));
    const form = screen.getByRole('form', { name: 'Corriger le bail' });
    await user.clear(within(form).getByLabelText('Fin'));
    await user.click(within(form).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => {
      expect(within(form).getByText('Ce bail chevauche un autre bail du bien.')).toBeInTheDocument();
      expect(within(form).getByLabelText('Début')).toHaveAttribute('aria-invalid', 'true');
      expect(within(form).queryByRole('alert')).not.toBeInTheDocument();
      expect(sent[0]!.body).not.toHaveProperty('endsOn');
      expect(sent[0]!.body).toMatchObject({ version: 1 });
    });
  });

  it('refuses an end on or before the start before sending', async () => {
    const { sent } = serve(propertyDetail());
    const { user } = await renderApp(`/properties/${PROPERTY}`);

    await user.click(await within(await region()).findByRole('button', { name: 'Ajouter un bail' }));
    const form = screen.getByRole('form', { name: 'Nouveau bail' });
    await user.type(within(form).getByLabelText('Libellé'), 'T2');
    await user.clear(within(form).getByLabelText('Début'));
    await user.type(within(form).getByLabelText('Début'), '2024-01-01');
    await user.type(within(form).getByLabelText('Fin'), '2024-01-01');
    await user.type(within(form).getByLabelText('Loyer mensuel'), '900');
    await user.click(within(form).getByRole('button', { name: 'Ajouter' }));

    await waitFor(() => {
      expect(within(form).getByText('La fin doit venir après le début.')).toBeInTheDocument();
      expect(within(form).getByLabelText('Fin')).toHaveAttribute('aria-invalid', 'true');
    });
    expect(sent).toEqual([]);
  });

  it('deletes a tenancy after a confirmation, with the version it read', async () => {
    const { sent } = serve();
    const { user } = await renderApp(`/properties/${PROPERTY}`);

    await user.click(within((await rows())[0]!).getByRole('button', { name: 'Supprimer' }));
    expect(screen.getByText('Ce bail sera supprimé.')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Confirmer' }));

    await waitFor(async () => expect(within(await region()).getByRole('status')).toHaveTextContent('Bail supprimé.'));
    await waitFor(async () => {
      expect(sent[0]!.url).toContain('/tenancies/t2?version=3');
      expect(await rows()).toHaveLength(1);
    });
  });

  it('sends the version it opened with though the property was read again meanwhile, and « Recharger » closes onto the stored row', async () => {
    const { sent, change, reads } = serve();
    const { user, queryClient } = await renderApp(`/properties/${PROPERTY}`);

    await user.click(within((await rows())[0]!).getByRole('button', { name: 'Corriger' }));
    change('t2', { rentMonthly: '950.00', version: 4 });
    await queryClient.invalidateQueries();
    await waitFor(() => expect(reads.detail).toBeGreaterThan(1));
    const form = screen.getByRole('form', { name: 'Corriger le bail' });
    await user.click(within(form).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(within(form).getByText('Ce bail a changé entre-temps : rechargez la page.')).toBeInTheDocument());
    await waitFor(() => expect(sent.map((s) => s.body?.version)).toEqual([3]));
    await user.click(within(form).getByRole('button', { name: 'Recharger' }));

    await waitFor(() => expect(screen.queryByRole('form', { name: 'Corriger le bail' })).not.toBeInTheDocument());
    await waitFor(async () => expect((await rows())[0]).toHaveTextContent('950,00 €'));
    await waitFor(async () => expect(within((await rows())[0]!).getByRole('button', { name: 'Corriger' })).toHaveFocus());
  });

  it('gives a reader the figures and the tenancies without any action', async () => {
    serve(propertyDetail({ canWrite: false, figures: rentedFigures(), tenancies: [ENDED, tenancy()] }));
    await renderApp(`/properties/${PROPERTY}`);

    const section = await region();
    await rows();
    expect(within(section).getByText('En cours')).toBeInTheDocument();
    expect(within(section).queryByRole('button')).not.toBeInTheDocument();
    expect(within(section).queryByRole('columnheader', { name: 'Actions' })).not.toBeInTheDocument();
  });
});
