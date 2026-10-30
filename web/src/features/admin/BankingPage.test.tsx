import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { adminCapabilities } from '@/test/fixtures';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { formatInstant } from './format';

const settings = { maxAdults: 2, referenceCurrency: 'EUR', adultCount: 2, pricesEnabled: false, irlEnabled: false };
const run = {
  id: '01926b9e-0000-7000-8000-0000000000f3', startedAt: '2026-10-29T03:30:00+00:00', finishedAt: '2026-10-29T03:31:00+00:00',
  trigger: 'schedule', outcome: 'succeeded', connections: 2, accounts: 5, balances: 9, proposals: 3, written: 4, skipped: 1, failureCode: null,
};

function serve(household: object, runs: unknown[] = []) {
  const sent: unknown[] = [];
  server.use(
    http.get('*/api/v1/admin/household', () => HttpResponse.json({ ...settings, ...household })),
    http.get('*/api/v1/admin/banking/runs', () => HttpResponse.json(runs)),
    http.patch('*/api/v1/admin/household', async ({ request }) => {
      const body = await request.json() as object;
      sent.push(body);
      return HttpResponse.json({ ...settings, ...household, ...body });
    }),
  );
  return sent;
}

describe('Administration › Banque', () => {
  it('lists the runs with their counts only, and names the provider and the return address', async () => {
    serve({ bankingEnabled: true, enableBankingAppId: 'app-1' }, [run]);
    const { container } = await renderApp('/admin/banking', { capabilities: adminCapabilities });

    const row = await screen.findByRole('row', { name: /réussi/ });
    expect(row).toHaveTextContent(formatInstant(run.startedAt));
    expect(within(row).getAllByRole('cell').map((c) => c.textContent).slice(1)).toEqual(['Nuit', 'réussi', '2', '5', '9', '3', '4', '1', '']);
    expect(screen.getByText(`Les données bancaires passent par Enable Banking (prestataire agréé, Finlande). Adresse de retour à déclarer : ${window.location.origin}/api/v1/banking/callback`))
      .toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Banque' })).toHaveAttribute('aria-current', 'page');
    await expectNoAxeViolations(container);
  });

  it('turns bank sync on from the switch', async () => {
    const sent = serve({ bankingEnabled: false, enableBankingAppId: null });
    const { user } = await renderApp('/admin/banking', { capabilities: adminCapabilities });

    const toggle = await screen.findByRole('switch', { name: 'Synchronisation bancaire' });
    expect(toggle).not.toBeChecked();
    await user.click(toggle);

    await waitFor(() => expect(sent).toEqual([{ bankingEnabled: true }]));
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Enregistré'));
  });

  it('saves the Enable Banking application id', async () => {
    const sent = serve({ bankingEnabled: true, enableBankingAppId: null });
    const { user } = await renderApp('/admin/banking', { capabilities: adminCapabilities });

    await user.type(await screen.findByLabelText('Identifiant de l’application Enable Banking'), '4f1c-app');
    await user.click(screen.getByRole('button', { name: 'Enregistrer l’identifiant' }));

    await waitFor(() => expect(sent).toEqual([{ enableBankingAppId: '4f1c-app' }]));
  });

  it('keeps the operations for a chosen number of months, or without limit', async () => {
    const sent = serve({ bankingEnabled: true, enableBankingAppId: null, bankRetentionMonths: 36 });
    const { user } = await renderApp('/admin/banking', { capabilities: adminCapabilities });

    expect(await screen.findByRole('combobox', { name: 'Conserver les opérations' })).toHaveTextContent('36 mois');
    await pick(user, 'Conserver les opérations', '24 mois');
    await waitFor(() => expect(sent).toEqual([{ bankRetentionMonths: 24 }]));
    await pick(user, 'Conserver les opérations', 'Sans limite');
    await waitFor(() => expect(sent).toEqual([{ bankRetentionMonths: 24 }, { clearBankRetention: true }]));
  });
});
