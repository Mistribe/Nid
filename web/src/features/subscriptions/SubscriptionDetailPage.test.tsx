import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { Detail } from './api';
import { detail, persons, SUB } from './testData';

export function serveDetail(body: Detail | null) {
  server.use(
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
    expect(screen.getByText(/Prochaine échéance/)).toHaveTextContent('28 janvier 2026');
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

    const form = screen.getByRole('form', { name: 'Ajouter un achat' });
    await user.type(within(form).getByLabelText('Libellé'), 'Carte cadeau');
    await user.type(within(form).getByLabelText('Date'), '2025-12-01');
    await user.type(within(form).getByLabelText('Montant'), '0');
    await user.click(within(form).getByRole('button', { name: 'Ajouter un achat' }));

    const count = within(form).getByLabelText('Durée couverte');
    await waitFor(() => expect(count).toHaveAttribute('aria-invalid', 'true'));
    expect(count).toHaveAccessibleDescription('Saisissez un nombre entier positif.');
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
});
