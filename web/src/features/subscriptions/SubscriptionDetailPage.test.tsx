import { link, list, summary } from '../documents/testData';
import { cleanup, screen, waitFor, within } from '@testing-library/react';
import { delay } from 'msw';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import { pick } from '@/test/controls';
import { expectNoAxeViolations } from '@/test/axe';
import { expectHeaderActionRule, openMore } from '@/test/headerActions';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { toasts } from '@/test/toasts';
import type { Detail } from './api';
import { detail, eur, persons, SUB } from './testData';

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
    expect(screen.getByText(/92 jours offerts/)).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('shows the prices and the purchases under « Historique »', async () => {
    serveDetail(detail({ canWrite: false }));
    const { container } = await renderApp(`/subscriptions/${SUB}?tab=history`);

    const phases = await screen.findByRole('table', { name: 'Tarifs' });
    expect(within(phases).getAllByRole('row')).toHaveLength(2);
    expect(phases).toHaveTextContent('17,20 €');
    expect(phases).toHaveTextContent('1 mois');
    const purchases = screen.getByRole('region', { name: 'Achats' });
    expect(purchases).toHaveTextContent('Carte cadeau 3 mois');
    expect(purchases).toHaveTextContent('3 mois');
    await expectNoAxeViolations(container);
  });

  it('the detail header follows the action rule', async () => {
    serveDetail(detail());
    const { user, container } = await renderApp(`/subscriptions/${SUB}`);
    await screen.findByRole('heading', { level: 1, name: 'Spotify Premium Duo' });

    expectHeaderActionRule(container);
    const header = container.querySelector<HTMLElement>('header[data-page-header] [data-page-actions]')!;
    expect(within(header).getByRole('button', { name: 'Changer le prix' })).toHaveClass('bg-primary');
    expect(within(header).getByRole('button', { name: 'Ajouter un achat' })).not.toHaveClass('bg-primary');
    expect(within(header).queryByRole('button', { name: 'Conditions du contrat' })).not.toBeInTheDocument();
    expect(within(header).queryByRole('button', { name: 'Ajouter l’assurance' })).not.toBeInTheDocument();

    await openMore(user);
    await screen.findByRole('menuitem', { name: 'Conditions du contrat' });
    await waitFor(() => expect(screen.getAllByRole('menuitem').map((i) => i.textContent)).toEqual(['Ajouter un achat', 'Conditions du contrat', 'Ajouter l’assurance', 'Partage…']));
    await user.click(screen.getByRole('menuitem', { name: 'Conditions du contrat' }));
    const terms = await screen.findByRole('dialog', { name: 'Conditions du contrat' });
    await user.click(within(terms).getByRole('button', { name: 'Annuler' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    await waitFor(() => expect(screen.getByRole('button', { name: 'Autres actions' })).toHaveFocus());

    await openMore(user);
    await user.click(await screen.findByRole('menuitem', { name: 'Ajouter l’assurance' }));
    await screen.findByRole('dialog', { name: 'Assurance' });
    await user.keyboard('{Escape}');
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    await waitFor(() => expect(screen.getByRole('button', { name: 'Autres actions' })).toHaveFocus());
  });

  it('the detail is in tabs', async () => {
    const bundle = { key: 'apple-one', name: 'Apple One', origin: 'catalog', labels: [], iconUrl: null };
    const body = detail({
      provider: { ...bundle, included: [{ key: 'apple-tv', name: 'Apple TV', labels: [], iconUrl: null }] },
      findings: [{ code: 'subscriptions.cycle_cap', message: 'A phase reached the renewal cap.', reference: null }],
      insurance: { kind: 'home', property: null, insuredLabel: 'La maison', policyNumber: null, insurerContact: null, guarantees: [] },
      contractTerms: { anniversaryOn: '2026-06-28', notice: null, commitmentEndsOn: null, cancellableFrom: null },
    });
    serveDetail(body);
    server.use(http.get('*/api/v1/documents', () => HttpResponse.json(list([summary({ title: 'Facture janvier', links: [link({ targetId: SUB, anchorItemId: SUB })] })]))));
    await renderApp(`/subscriptions/${SUB}`);

    await screen.findByRole('heading', { level: 1, name: 'Spotify Premium Duo' });
    expect(screen.getAllByRole('tab').map((tab) => tab.textContent)).toEqual(['Vue d’ensemble', 'Historique', 'Documents', 'Réglages']);
    let panel = screen.getByRole('tabpanel');
    expect(within(panel).getByRole('img', { name: /Couverture/ })).toBeInTheDocument();
    expect(within(panel).getByRole('region', { name: 'Contrat' })).toBeInTheDocument();
    expect(within(panel).getByRole('region', { name: 'Assurance' })).toBeInTheDocument();
    expect(within(panel).getByRole('region', { name: 'Inclut' })).toBeInTheDocument();
    // The findings concern every tab: above them, not in the overview.
    const findings = screen.getByRole('list', { name: 'Points à vérifier' });
    expect(panel).not.toContainElement(findings);
    expect(findings.compareDocumentPosition(screen.getByRole('tablist')) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(within(panel).queryByRole('table', { name: 'Tarifs' })).not.toBeInTheDocument();
    cleanup();

    await renderApp(`/subscriptions/${SUB}?tab=history`);
    panel = await screen.findByRole('tabpanel');
    expect(await within(panel).findByRole('table', { name: 'Tarifs' })).toBeInTheDocument();
    expect(within(panel).getByRole('region', { name: 'Achats' })).toBeInTheDocument();
    expect(within(panel).getByRole('table', { name: 'Paiements' })).toBeInTheDocument();
    expect(within(panel).queryByRole('img', { name: /Couverture/ })).not.toBeInTheDocument();
    cleanup();

    await renderApp(`/subscriptions/${SUB}?tab=documents`);
    panel = await screen.findByRole('tabpanel', { name: 'Documents' });
    expect((await within(panel).findAllByText('Facture janvier')).length).toBeGreaterThan(0);
    cleanup();

    await renderApp(`/subscriptions/${SUB}?tab=settings`);
    panel = await screen.findByRole('tabpanel', { name: 'Réglages' });
    expect(within(panel).getByRole('button', { name: 'Enregistrer les réglages' })).toBeInTheDocument();
  });

  it('a read-only subscription has no header buttons and no « Réglages » tab', async () => {
    serveDetail(detail({ canWrite: false }));
    const { container } = await renderApp(`/subscriptions/${SUB}?tab=settings`);

    await screen.findByRole('heading', { level: 1, name: 'Spotify Premium Duo' });
    expect(container.querySelectorAll('header[data-page-header] [data-page-actions] button')).toHaveLength(0);
    expect(screen.getAllByRole('tab').map((tab) => tab.textContent)).toEqual(['Vue d’ensemble', 'Historique', 'Documents']);
    expect(screen.getByRole('tab', { name: 'Vue d’ensemble' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.queryByRole('button', { name: 'Enregistrer les réglages' })).not.toBeInTheDocument();
  });

  // Two years of monthly charges, oldest first as the server sends them; from 2026 on they are projected.
  const monthly = (projectedFrom: number) => Array.from({ length: 24 }, (_, i) => ({
    date: `20${25 + Math.floor(i / 12)}-${String((i % 12) + 1).padStart(2, '0')}-28`, price: eur('17.20'), isProjected: i >= projectedFrom, purchaseId: null,
  }));
  const chargeRows = async () => within(await screen.findByRole('table', { name: 'Paiements' })).getAllByRole('row').slice(1);

  it('the charges show 12 rows, then « Tout afficher »', async () => {
    serveDetail(detail({ charges: monthly(12) }));
    const { user } = await renderApp(`/subscriptions/${SUB}?tab=history`);

    // Around today: the 3 next charges and the 9 past ones before them, oldest first.
    let shown = await chargeRows();
    expect(shown).toHaveLength(12);
    expect(shown[0]).toHaveTextContent('28 avril 2025');
    expect(shown[8]).toHaveTextContent('28 décembre 2025');
    expect(shown[9]).toHaveTextContent('28 janvier 2026');
    expect(shown[11]).toHaveTextContent('28 mars 2026');

    const toggle = screen.getByRole('button', { name: 'Tout afficher (24)' });
    await user.click(toggle);
    shown = await chargeRows();
    expect(shown).toHaveLength(24);
    expect(shown[0]).toHaveTextContent('28 janvier 2025');
    expect(shown[23]).toHaveTextContent('28 décembre 2026');
    expect(toggle).toHaveAccessibleName('Afficher moins');
    expect(toggle).toHaveFocus();

    await user.click(toggle);
    expect(await chargeRows()).toHaveLength(12);
    expect(toggle).toHaveAccessibleName('Tout afficher (24)');
    expect(toggle).toHaveFocus();
  });

  it('shows the last 12 charges when none is projected', async () => {
    serveDetail(detail({ charges: monthly(24) }));
    await renderApp(`/subscriptions/${SUB}?tab=history`);

    const shown = await chargeRows();
    expect(shown).toHaveLength(12);
    expect(shown[0]).toHaveTextContent('28 janvier 2026');
    expect(shown[11]).toHaveTextContent('28 décembre 2026');
  });

  it('folds the charges again on another subscription', async () => {
    serveDetail(detail({ charges: monthly(12) }));
    const { user, router } = await renderApp(`/subscriptions/${SUB}?tab=history`);
    await user.click(await screen.findByRole('button', { name: 'Tout afficher (24)' }));
    expect(await chargeRows()).toHaveLength(24);

    await router.navigate({ href: '/subscriptions/0192f0aa-0000-7000-8000-000000000009?tab=history' });
    await waitFor(async () => expect(await chargeRows()).toHaveLength(12));
    expect(screen.getByRole('button', { name: 'Tout afficher (24)' })).toBeInTheDocument();
  });

  it('shows no « Tout afficher » with few charges', async () => {
    const charges = Array.from({ length: 5 }, (_, i) => ({ date: `2025-0${i + 1}-28`, price: eur('17.20'), isProjected: false, purchaseId: null }));
    serveDetail(detail({ charges }));
    await renderApp(`/subscriptions/${SUB}?tab=history`);

    const table = await screen.findByRole('table', { name: 'Paiements' });
    expect(within(table).getAllByRole('row')).toHaveLength(6);
    expect(screen.queryByRole('button', { name: /Tout afficher/ })).not.toBeInTheDocument();
  });

  it('loading and error states show no inline back link: the bar leads back to the list', async () => {
    const leadsBack = () => {
      expect(screen.queryByRole('link', { name: 'Retour aux abonnements' })).not.toBeInTheDocument();
      const trail = screen.getByRole('navigation', { name: 'Fil d’Ariane' });
      expect(within(trail).getByRole('link', { name: 'Abonnements' })).toHaveAttribute('href', '/subscriptions');
      expect(screen.getByRole('link', { name: 'Retour à Abonnements' })).toHaveAttribute('href', '/subscriptions');
    };
    server.use(
      http.get('*/api/v1/subscriptions/:id', async () => { await delay('infinite'); return HttpResponse.json({}); }),
      http.get('*/api/v1/core/persons', () => HttpResponse.json(persons)),
    );
    await renderApp(`/subscriptions/${SUB}`);
    expect(await screen.findByLabelText('Chargement de l\'abonnement')).toBeInTheDocument();
    await waitFor(leadsBack);
    cleanup();

    serveDetail(null);
    await renderApp(`/subscriptions/${SUB}`);
    expect(await screen.findByRole('alert')).toBeInTheDocument();
    await waitFor(leadsBack);
  });

  it('ends the trail with the subscription name', async () => {
    serveDetail(detail({ canWrite: false }));
    await renderApp(`/subscriptions/${SUB}`);
    await screen.findByRole('heading', { level: 1, name: 'Spotify Premium Duo' });
    const trail = screen.getByRole('navigation', { name: 'Fil d’Ariane' });
    expect(within(trail).getByText('Spotify Premium Duo')).toHaveAttribute('aria-current', 'page');
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
    await waitFor(() => expect(toasts()).toHaveTextContent('Modification enregistr\u00e9e.'));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole('button', { name: 'Changer le prix' })).toHaveFocus());

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
    await renderApp(`/subscriptions/${SUB}?tab=documents`);

    const section = await screen.findByRole('tabpanel', { name: 'Documents' });
    expect((await within(section).findAllByText('Facture janvier')).length).toBeGreaterThan(0);
  });
});
