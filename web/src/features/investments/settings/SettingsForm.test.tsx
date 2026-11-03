import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { toasts } from '@/test/toasts';
import { detail, list, PEA, summary } from '../testData';

type Patch = Record<string, unknown>;
type Answer = (body: Patch) => Response | undefined;

/** Like the real server: a patch is applied to the envelope the next detail read returns. */
function serve(initial = detail(), bodies: Patch[] = [], answer: Answer = () => undefined) {
  const current = initial;
  server.use(
    http.get('*/api/v1/investments/envelopes', () => HttpResponse.json(list([summary()]))),
    http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json(current)),
    http.get('*/api/v1/investments/envelopes/:id/series', () => HttpResponse.json({ id: PEA, points: [] })),
    http.get('*/api/v1/investments/envelopes/:id/performance', () => HttpResponse.json({ from: '2025-01-01', to: '2025-10-15' })),
    http.patch('*/api/v1/investments/envelopes/:id', async ({ request }) => {
      const body = await request.json() as Patch;
      bodies.push(body);
      const refused = answer(body);
      if (refused) return refused;
      const target = current as unknown as Record<string, unknown>;
      for (const key of ['displayName', 'kind', 'valuationCadence', 'institution', 'accountHint', 'staleAfterDays', 'note'])
        if (body[key] != null) target[key] = body[key];
      for (const key of ['institution', 'accountHint', 'staleAfterDays', 'note'])
        if (body[`clear${key[0]!.toUpperCase()}${key.slice(1)}`] === true) target[key] = null;
      return HttpResponse.json({ id: PEA });
    }),
  );
}

const problem = (status: number, code: string | null, extra: object = {}) => () =>
  HttpResponse.json({ status, code, title: 'Problem', ...extra }, { status, headers: { 'Content-Type': 'application/problem+json' } });

const CLEARS = { clearInstitution: false, clearAccountHint: false, clearOpenedOn: false, clearStaleAfterDays: false, clearNote: false, clearUcShareFloorPct: false };
const openForm = async () => within(await screen.findByRole('form', { name: 'Réglages' }));

