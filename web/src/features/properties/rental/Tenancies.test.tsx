import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import type { components } from '@/api/schema';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { toasts } from '@/test/toasts';
import { todayIso } from '@/lib/today';
import type { PropertyDetail, PropertyTenancy } from '@/api/properties';
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
  // « Location » shows for a rental only.
  let current: PropertyDetail = { ...initial, type: 'rental' };
  let next = 3;
  const reads = { detail: 0 };
  const sent: { method: string; url: string; body?: Body }[] = [];
  const ident = (sent: string | null | undefined, stored: string | null | undefined) => (sent === undefined || sent === null ? (stored ?? null) : (sent || null));
  const view = (id: string, b: Body, version: number, old?: PropertyTenancy): PropertyTenancy => ({
    id, version, label: b.label!, startsOn: b.startsOn!, endsOn: b.endsOn ?? null, rentMonthly: written(b.rentMonthly!),
    chargesMonthly: written(b.chargesMonthly ?? '0'), deposit: b.deposit ? written(b.deposit) : null, note: b.note ?? null,
    current: b.startsOn! <= todayIso() && todayIso() < (b.endsOn ?? OPEN), indexed: b.indexed ?? false, irlReferenceQuarter: b.irlReferenceQuarter ?? null,
    // Tri-state on a PUT, as the server: absent keeps the stored value, "" clears it.
    tenantNames: ident(b.tenantNames, old?.tenantNames), landlordName: ident(b.landlordName, old?.landlordName),
    landlordAddress: ident(b.landlordAddress, old?.landlordAddress), rentsTrackedFrom: ident(b.rentsTrackedFrom, old?.rentsTrackedFrom),
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
      current = { ...current, tenancies: byStart(current.tenancies.map((x) => (x.id === old.id ? view(old.id, body, old.version + 1, old) : x))) };
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

const PAGE = `/properties/${PROPERTY}?tab=rental`;
const fr = (iso: string) => iso.split('-').reverse().join('/');
type User = Awaited<ReturnType<typeof renderApp>>['user'];
/** The row's ⋯ menu, opened, then the item picked. */
const rowAction = async (user: User, row: HTMLElement, date: string, item: string) => {
  await user.click(within(row).getByRole('button', { name: new RegExp(`^Actions pour le ${date}`) }));
  await user.click(await screen.findByRole('menuitem', { name: item }));
};
const region = () => screen.findByRole('region', { name: 'Location' });
const rows = async () => within(await screen.findByRole('table', { name: 'Baux du bien' })).getAllByRole('row').slice(1);

describe('tenancies', () => {
  // The lazy route chunk loaded cold inside the first test races its timeouts under load.
  beforeAll(async () => { await import('../PropertyDetailPage'); }, 30_000);

  it('lists the tenancies newest first: label, period, rent, charges, deposit, note, « En cours » on the one covering today', async () => {
    serve();
    const { container } = await renderApp(PAGE);

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
    const { user, container } = await renderApp(PAGE);

    const section = await region();
    expect(within(section).getByText('Ce bien n’est pas loué.')).toBeInTheDocument();
    expect(within(section).queryByRole('table')).not.toBeInTheDocument();
    await user.click(within(section).getByRole('button', { name: 'Ajouter un bail' }));
    const form = within(await screen.findByRole('dialog', { name: 'Nouveau bail' }));
    await waitFor(() => expect(form.getByLabelText('Libellé')).toHaveFocus());
    expect(form.getByText('Pas de nom de locataire ici, il a son champ : une colocation est un seul bail.')).toBeInTheDocument();
    expect(form.getByLabelText('Début')).toHaveValue(fr(todayIso()));
    expect(form.getByLabelText('Charges mensuelles')).toHaveValue('0');
    await user.type(form.getByLabelText('Libellé'), 'T2 meublé');
    await user.clear(form.getByLabelText('Début'));
    await user.type(form.getByLabelText('Début'), '01/01/2021');
    await user.type(form.getByLabelText('Loyer mensuel'), '900,5');
    await expectNoAxeViolations(container);
    await user.click(form.getByRole('button', { name: 'Ajouter' }));

    await waitFor(() => expect(toasts()).toHaveTextContent('Bail ajouté.'));
    await waitFor(() => {
      expect(sent).toEqual([expect.objectContaining({
        method: 'POST', body: { label: 'T2 meublé', startsOn: '2021-01-01', rentMonthly: '900.5', chargesMonthly: '0', indexed: false },
      })]);
      expect(screen.getByRole('heading', { name: 'Location' })).toHaveFocus();
    });
    await waitFor(async () => expect((await rows())[0]).toHaveTextContent('900,50 €'));
    await expectNoAxeViolations(container);
  });

  it('ends a tenancy by correcting it with an end, the version it opened with, an emptied deposit left out', async () => {
    const { sent } = serve();
    const { user, container } = await renderApp(PAGE);

    await rowAction(user, (await rows())[0]!, '1 janvier 2023', 'Corriger');
    const form = within(await screen.findByRole('dialog', { name: 'Corriger le bail' }));
        expect(form.getByLabelText('Loyer mensuel')).toHaveValue('900,00');
    await expectNoAxeViolations(container);
    await user.type(form.getByLabelText('Fin'), '31/12/2025');
    await user.clear(form.getByLabelText('Dépôt de garantie'));
    await user.click(form.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(toasts()).toHaveTextContent('Bail corrigé.'));
    await waitFor(() => {
      expect(sent[0]).toMatchObject({ method: 'PUT' });
      expect(sent[0]!.url).toContain('/tenancies/t2');
      expect(sent[0]!.body).toEqual({
        label: 'T2 meublé', startsOn: '2023-01-01', endsOn: '2025-12-31', rentMonthly: '900.00', chargesMonthly: '50.00',
        note: 'bail de 3 ans', version: 3, indexed: false, tenantNames: '', landlordName: '', landlordAddress: '', rentsTrackedFrom: '',
      });
    });
    await waitFor(async () => {
      expect((await rows())[0]).toHaveTextContent('du 1 janvier 2023 au 31 décembre 2025');
      expect((await rows())[0]).not.toHaveTextContent('En cours');
    });
    await expectNoAxeViolations(container);
  });

  it('proposes the owners as landlord on a new lease only, and sends the identity typed', async () => {
    server.use(http.get('*/api/v1/core/persons', () => HttpResponse.json([
      { id: '01926b9e-0000-7000-8000-00000000000a', kind: 'adult', displayName: 'Alice Dev', email: null },
      { id: '01926b9e-0000-7000-8000-00000000000b', kind: 'adult', displayName: 'Bob Dev', email: null },
    ])));
    const { sent } = serve(propertyDetail({
      owners: [{ personId: '01926b9e-0000-7000-8000-00000000000a', shareBp: 5000 }, { personId: '01926b9e-0000-7000-8000-00000000000b', shareBp: 5000 }],
    }));
    const { user } = await renderApp(PAGE);

    await user.click(await screen.findByRole('button', { name: 'Ajouter un bail' }));
    const form = within(await screen.findByRole('dialog', { name: 'Nouveau bail' }));
    await user.click(form.getByRole('button', { name: /^Plus d’options/ }));
    await waitFor(() => expect(form.getByLabelText('Bailleur')).toHaveValue('Alice Dev et Bob Dev'));
    await user.type(form.getByLabelText('Libellé'), 'T2');
    await user.clear(form.getByLabelText('Début'));
    await user.type(form.getByLabelText('Début'), '01/01/2021');
    await user.type(form.getByLabelText('Loyer mensuel'), '900');
    await user.type(form.getByLabelText('Locataire(s)'), 'Mme Martin');
    await user.type(form.getByLabelText('Adresse du bailleur'), '1 rue A');
    await user.click(form.getByRole('button', { name: 'Ajouter' }));

    await waitFor(() => expect(sent[0]!.body).toMatchObject({ tenantNames: 'Mme Martin', landlordName: 'Alice Dev et Bob Dev', landlordAddress: '1 rue A' }));

    // A correction shows the stored identity, never the owners, and sends an emptied field as "" to clear it.
    await rowAction(user, (await rows())[0]!, '1 janvier 2021', 'Corriger');
    const edit = within(await screen.findByRole('dialog', { name: 'Corriger le bail' }));
    await user.click(edit.getByRole('button', { name: /^Plus d’options/ }));
    expect(edit.getByLabelText('Bailleur')).toHaveValue('Alice Dev et Bob Dev');
    await user.clear(edit.getByLabelText('Bailleur'));
    await user.click(edit.getByRole('button', { name: 'Enregistrer' }));
    await waitFor(() => expect(sent[1]!.body).toMatchObject({ tenantNames: 'Mme Martin', landlordName: '', landlordAddress: '1 rue A' }));
  });

  it('picks the month the rents are tracked from, among the lease\'s months, and clears it back to automatic', async () => {
    const { sent } = serve(propertyDetail());
    const { user } = await renderApp(PAGE);

    await user.click(await screen.findByRole('button', { name: 'Ajouter un bail' }));
    const form = within(await screen.findByRole('dialog', { name: 'Nouveau bail' }));
    await user.click(form.getByRole('button', { name: /^Plus d’options/ }));
    expect(form.getByText('Mois du premier loyer suivi dans Nid ; laissez vide pour le calcul automatique')).toBeInTheDocument();
    expect(form.getByRole('combobox', { name: 'Loyers suivis depuis' })).toHaveTextContent('Automatique');
    await user.type(form.getByLabelText('Libellé'), 'T2');
    await user.clear(form.getByLabelText('Début'));
    await user.type(form.getByLabelText('Début'), '15/01/2021');
    await user.type(form.getByLabelText('Loyer mensuel'), '900');
    await user.click(form.getByRole('combobox', { name: 'Loyers suivis depuis' }));
    const list = await screen.findByRole('listbox');
    // Nothing before the lease's own month.
    expect(within(list).queryByRole('option', { name: 'décembre 2020' })).not.toBeInTheDocument();
    await user.keyboard('{Escape}');
    await pick(user, 'Loyers suivis depuis', 'janvier 2021');
    await user.click(form.getByRole('button', { name: 'Ajouter' }));
    await waitFor(() => expect(sent[0]!.body).toMatchObject({ rentsTrackedFrom: '2021-01' }));

    // A creation left on automatic sends nothing; a correction back to automatic sends "" to clear it.
    await rowAction(user, (await rows())[0]!, '15 janvier 2021', 'Corriger');
    const edit = within(await screen.findByRole('dialog', { name: 'Corriger le bail' }));
    await user.click(edit.getByRole('button', { name: /^Plus d’options/ }));
    expect(edit.getByRole('combobox', { name: 'Loyers suivis depuis' })).toHaveTextContent('janvier 2021');
    await pick(user, 'Loyers suivis depuis', 'Automatique');
    await user.click(edit.getByRole('button', { name: 'Enregistrer' }));
    await waitFor(() => expect(sent[1]!.body).toMatchObject({ rentsTrackedFrom: '' }));
  });

  it('shows no proposed landlord when correcting a lease without one', async () => {
    serve();
    const { user } = await renderApp(PAGE);

    await rowAction(user, (await rows())[0]!, '1 janvier 2023', 'Corriger');
    const form = within(await screen.findByRole('dialog', { name: 'Corriger le bail' }));
    await user.click(form.getByRole('button', { name: /^Plus d’options/ }));
    expect(form.getByLabelText('Bailleur')).toHaveValue('');
  });

  it('reopening an ended tenancy sends no end, and the overlap it creates reads under « Début »', async () => {
    const { sent } = serve();
    const { user } = await renderApp(PAGE);

    await rowAction(user, (await rows())[1]!, '1 janvier 2021', 'Corriger');
    const form = within(await screen.findByRole('dialog', { name: 'Corriger le bail' }));
    await user.clear(form.getByLabelText('Fin'));
    await user.click(form.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => {
      expect(form.getByText('Ce bail chevauche un autre bail du bien.')).toBeInTheDocument();
      expect(form.getByLabelText('Début')).toHaveAttribute('aria-invalid', 'true');
      expect(form.queryByRole('alert')).not.toBeInTheDocument();
      expect(sent[0]!.body).not.toHaveProperty('endsOn');
      expect(sent[0]!.body).toMatchObject({ version: 1 });
    });
  });

  it('refuses an end on or before the start before sending', async () => {
    const { sent } = serve(propertyDetail());
    const { user } = await renderApp(PAGE);

    await user.click(await within(await region()).findByRole('button', { name: 'Ajouter un bail' }));
    const form = within(await screen.findByRole('dialog', { name: 'Nouveau bail' }));
    await user.type(form.getByLabelText('Libellé'), 'T2');
    await user.clear(form.getByLabelText('Début'));
    await user.type(form.getByLabelText('Début'), '01/01/2024');
    await user.type(form.getByLabelText('Fin'), '01/01/2024');
    await user.type(form.getByLabelText('Loyer mensuel'), '900');
    await user.click(form.getByRole('button', { name: 'Ajouter' }));

    await waitFor(() => {
      expect(form.getByText('La fin doit venir après le début.')).toBeInTheDocument();
      expect(form.getByLabelText('Fin')).toHaveAttribute('aria-invalid', 'true');
    });
    expect(sent).toEqual([]);
  });

  it('deletes a tenancy after a confirmation, with the version it read', async () => {
    const { sent } = serve();
    const { user, container } = await renderApp(PAGE);

    await rowAction(user, (await rows())[0]!, '1 janvier 2023', 'Supprimer');
    expect(await screen.findByText('Ce bail sera supprimé.')).toBeInTheDocument();
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));

    await waitFor(() => expect(toasts()).toHaveTextContent('Bail supprimé.'));
    await waitFor(async () => {
      expect(sent[0]!.url).toContain('/tenancies/t2?version=3');
      expect(await rows()).toHaveLength(1);
    });
    await expectNoAxeViolations(container);
  });

  it('sends the version it opened with though the property was read again meanwhile, and « Recharger » closes onto the stored row', async () => {
    const { sent, change, reads } = serve();
    const { user, queryClient } = await renderApp(PAGE);

    await rowAction(user, (await rows())[0]!, '1 janvier 2023', 'Corriger');
    change('t2', { rentMonthly: '950.00', version: 4 });
    await queryClient.invalidateQueries();
    await waitFor(() => expect(reads.detail).toBeGreaterThan(1));
    const form = within(await screen.findByRole('dialog', { name: 'Corriger le bail' }));
    await user.click(form.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(form.getByText('Ce bail a changé entre-temps : rechargez la page.')).toBeInTheDocument());
    await waitFor(() => expect(sent.map((s) => s.body?.version)).toEqual([3]));
    await user.click(form.getByRole('button', { name: 'Recharger' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    await waitFor(async () => expect((await rows())[0]).toHaveTextContent('950,00 €'));
    await waitFor(async () => expect(within((await rows())[0]!).getByRole('button', { name: /^Actions pour le 1 janvier 2023/ })).toHaveFocus());
  });

  it('gives a reader the figures and the tenancies without any action', async () => {
    serve(propertyDetail({ canWrite: false, figures: rentedFigures(), tenancies: [ENDED, tenancy()] }));
    await renderApp(PAGE);

    const section = await region();
    await rows();
    expect(within(section).getByText('En cours')).toBeInTheDocument();
    expect(within(section).queryByRole('button')).not.toBeInTheDocument();
    expect(within(section).queryByRole('columnheader', { name: 'Actions' })).not.toBeInTheDocument();
  });

  it('revises the rent each year: the reference quarter is the server\'s default unless picked under « personnaliser »', async () => {
    const { sent } = serve(propertyDetail());
    const { user } = await renderApp(PAGE);

    await user.click(await screen.findByRole('button', { name: 'Ajouter un bail' }));
    const form = within(await screen.findByRole('dialog', { name: 'Nouveau bail' }));
    await user.type(form.getByLabelText('Libellé'), 'T2 meublé');
    await user.clear(form.getByLabelText('Début'));
    await user.type(form.getByLabelText('Début'), '01/01/2021');
    await user.type(form.getByLabelText('Loyer mensuel'), '900');
    expect(form.getByRole('button', { name: 'À propos : IRL' })).toBeInTheDocument();
    await user.click(form.getByRole('switch', { name: 'Le loyer est-il révisé chaque année ?' }));
    expect(form.getByRole('button', { name: /^Trimestre de référence : par défaut, le dernier publié au début du bail/ })).toBeInTheDocument();
    expect(form.queryByRole('radio', { name: 'T2' })).not.toBeInTheDocument();
    await user.click(form.getByRole('button', { name: 'Ajouter' }));

    await waitFor(() => expect(sent[0]!.body).toMatchObject({ indexed: true }));
    expect(sent[0]!.body).not.toHaveProperty('irlReferenceQuarter');

    await rowAction(user, (await rows())[0]!, '1 janvier 2021', 'Corriger');
    const edit = within(await screen.findByRole('dialog', { name: 'Corriger le bail' }));
    await user.click(edit.getByRole('button', { name: /^Trimestre de référence : par défaut/ }));
    expect(edit.getByRole('button', { name: 'À propos : Trimestre de référence' })).toBeInTheDocument();
    await user.click(edit.getByRole('radio', { name: 'T2' }));
    expect(edit.getByRole('button', { name: 'Trimestre de référence : T2 · personnaliser' })).toBeInTheDocument();
    await user.click(edit.getByRole('button', { name: 'Enregistrer' }));
    await waitFor(() => expect(sent[1]!.body).toMatchObject({ indexed: true, irlReferenceQuarter: 2 }));
  });

  it('shows a revised lease as one row, its history unfolding, and undoes the last revision', async () => {
    const first = tenancy({ id: 'a', label: 'Bail', startsOn: '2023-01-01', endsOn: '2024-01-01', rentMonthly: '900.00', current: false, indexed: true, irlReferenceQuarter: 2 });
    const last = tenancy({ id: 'b', label: 'Bail', startsOn: '2024-01-01', rentMonthly: '927.00', indexed: true, irlReferenceQuarter: 2, revisedFrom: 'a' });
    let undone = 0;
    serve(propertyDetail({ figures: rentedFigures(), tenancies: [first, last] }));
    server.use(http.delete('*/api/v1/realestate/properties/:id/tenancies/:tenancyId/revision', ({ params }) => {
      undone += params.tenancyId === 'b' ? 1 : 0;
      return new HttpResponse(null, { status: 204 });
    }));
    const { user } = await renderApp(PAGE);

    const [row, ...others] = await rows();
    expect(others).toHaveLength(0);
    expect(row).toHaveTextContent('927,00 €');
    // The lease runs since its first link.
    expect(within(row!).getAllByRole('cell')[1]).toHaveTextContent('depuis le 1 janvier 2023');
    await user.click(within(row!).getByRole('button', { name: 'Historique des loyers' }));
    const history = within(row!).getByRole('list', { name: 'Historique des loyers' });
    expect(history).toHaveTextContent('depuis le 1 janvier 2024 · 927,00 €');
    expect(history).toHaveTextContent('depuis le 1 janvier 2023 · 900,00 €');

    await user.click(within(row!).getByRole('button', { name: 'Annuler la révision' }));
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));
    await waitFor(() => expect(undone).toBe(1));
  });

  it('opens the last link of a revised lease in the explorer', async () => {
    const first = tenancy({ id: 'a', label: 'Bail', startsOn: '2023-01-01', endsOn: '2024-01-01', rentMonthly: '900.00', current: false });
    const last = tenancy({ id: 'b', label: 'Bail', startsOn: '2024-01-01', rentMonthly: '927.00', revisedFrom: 'a' });
    serve(propertyDetail({ figures: rentedFigures(), tenancies: [first, last] }));
    const { user, router } = await renderApp(PAGE);

    const [row] = await rows();
    expect(screen.getAllByRole('link', { name: 'Voir la série' })).toHaveLength(1);
    await user.click(within(row!).getByRole('link', { name: 'Voir la série' }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/series'));
    expect(router.state.location.search).toMatchObject({ s: `tenancy:${PROPERTY}:b:rent,irl` });
  });

  it('corrects a revised link with its rent and start kept, and never deletes it', async () => {
    const first = tenancy({ id: 'a', label: 'Bail', startsOn: '2023-01-01', endsOn: '2024-01-01', rentMonthly: '900.00', current: false, indexed: true, irlReferenceQuarter: 2 });
    const last = tenancy({ id: 'b', label: 'Bail', startsOn: '2024-01-01', rentMonthly: '927.00', indexed: true, irlReferenceQuarter: 2, revisedFrom: 'a' });
    const { sent } = serve(propertyDetail({ figures: rentedFigures(), tenancies: [first, last] }));
    const { user } = await renderApp(PAGE);

    await user.click(within((await rows())[0]!).getByRole('button', { name: /^Actions pour le 1 janvier 2024/ }));
    expect(await screen.findByRole('menuitem', { name: 'Corriger' })).toBeInTheDocument();
    expect(screen.queryByRole('menuitem', { name: 'Supprimer' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('menuitem', { name: 'Corriger' }));
    const form = within(await screen.findByRole('dialog', { name: 'Corriger le bail' }));
    expect(form.getByLabelText('Loyer mensuel')).toHaveAttribute('readonly');
    expect(form.getByLabelText('Début')).toHaveAttribute('readonly');
    // Ending a lease is correcting it with an end: the chain's last link keeps that.
    expect(form.getByLabelText('Fin')).not.toHaveAttribute('readonly');
    await user.clear(form.getByLabelText('Libellé'));
    await user.type(form.getByLabelText('Libellé'), 'Bail Dupont');
    await user.click(form.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(sent[0]!.body).toMatchObject({ label: 'Bail Dupont', startsOn: '2024-01-01', rentMonthly: '927.00' }));
  });

  it('offers a revised link the months of its whole chain to track the rents from', async () => {
    const first = tenancy({ id: 'a', label: 'Bail', startsOn: '2023-01-01', endsOn: '2024-01-01', rentMonthly: '900.00', current: false, indexed: true, irlReferenceQuarter: 2 });
    const last = tenancy({ id: 'b', label: 'Bail', startsOn: '2024-01-01', rentMonthly: '927.00', indexed: true, irlReferenceQuarter: 2, revisedFrom: 'a' });
    const { sent } = serve(propertyDetail({ figures: rentedFigures(), tenancies: [first, last] }));
    const { user } = await renderApp(PAGE);

    await user.click(within((await rows())[0]!).getByRole('button', { name: /^Actions pour le 1 janvier 2024/ }));
    await user.click(await screen.findByRole('menuitem', { name: 'Corriger' }));
    const form = within(await screen.findByRole('dialog', { name: 'Corriger le bail' }));
    await user.click(form.getByRole('button', { name: /^Plus d’options/ }));
    await pick(user, 'Loyers suivis depuis', 'mars 2023');
    await user.click(form.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(sent[0]!.body).toMatchObject({ rentsTrackedFrom: '2023-03' }));
  });

  it.each([
    ['irl_missing', 'IRL du 2ᵉ trimestre 2026 manquant', '2026-Q2', true],
    ['irl_missing', 'IRL du 1ᵉʳ trimestre 2027 manquant', '2027-Q1', true],
    ['forbidden_dpe', 'Révision interdite (DPE F/G)', null, false],
  ])('says why no IRL revision is offered, a free one staying open but in F or G: %s', async (state, text, missingQuarter, free) => {
    const revision = { state, dpeUnknown: false, missingQuarter, cpi: { percent: null, inflationTo: null, missing: null } };
    serve(propertyDetail({ figures: rentedFigures(), tenancies: [tenancy({ indexed: true, irlReferenceQuarter: 2, revision })] }));
    await renderApp(PAGE);

    expect((await rows())[0]).toHaveTextContent(text);
    expect(screen.queryAllByRole('button', { name: 'Réviser le loyer' })).toHaveLength(free ? 1 : 0);
  });

  it('says a revision was missed', async () => {
    const revision = { state: 'none', dpeUnknown: false, lostAnniversary: '2025-01-01', cpi: { percent: null, inflationTo: null, missing: null } };
    serve(propertyDetail({ figures: rentedFigures(), tenancies: [tenancy({ indexed: true, irlReferenceQuarter: 2, revision })] }));
    await renderApp(PAGE);

    expect((await rows())[0]).toHaveTextContent('Révision du 1 janvier 2025 non faite');
  });
});
