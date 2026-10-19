import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { Detail } from '../api';
import { BOB, detail, persons, PURCHASE, SUB } from '../testData';

function serve(body: Detail, extra: Parameters<typeof server.use> = []) {
  let current = body;
  // The test's own handlers first: `:id` would also match /subscriptions/providers.
  server.use(
    ...extra,
    http.get('*/api/v1/subscriptions/providers', () => HttpResponse.json([])), // the settings form's provider list
    http.get('*/api/v1/subscriptions/:id', () => HttpResponse.json(current)),
    http.get('*/api/v1/core/persons', () => HttpResponse.json(persons)),
  );
  return { set: (next: Detail) => { current = next; } };
}

const open = async () => {
  const utils = await renderApp(`/subscriptions/${SUB}`);
  await screen.findByRole('heading', { level: 1, name: 'Spotify Premium Duo' });
  return utils;
};

describe('Abonnements › modifications', () => {
  afterEach(() => vi.useRealTimers());

  it('sends only the setting that changed', async () => {
    const sent: unknown[] = [];
    serve(detail(), [http.patch('*/api/v1/subscriptions/:id', async ({ request }) => {
      sent.push(await request.json());
      return HttpResponse.json({ id: SUB });
    })]);
    const { user } = await open();

    await user.click(screen.getByLabelText('Renouvellement automatique'));
    await user.click(screen.getByRole('button', { name: 'Enregistrer les réglages' }));

    await waitFor(() => expect(sent).toEqual([{ autoRenew: false, clearCancelledAt: false, clearProviderKey: false }]));
  });

  it('clears a cancellation date', async () => {
    const sent: unknown[] = [];
    serve(detail({ cancelledAt: '2026-03-01' }), [http.patch('*/api/v1/subscriptions/:id', async ({ request }) => {
      sent.push(await request.json());
      return HttpResponse.json({ id: SUB });
    })]);
    const { user } = await open();

    await user.clear(screen.getByLabelText('Date de résiliation'));
    await user.click(screen.getByRole('button', { name: 'Enregistrer les réglages' }));

    await waitFor(() => expect(sent).toEqual([{ clearCancelledAt: true, clearProviderKey: false }]));
  });

  it('offers to reload when the subscription changed meanwhile', async () => {
    serve(detail(), [http.patch('*/api/v1/subscriptions/:id', () => HttpResponse.json({ status: 409, code: 'core.conflict' }, { status: 409 }))]);
    const { user } = await open();

    await user.click(screen.getByLabelText('Renouvellement automatique'));
    await user.click(screen.getByRole('button', { name: 'Enregistrer les réglages' }));

    expect(await screen.findByRole('button', { name: 'Recharger' })).toBeInTheDocument();
  });

  it("never sends back a field it did not change, even after the subscription changed meanwhile", async () => {
    const sent: unknown[] = [];
    let first = true;
    const state = serve(detail(), [http.patch('*/api/v1/subscriptions/:id', async ({ request }) => {
      if (first) {
        first = false;
        state.set(detail({ note: 'Changée par Bob' }));
        return HttpResponse.json({ status: 409, code: 'core.conflict' }, { status: 409 });
      }
      sent.push(await request.json());
      return HttpResponse.json({ id: SUB });
    })]);
    const { user } = await open();

    await user.click(screen.getByLabelText('Renouvellement automatique'));
    await user.click(screen.getByRole('button', { name: 'Enregistrer les réglages' }));
    await user.click(await screen.findByRole('button', { name: 'Recharger' }));
    await waitFor(() => expect(screen.getByLabelText('Note')).toHaveValue('Changée par Bob'));
    await user.click(screen.getByRole('button', { name: 'Enregistrer les réglages' }));

    await waitFor(() => expect(sent).toEqual([{ autoRenew: false, clearCancelledAt: false, clearProviderKey: false }]));
  });

  it('changes the price with the amount written with a comma', async () => {
    const sent: unknown[] = [];
    serve(detail(), [http.post('*/api/v1/subscriptions/:id/price-changes', async ({ request }) => {
      sent.push(await request.json());
      return HttpResponse.json({ id: 'x' }, { status: 201 });
    })]);
    const { user } = await open();

    await user.click(screen.getByRole('button', { name: 'Changer le prix' }));
    const form = await screen.findByRole('form', { name: 'Changer le prix' });
    await user.type(within(form).getByLabelText('À partir du'), '2026-02-28');
    await user.type(within(form).getByLabelText('Nouveau montant'), '15,99');
    await user.click(within(form).getByRole('button', { name: 'Changer le prix' }));

    await waitFor(() => expect(sent).toEqual([{ startsOn: '2026-02-28', price: { amount: '15.99', currency: 'EUR' } }]));
  });

  it('shows a refusal of the server on its field', async () => {
    serve(detail(), [http.post('*/api/v1/subscriptions/:id/price-changes', () => HttpResponse.json({
      status: 422, code: 'subscriptions.no_open_phase', title: 'No phase is open on this date.',
      errors: [{ path: 'startsOn', code: 'subscriptions.no_open_phase', message: 'No phase is open on this date.' }],
    }, { status: 422 }))]);
    const { user } = await open();

    await user.click(screen.getByRole('button', { name: 'Changer le prix' }));
    const form = await screen.findByRole('form', { name: 'Changer le prix' });
    await user.type(within(form).getByLabelText('À partir du'), '2020-01-01');
    await user.type(within(form).getByLabelText('Nouveau montant'), '15');
    await user.click(within(form).getByRole('button', { name: 'Changer le prix' }));

    const date = within(form).getByLabelText('À partir du');
    await waitFor(() => expect(date).toHaveAttribute('aria-invalid', 'true'));
    expect(date).toHaveAccessibleDescription(/Aucun tarif n'est en cours à cette date/);
  });

  it('adds a purchase granting time to this subscription', async () => {
    const sent: unknown[] = [];
    serve(detail(), [http.post('*/api/v1/subscriptions/:id/purchases', async ({ request }) => {
      sent.push(await request.json());
      return HttpResponse.json({ id: 'p' });
    })]);
    const { user } = await open();

    await user.click(screen.getByRole('button', { name: 'Ajouter un achat' }));
    const form = await screen.findByRole('form', { name: 'Ajouter un achat' });
    await user.type(within(form).getByLabelText('Libellé'), 'Carte cadeau');
    await user.type(within(form).getByLabelText('Date'), '2025-12-01');
    await pick(user, 'Type', 'Carte cadeau');
    await user.type(within(form).getByLabelText('Montant'), '0');
    await user.clear(within(form).getByLabelText('Durée couverte'));
    await user.type(within(form).getByLabelText('Durée couverte'), '2');
    await user.click(within(form).getByRole('button', { name: 'Ajouter un achat' }));

    await waitFor(() => expect(sent).toEqual([{
      date: '2025-12-01', label: 'Carte cadeau', kind: 'gift', price: { amount: '0', currency: 'EUR' },
      payer: { type: 'external', personId: null },
      grants: [{ duration: { count: 2, unit: 'month' }, source: 'gift', startsOn: null }],
      allocatedAmount: null, note: null,
    }]));
  });

  it('asks before deleting a purchase', async () => {
    const deleted: string[] = [];
    serve(detail(), [http.delete('*/api/v1/subscriptions/:id/purchases/:purchaseId', ({ params }) => {
      deleted.push(String(params.purchaseId));
      return HttpResponse.json({ id: params.purchaseId });
    })]);
    const { user } = await open();

    await user.click(screen.getByRole('button', { name: 'Actions de l’achat Carte cadeau 3 mois' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Supprimer' }));
    expect(deleted).toEqual([]);
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));

    await waitFor(() => expect(deleted).toEqual([PURCHASE]));
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Achat supprimé.'));
    await waitFor(() => expect(screen.getByRole('heading', { name: 'Achats' })).toHaveFocus());
  });

  it('shows no action and says who can write when the reader may not', async () => {
    serve(detail({ canWrite: false }));
    const { container } = await open();

    expect(container.querySelector('form')).toBeNull();
    expect(screen.queryByRole('button', { name: /Actions de l’achat/ })).not.toBeInTheDocument();
    expect(screen.getByText('Cet abonnement ne peut être modifié que par Alice Dev.')).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('names every owner who can write', async () => {
    serve(detail({ canWrite: false, owners: [{ personId: persons[0]!.id, shareBp: 5000 }, { personId: BOB, shareBp: 5000 }] }));
    await open();

    expect(screen.getByText('Cet abonnement ne peut être modifié que par Alice Dev et Bob Dev.')).toBeInTheDocument();
  });

  it('has no accessibility violation with the forms shown', async () => {
    serve(detail());
    const { container } = await open();
    await expectNoAxeViolations(container);
  });
});

describe('ProviderPicker', () => {
  afterEach(() => vi.useRealTimers());

  it('searches the visible providers and links the one chosen', async () => {
    const sent: unknown[] = [];
    serve(detail(), [
      http.get('*/api/v1/subscriptions/providers', () =>
        HttpResponse.json([{ key: 'spotify', name: 'Spotify', origin: 'catalog', labels: [], website: null, archived: false, iconUrl: null }])),
      http.patch('*/api/v1/subscriptions/:id', async ({ request }) => {
        sent.push(await request.json());
        return HttpResponse.json({ id: SUB });
      }),
    ]);
    const { user } = await open();

    await pick(user, 'Fournisseur', 'Spotify');
    await user.click(screen.getByRole('button', { name: 'Enregistrer les réglages' }));

    await waitFor(() => expect(sent).toEqual([{ providerKey: 'spotify', clearCancelledAt: false, clearProviderKey: false }]));
  });

  it('says the providers are loading rather than that there are none', async () => {
    serve(detail(), [http.get('*/api/v1/subscriptions/providers', () => new Promise<Response>(() => {}))]);
    const { user } = await open();

    await user.click(screen.getByRole('combobox', { name: 'Fournisseur' }));
    await user.type(await screen.findByRole('combobox', { name: 'Rechercher' }), 'zzz');

    expect(await screen.findByText('Chargement…')).toBeInTheDocument();
    expect(screen.queryByText('Aucun résultat')).not.toBeInTheDocument();
  });

  it('says so, and offers no new provider, when the providers cannot be read', async () => {
    serve(detail(), [http.get('*/api/v1/subscriptions/providers', () => HttpResponse.json({ status: 403, title: 'no', traceId: '00-t-09' }, { status: 403 }))]);
    await open();

    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Nouveau fournisseur' })).toBeDisabled();
  });

  it('creates a custom provider from the settings form and links it', async () => {
    const sent: unknown[] = [];
    serve(detail(), [
      http.get('*/api/v1/subscriptions/providers', () => HttpResponse.json([])),
      http.get('*/api/v1/subscriptions/labels', () => HttpResponse.json([{ key: 'other', name: 'Other', color: '#9E9E9E' }])),
      http.post('*/api/v1/subscriptions/providers', () => HttpResponse.json({ key: 'ma-box-x1y2z3', name: 'Ma box', origin: 'custom',
        labels: [{ key: 'other', name: 'Other', color: '#9E9E9E' }], website: null, archived: false, iconUrl: null }, { status: 201 })),
      http.patch('*/api/v1/subscriptions/:id', async ({ request }) => { sent.push(await request.json()); return HttpResponse.json({ id: SUB }); }),
    ]);
    const { user } = await open();

    await user.click(screen.getByRole('button', { name: 'Nouveau fournisseur' }));
    const dialog = await screen.findByRole('dialog', { name: 'Nouveau fournisseur' });
    await user.type(within(dialog).getByLabelText('Nom'), 'Ma box');
    await user.click(within(dialog).getByRole('checkbox', { name: 'Other' }));
    await user.click(within(dialog).getByRole('button', { name: 'Créer' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(sent).toEqual([]); // creating the provider does not save the settings
    await user.click(screen.getByRole('button', { name: 'Enregistrer les réglages' }));

    await waitFor(() => expect(sent).toEqual([{ providerKey: 'ma-box-x1y2z3', clearCancelledAt: false, clearProviderKey: false }]));
  });
});