describe('an envelope\'s settings', () => {
  // The lazy route chunk loaded cold inside the first test races its timeouts under load.
  beforeAll(async () => { await import('../EnvelopeDetailPage'); }, 30_000);

  it('saves a renamed envelope, sending only the name, and announces it', async () => {
    const bodies: Patch[] = [];
    serve(detail(), bodies);
    const { user, container } = await renderApp(`/investments/${PEA}?tab=settings`);

    const form = await openForm();
    expect(form.getByLabelText('Nom')).toHaveValue('PEA');
    await user.clear(form.getByLabelText('Nom'));
    await user.type(form.getByLabelText('Nom'), 'PEA Bourse');
    await user.click(form.getByRole('button', { name: 'Enregistrer les réglages' }));

    await waitFor(() => expect(toasts()).toHaveTextContent('Réglages enregistrés.'));
    expect(bodies).toEqual([{ ...CLEARS, displayName: 'PEA Bourse' }]);
    expect(await screen.findByRole('heading', { level: 1, name: 'PEA Bourse' })).toBeInTheDocument();
    expect(form.getByLabelText('Nom')).toHaveValue('PEA Bourse');
    await expectNoAxeViolations(container);
  });

  it('diffs against what the form started from, so a refetched envelope never makes untouched fields go out', async () => {
    const bodies: Patch[] = [];
    let reads = 0;
    serve(detail(), bodies);
    // A co-owner changes the institution after the form mounted: the second read already shows it.
    server.use(
      http.get('*/api/v1/investments/envelopes/:id', () => {
        reads += 1;
        return HttpResponse.json(reads === 1 ? detail() : { ...detail(), institution: 'Autre banque' });
      }),
      http.post('*/api/v1/investments/envelopes/:id/valuations', () => HttpResponse.json({ id: PEA }, { status: 201 })),
    );
    const { user } = await renderApp(`/investments/${PEA}?tab=settings`);

    const form = await openForm();
    expect(form.getByLabelText('Établissement')).toHaveValue('Banque');
    // Recording a value invalidates the envelope: it is read again while the form stays mounted.
    await user.click(screen.getByRole('button', { name: 'Saisir une valeur' }));
    await user.click(within(await screen.findByRole('dialog', { name: 'Nouvelle valeur' })).getByRole('button', { name: 'Enregistrer la valeur' }));
    await waitFor(() => { expect(reads).toBeGreaterThan(1); });
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    // The form keeps the values it started from (and any typing), whatever the refetch brought.
    expect(form.getByLabelText('Établissement')).toHaveValue('Banque');

    await user.clear(form.getByLabelText('Nom'));
    await user.type(form.getByLabelText('Nom'), 'PEA Bourse');
    await user.click(form.getByRole('button', { name: 'Enregistrer les réglages' }));

    expect(await screen.findByText('Réglages enregistrés.')).toBeInTheDocument();
    expect(bodies).toEqual([{ ...CLEARS, displayName: 'PEA Bourse' }]);
  });

  it('clears an emptied optional field and sets a threshold', async () => {
    const bodies: Patch[] = [];
    serve(detail(), bodies);
    const { user } = await renderApp(`/investments/${PEA}?tab=settings`);

    const form = await openForm();
    await user.clear(form.getByLabelText('Établissement'));
    await user.type(form.getByLabelText('Seuil de péremption (jours)'), '30');
    await user.click(form.getByRole('button', { name: 'Enregistrer les réglages' }));

    await screen.findByText('Réglages enregistrés.');
    expect(bodies).toEqual([{ ...CLEARS, clearInstitution: true, staleAfterDays: 30 }]);
    expect(form.getByLabelText('Établissement')).toHaveValue('');
  });

  it('shows the opening date\'s help for every kind, and the premiums field for a life insurance only', async () => {
    serve(detail());
    const { unmount } = await renderApp(`/investments/${PEA}?tab=settings`);
    const pea = await openForm();
    expect(pea.getByLabelText('Date d’ouverture')).toBeInTheDocument();
    expect(pea.getByText("Pour un PEA ou une assurance-vie transférés, la date d’ouverture du contrat d'origine : elle compte pour la fiscalité.")).toBeInTheDocument();
    expect(pea.queryByLabelText('Primes versées avant le 27/09/2017')).not.toBeInTheDocument();
    unmount();

    serve(detail({ kind: 'life_insurance' }));
    await renderApp(`/investments/${PEA}?tab=settings`);
    expect((await openForm()).getByLabelText('Primes versées avant le 27/09/2017')).toHaveValue('');
  });

  it('sends the premiums before 2017 typed with a comma, then clears them once emptied', async () => {
    const bodies: Patch[] = [];
    serve(detail({ kind: 'life_insurance' }), bodies);
    const { user } = await renderApp(`/investments/${PEA}?tab=settings`);

    const form = await openForm();
    await user.type(form.getByLabelText('Primes versées avant le 27/09/2017'), '12000,50');
    await user.click(form.getByRole('button', { name: 'Enregistrer les réglages' }));
    await screen.findByText('Réglages enregistrés.');
    expect(bodies[0]).toEqual({ ...CLEARS, premiumsBefore2017: '12000.50' });

    await user.clear(form.getByLabelText('Primes versées avant le 27/09/2017'));
    await user.click(form.getByRole('button', { name: 'Enregistrer les réglages' }));
    await waitFor(() => expect(bodies).toHaveLength(2));
    expect(bodies[1]).toEqual({ ...CLEARS, clearPremiumsBefore2017: true });
  });

  it('sends nothing and says so when nothing changed, then saves the next change', async () => {
    const bodies: Patch[] = [];
    serve(detail(), bodies);
    const { user } = await renderApp(`/investments/${PEA}?tab=settings`);

    const form = await openForm();
    await user.click(form.getByRole('button', { name: 'Enregistrer les réglages' }));
    await waitFor(() => expect(toasts()).toHaveTextContent('Rien à enregistrer.'));
    expect(bodies).toEqual([]);

    await user.type(form.getByLabelText('Note'), 'Plafond atteint');
    await user.click(form.getByRole('button', { name: 'Enregistrer les réglages' }));
    await waitFor(() => expect(toasts()).toHaveTextContent('Réglages enregistrés.'));
  });

  it.each(['0', '4000'])('refuses a stale threshold of %s before sending', async (days) => {
    const bodies: Patch[] = [];
    serve(detail(), bodies);
    const { user } = await renderApp(`/investments/${PEA}?tab=settings`);

    const form = await openForm();
    await user.type(form.getByLabelText('Seuil de péremption (jours)'), days);
    await user.click(form.getByRole('button', { name: 'Enregistrer les réglages' }));

    expect(await form.findByText('Entre 1 et 3650 jours, ou vide pour suivre la cadence.')).toBeInTheDocument();
    expect(form.getByLabelText('Seuil de péremption (jours)')).toBeInvalid();
    expect(bodies).toEqual([]);
  });

  it('places a refused value on the field the server names', async () => {
    serve(detail(), [], problem(422, 'investments.invalid_envelope', { errors: [{ path: 'accountHint', code: 'investments.invalid_envelope', message: 'x' }] }));
    const { user, container } = await renderApp(`/investments/${PEA}?tab=settings`);

    const form = await openForm();
    await user.clear(form.getByLabelText('4 derniers caractères du compte'));
    await user.type(form.getByLabelText('4 derniers caractères du compte'), '99');
    await user.click(form.getByRole('button', { name: 'Enregistrer les réglages' }));

    expect(await form.findByLabelText('4 derniers caractères du compte')).toBeInvalid();
    expect(form.getByText('Cette valeur n’est pas acceptée.')).toBeInTheDocument();
    expect(screen.queryByText('Problem')).not.toBeInTheDocument();
    expect(screen.queryByText('Réglages enregistrés.')).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('falls back to the one field that changed when the refusal names none', async () => {
    serve(detail(), [], problem(422, 'investments.invalid_envelope'));
    const { user } = await renderApp(`/investments/${PEA}?tab=settings`);

    const form = await openForm();
    await user.clear(form.getByLabelText('4 derniers caractères du compte'));
    await user.type(form.getByLabelText('4 derniers caractères du compte'), '99');
    await user.click(form.getByRole('button', { name: 'Enregistrer les réglages' }));

    expect(await form.findByLabelText('4 derniers caractères du compte')).toBeInvalid();
  });

  it('says a refusal no single field owns in French, page-level', async () => {
    serve(detail(), [], problem(422, 'investments.invalid_envelope'));
    const { user } = await renderApp(`/investments/${PEA}?tab=settings`);

    const form = await openForm();
    await user.type(form.getByLabelText('Établissement'), ' Bis');
    await user.type(form.getByLabelText('Note'), 'Plafond atteint');
    await user.click(form.getByRole('button', { name: 'Enregistrer les réglages' }));

    expect(await form.findByRole('alert')).toHaveTextContent('Cette valeur n’est pas acceptée.');
    expect(screen.queryByText('Problem')).not.toBeInTheDocument();
  });

  it.each([
    ['investments.unknown_kind', 'Type', 'Choisissez un type d’enveloppe.'],
    ['investments.unknown_cadence', 'Mise à jour de la valeur', 'Choisissez une fréquence.'],
  ])('places %s on its select', async (code, label, message) => {
    serve(detail(), [], problem(422, code));
    const { user } = await renderApp(`/investments/${PEA}?tab=settings`);

    const form = await openForm();
    await pick(user, label, label === 'Type' ? 'Livret' : 'Chaque trimestre');
    await user.click(form.getByRole('button', { name: 'Enregistrer les réglages' }));

    expect(await form.findByLabelText(label)).toBeInvalid();
    expect(form.getByText(message)).toBeInTheDocument();
  });

  it('says an envelope closed meanwhile, with its reload', async () => {
    serve(detail(), [], problem(409, 'investments.envelope_closed'));
    const { user } = await renderApp(`/investments/${PEA}?tab=settings`);

    const form = await openForm();
    await user.type(form.getByLabelText('Note'), 'x');
    await user.click(form.getByRole('button', { name: 'Enregistrer les réglages' }));

    expect(await form.findByText('Cette enveloppe est clôturée.')).toBeInTheDocument();
    server.use(http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json(detail({ closed: true }))));
    await user.click(form.getByRole('button', { name: 'Recharger' }));
    await waitFor(() => expect(screen.queryByRole('form', { name: 'Réglages' })).not.toBeInTheDocument());
    expect(screen.getByText('Enveloppe clôturée')).toBeInTheDocument();
  });

  it('is not offered to a reader', async () => {
    serve(detail({ canWrite: false }));
    await renderApp(`/investments/${PEA}`);

    await screen.findByRole('heading', { level: 1, name: 'PEA' });
    expect(screen.queryByRole('form', { name: 'Réglages' })).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Réglages' })).not.toBeInTheDocument();
  });
  it('hides the premiums field as soon as another kind is picked', async () => {
    serve(detail({ kind: 'life_insurance' }));
    const { user } = await renderApp(`/investments/${PEA}?tab=settings`);

    const form = await openForm();
    expect(form.getByLabelText('Primes versées avant le 27/09/2017')).toBeInTheDocument();
    await pick(user, 'Type', 'Livret');
    expect(form.queryByLabelText('Primes versées avant le 27/09/2017')).not.toBeInTheDocument();
  });

  it('shows an empty premiums field after a life insurance saved as another kind is switched back', async () => {
    const bodies: Patch[] = [];
    // The server drops the premiums with the kind change; the envelope read stays as the page first got it.
    const insurance = detail({ kind: 'life_insurance', premiumsBefore2017: { amount: '5000.00', currency: 'EUR' } });
    serve(insurance, bodies);
    server.use(
      http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json(insurance)),
      http.patch('*/api/v1/investments/envelopes/:id', async ({ request }) => { bodies.push(await request.json() as Patch); return HttpResponse.json({ id: PEA }); }),
    );
    const { user } = await renderApp(`/investments/${PEA}?tab=settings`);

    const form = await openForm();
    expect(form.getByLabelText('Primes versées avant le 27/09/2017')).toHaveValue('5000.00');
    await pick(user, 'Type', 'Livret');
    await user.click(form.getByRole('button', { name: 'Enregistrer les réglages' }));
    await screen.findByText('Réglages enregistrés.');
    await pick(user, 'Type', 'Assurance-vie');

    expect(form.getByLabelText('Primes versées avant le 27/09/2017')).toHaveValue('');
    await user.click(form.getByRole('button', { name: 'Enregistrer les réglages' }));
    await waitFor(() => expect(bodies).toHaveLength(2));
    expect(bodies[1]).not.toHaveProperty('premiumsBefore2017');
  });

  it('is not blocked by an invalid premiums value the kind change hid', async () => {
    const bodies: Patch[] = [];
    serve(detail({ kind: 'life_insurance' }), bodies);
    const { user } = await renderApp(`/investments/${PEA}?tab=settings`);

    const form = await openForm();
    await user.type(form.getByLabelText('Primes versées avant le 27/09/2017'), 'abc');
    await pick(user, 'Type', 'Livret');
    await user.click(form.getByRole('button', { name: 'Enregistrer les réglages' }));

    await screen.findByText('Réglages enregistrés.');
    expect(bodies).toEqual([{ ...CLEARS, kind: 'savings' }]);
  });

  it('sends the opening date typed as jj/mm/aaaa in ISO form', async () => {
    const bodies: Patch[] = [];
    serve(detail(), bodies);
    const { user } = await renderApp(`/investments/${PEA}?tab=settings`);

    const form = await openForm();
    await user.type(form.getByLabelText('Date d’ouverture'), '15/03/2019');
    await user.click(form.getByRole('button', { name: 'Enregistrer les réglages' }));

    await screen.findByText('Réglages enregistrés.');
    expect(bodies).toEqual([{ ...CLEARS, openedOn: '2019-03-15' }]);
  });

  it('refreshes the sale estimate once the opening date is saved', async () => {
    let dated = false;
    serve(detail());
    const finding = { item: PEA, module: 'investments', name: 'PEA', kind: 'pea', status: 'not_estimated', finding: 'opening_date_unknown', estimated: false, stale: false, approximate: false, notes: [] };
    server.use(
      http.get('*/api/v1/tax/sale', () => HttpResponse.json({ currency: 'EUR', rulesYear: 2026, carriedForward: false, total: false, totals: null, notes: [], items: dated ? [] : [finding] })),
      http.patch('*/api/v1/investments/envelopes/:id', () => { dated = true; return HttpResponse.json({ id: PEA }); }),
    );
    const { user } = await renderApp(`/investments/${PEA}`);

    expect(await screen.findByText('Date d’ouverture inconnue : renseignez-la dans les réglages')).toBeInTheDocument();
    await user.click(screen.getByRole('tab', { name: 'Réglages' }));
    const form = await openForm();
    await user.type(form.getByLabelText('Date d’ouverture'), '15/03/2019');
    await user.click(form.getByRole('button', { name: 'Enregistrer les réglages' }));
    await screen.findByText('Réglages enregistrés.');
    await user.click(screen.getByRole('tab', { name: 'Vue d’ensemble' }));

    await waitFor(() => expect(screen.queryByText('Date d’ouverture inconnue : renseignez-la dans les réglages')).not.toBeInTheDocument());
  });
});
