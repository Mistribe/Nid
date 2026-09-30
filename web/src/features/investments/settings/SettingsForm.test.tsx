import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
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

const CLEARS = { clearInstitution: false, clearAccountHint: false, clearOpenedOn: false, clearStaleAfterDays: false, clearNote: false };
const openForm = async () => within(await screen.findByRole('form', { name: 'Réglages' }));

describe('an envelope\'s settings', () => {
  it('saves a renamed envelope, sending only the name, and announces it', async () => {
    const bodies: Patch[] = [];
    serve(detail(), bodies);
    const { user, container } = await renderApp(`/investments/${PEA}`);

    const form = await openForm();
    expect(form.getByLabelText('Nom')).toHaveValue('PEA');
    await user.clear(form.getByLabelText('Nom'));
    await user.type(form.getByLabelText('Nom'), 'PEA Bourse');
    await user.click(form.getByRole('button', { name: 'Enregistrer les réglages' }));

    expect(await screen.findByText('Réglages enregistrés.')).toHaveAttribute('role', 'status');
    expect(bodies).toEqual([{ ...CLEARS, displayName: 'PEA Bourse' }]);
    expect(await screen.findByRole('heading', { level: 1, name: 'PEA Bourse' })).toBeInTheDocument();
    expect(form.getByLabelText('Nom')).toHaveValue('PEA Bourse');
    await expectNoAxeViolations(container);
  });

  it('clears an emptied optional field and sets a threshold', async () => {
    const bodies: Patch[] = [];
    serve(detail(), bodies);
    const { user } = await renderApp(`/investments/${PEA}`);

    const form = await openForm();
    await user.clear(form.getByLabelText('Établissement'));
    await user.type(form.getByLabelText('Seuil de péremption (jours)'), '30');
    await user.click(form.getByRole('button', { name: 'Enregistrer les réglages' }));

    await screen.findByText('Réglages enregistrés.');
    expect(bodies).toEqual([{ ...CLEARS, clearInstitution: true, staleAfterDays: 30 }]);
    expect(form.getByLabelText('Établissement')).toHaveValue('');
  });

  it('sends nothing and says so when nothing changed, then clears that before the next save', async () => {
    const bodies: Patch[] = [];
    serve(detail(), bodies);
    const { user } = await renderApp(`/investments/${PEA}`);

    const form = await openForm();
    await user.click(form.getByRole('button', { name: 'Enregistrer les réglages' }));
    expect(await screen.findByText('Rien à enregistrer.')).toHaveAttribute('role', 'status');
    expect(bodies).toEqual([]);

    await user.type(form.getByLabelText('Note'), 'Plafond atteint');
    await user.click(form.getByRole('button', { name: 'Enregistrer les réglages' }));
    expect(await screen.findByText('Réglages enregistrés.')).toBeInTheDocument();
    expect(screen.queryByText('Rien à enregistrer.')).not.toBeInTheDocument();
  });

  it.each(['0', '4000'])('refuses a stale threshold of %s before sending', async (days) => {
    const bodies: Patch[] = [];
    serve(detail(), bodies);
    const { user } = await renderApp(`/investments/${PEA}`);

    const form = await openForm();
    await user.type(form.getByLabelText('Seuil de péremption (jours)'), days);
    await user.click(form.getByRole('button', { name: 'Enregistrer les réglages' }));

    expect(await form.findByText('Entre 1 et 3650 jours, ou vide pour suivre la cadence.')).toBeInTheDocument();
    expect(form.getByLabelText('Seuil de péremption (jours)')).toBeInvalid();
    expect(bodies).toEqual([]);
  });

  it('places a refused value on the one field that changed', async () => {
    serve(detail(), [], problem(422, 'investments.invalid_envelope'));
    const { user, container } = await renderApp(`/investments/${PEA}`);

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

  it('says a refusal no single field owns in French, page-level', async () => {
    serve(detail(), [], problem(422, 'investments.invalid_envelope'));
    const { user } = await renderApp(`/investments/${PEA}`);

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
    const { user } = await renderApp(`/investments/${PEA}`);

    const form = await openForm();
    await user.selectOptions(form.getByLabelText(label), label === 'Type' ? 'savings' : 'quarterly');
    await user.click(form.getByRole('button', { name: 'Enregistrer les réglages' }));

    expect(await form.findByLabelText(label)).toBeInvalid();
    expect(form.getByText(message)).toBeInTheDocument();
  });

  it('says an envelope closed meanwhile, with its reload', async () => {
    serve(detail(), [], problem(409, 'investments.envelope_closed'));
    const { user } = await renderApp(`/investments/${PEA}`);

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
});
