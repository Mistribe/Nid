import { link, list, summary } from '../documents/testData';
import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import { pick } from '@/test/controls';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { Detail } from './api';
import { detail, persons, SUB } from './testData';

export function serveDetail(body: Detail | null) {
  server.use(
    http.get('*/api/v1/subscriptions/providers', () => HttpResponse.json([])), // the settings form's provider list
    http.get('*/api/v1/subscriptions/:id', () => (body
      ? HttpResponse.json(body)
      : HttpResponse.json({ status: 404, code: 'not_found' }, { status: 404 }))),
    http.get('*/api/v1/core/persons', () => HttpResponse.json(persons)),
  );
}

describe('Abonnements › détail', () => {
  it('shows the header, the coverage band and the sections', async () => {
    serveDetail(detail({ canWrite: false }));
    const { container } = await renderApp(`/subscriptions/${SUB}`);

    expect(await screen.findByRole('heading', { level: 1, name: 'Spotify Premium Duo' })).toBeInTheDocument();
    expect(screen.getByText('Partagé')).toBeInTheDocument();
    expect(screen.getByText('Prochaine échéance').nextElementSibling).toHaveTextContent('28 janvier 2026');
    expect(screen.getByRole('img', { name: /Couverture/ })).toBeInTheDocument();
    const phases = screen.getByRole('table', { name: 'Tarifs' });
    expect(within(phases).getAllByRole('row')).toHaveLength(2);
    expect(phases).toHaveTextContent('17,20 €');
    expect(phases).toHaveTextContent('1 mois');
    const purchases = screen.getByRole('region', { name: 'Achats' });
    expect(purchases).toHaveTextContent('Carte cadeau 3 mois');
    expect(purchases).toHaveTextContent('3 mois');
    expect(screen.getByText(/92 jours offerts/)).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('lists what a bundle includes, and nothing for a provider that includes nothing', async () => {
    const bundle = { key: 'apple-one', name: 'Apple One', origin: 'catalog', labels: [], iconUrl: null };
    serveDetail(detail({
      provider: { ...bundle, included: [{ key: 'apple-tv', name: 'Apple TV', labels: [], iconUrl: null }, { key: 'icloud-plus', name: 'iCloud+', labels: [], iconUrl: null }] },
    }));
    const { container } = await renderApp(`/subscriptions/${SUB}`);

    const included = await screen.findByRole('region', { name: 'Inclut' });
    expect(within(included).getAllByRole('listitem').map((item) => item.textContent)).toEqual(['ATApple TV', 'IiCloud+']);
    await expectNoAxeViolations(container);
  });

  it('shows no included services without a bundle', async () => {
    serveDetail(detail({ provider: { key: 'spotify', name: 'Spotify', origin: 'catalog', labels: [], iconUrl: null, included: [] } }));
    await renderApp(`/subscriptions/${SUB}`);

    await screen.findByRole('heading', { level: 1 });
    expect(screen.queryByRole('region', { name: 'Inclut' })).not.toBeInTheDocument();
  });

  it('shows the findings as text', async () => {
    serveDetail(detail({ findings: [{ code: 'subscriptions.gap', message: 'Un trou de couverture existe.', reference: null }] }));
    await renderApp(`/subscriptions/${SUB}`);

    expect(await screen.findByText('Un trou de couverture existe.')).toBeInTheDocument();
  });

  it('hides the horizon finding and reads the others in French', async () => {
    serveDetail(detail({
      findings: [
        { code: 'subscriptions.horizon_reached', message: 'The walk stopped at the horizon.', reference: null },
        { code: 'subscriptions.cycle_cap', message: 'A phase reached the renewal cap.', reference: null },
      ],
    }));
    await renderApp(`/subscriptions/${SUB}`);

    expect(await screen.findByText(/plafond de renouvellements/)).toBeInTheDocument();
    expect(screen.queryByText(/The walk stopped/)).not.toBeInTheDocument();
    expect(screen.queryByText(/A phase reached/)).not.toBeInTheDocument();
  });

  it('shows no findings section when the horizon is the only finding', async () => {
    serveDetail(detail({ findings: [{ code: 'subscriptions.horizon_reached', message: 'The walk stopped.', reference: null }] }));
    await renderApp(`/subscriptions/${SUB}`);

    await screen.findByRole('heading', { level: 1, name: 'Spotify Premium Duo' });
    expect(screen.queryByRole('heading', { name: 'Points à vérifier' })).not.toBeInTheDocument();
  });

  it('answers a subscription it cannot read like one that does not exist', async () => {
    serveDetail(null);
    await renderApp(`/subscriptions/${SUB}`);

    expect(await screen.findByRole('alert')).toHaveTextContent('Cet élément est introuvable.');
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('shows a purchase refusal on its field, with no generic alert', async () => {
    serveDetail(detail());
    server.use(http.post('*/api/v1/subscriptions/:id/purchases', () => HttpResponse.json({
      status: 422, code: 'request.invalid', title: 'A count is positive.',
      errors: [{ path: 'grants[0].duration.count', code: 'request.count', message: 'A count is positive.' }],
    }, { status: 422 })));
    const { user } = await renderApp(`/subscriptions/${SUB}`);
    await screen.findByRole('heading', { level: 1, name: 'Spotify Premium Duo' });

    await user.click(screen.getByRole('button', { name: 'Ajouter un achat' }));
    const form = await screen.findByRole('form', { name: 'Ajouter un achat' });
    await user.type(within(form).getByLabelText('Libellé'), 'Carte cadeau');
    await user.type(within(form).getByLabelText('Date'), '2025-12-01');
    await user.type(within(form).getByLabelText('Montant'), '0');
    await user.click(within(form).getByRole('button', { name: 'Ajouter un achat' }));

    const count = within(form).getByLabelText('Durée couverte');
    await waitFor(() => expect(count).toHaveAttribute('aria-invalid', 'true'));
    expect(count).toHaveAccessibleDescription('Saisissez un nombre entier positif.');
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('shows its costs inline and findings in a notice', async () => {
    serveDetail(detail({
      findings: [
        { code: 'subscriptions.horizon_reached', message: 'The walk stopped at the horizon.', reference: null },
        { code: 'subscriptions.cycle_cap', message: 'A phase reached the renewal cap.', reference: null },
      ],
    }));
    const { container } = await renderApp(`/subscriptions/${SUB}`);

    await screen.findByRole('heading', { level: 1, name: 'Spotify Premium Duo' });
    const costs = screen.getByRole('region', { name: 'Co\u00fbts du mois' });
    expect(costs).toHaveTextContent('Pr\u00e9lev\u00e9 ce mois-ci');
    expect(costs).toHaveTextContent('Co\u00fbt mensuel r\u00e9el');
    expect(costs).toHaveTextContent('Prochaine \u00e9ch\u00e9ance');
    expect(costs).toHaveTextContent('28 janvier 2026');
    const notice = screen.getByRole('list', { name: 'Points \u00e0 v\u00e9rifier' });
    expect(notice).toHaveTextContent(/plafond de renouvellements/);
    expect(screen.queryByText(/The walk stopped/)).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('leaves out the monthly cost of one billed each month: it is the amount charged', async () => {
    serveDetail(detail({ billedMonthly: true }));
    await renderApp(`/subscriptions/${SUB}`);

    await screen.findByRole('heading', { level: 1, name: 'Spotify Premium Duo' });
    const costs = screen.getByRole('region', { name: 'Co\u00fbts du mois' });
    expect(costs).toHaveTextContent('Pr\u00e9lev\u00e9 ce mois-ci');
    expect(costs).not.toHaveTextContent('Co\u00fbt mensuel r\u00e9el');
  });

  it('edits price and purchases in dialogs', async () => {
    const prices: unknown[] = [];
    const purchases: unknown[] = [];
    serveDetail(detail());
    server.use(
      http.post('*/api/v1/subscriptions/:id/price-changes', async ({ request }) => { prices.push(await request.json()); return HttpResponse.json({ id: 'x' }, { status: 201 }); }),
      http.post('*/api/v1/subscriptions/:id/purchases', async ({ request }) => { purchases.push(await request.json()); return HttpResponse.json({ id: 'p' }); }),
    );
    const { user } = await renderApp(`/subscriptions/${SUB}`);
    await screen.findByRole('heading', { level: 1, name: 'Spotify Premium Duo' });

    await user.click(screen.getByRole('button', { name: 'Changer le prix' }));
    const price = await screen.findByRole('dialog', { name: 'Changer le prix' });
    await user.type(within(price).getByLabelText('\u00c0 partir du'), '2026-02-28');
    await user.type(within(price).getByLabelText('Nouveau montant'), '15,99');
    await user.click(within(price).getByRole('button', { name: 'Changer le prix' }));
    await waitFor(() => expect(prices).toEqual([{ startsOn: '2026-02-28', price: { amount: '15.99', currency: 'EUR' } }]));
    await waitFor(() => expect(screen.getAllByRole('status').map((s) => s.textContent)).toContain('Modification enregistr\u00e9e.'));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Ajouter un achat' }));
    const purchase = await screen.findByRole('dialog', { name: 'Ajouter un achat' });
    await user.type(within(purchase).getByLabelText('Libell\u00e9'), 'Carte cadeau');
    await user.type(within(purchase).getByLabelText('Date'), '2025-12-01');
    await pick(user, 'Type', 'Carte cadeau');
    await user.type(within(purchase).getByLabelText('Montant'), '0');
    await user.click(within(purchase).getByRole('button', { name: 'Ajouter un achat' }));
    await waitFor(() => expect(purchases).toEqual([expect.objectContaining({ label: 'Carte cadeau', kind: 'gift' })]));
  });

  it('a reader sees no write action', async () => {
    serveDetail(detail({ canWrite: false }));
    const { container } = await renderApp(`/subscriptions/${SUB}`);

    await screen.findByRole('heading', { level: 1, name: 'Spotify Premium Duo' });
    expect(within(screen.getByRole('article')).queryAllByRole('button')).toEqual([]);
    expect(container.querySelector('form')).toBeNull();
    await expectNoAxeViolations(container);
  });

  it('shows a Documents section listing the documents anchored on the subscription', async () => {
    serveDetail(detail());
    server.use(http.get('*/api/v1/documents', () => HttpResponse.json(list([summary({ title: 'Facture janvier', links: [link({ targetId: SUB, anchorItemId: SUB })] })]))));
    await renderApp(`/subscriptions/${SUB}`);

    const section = await screen.findByRole('region', { name: 'Documents' });
    expect((await within(section).findAllByText('Facture janvier')).length).toBeGreaterThan(0);
  });
});
