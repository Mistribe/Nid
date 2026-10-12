import { screen, waitFor, within } from '@testing-library/react';
import { delay, http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { LoanList } from './api';
import { list, LOAN, summary } from './testData';

/** `withEnded` answers `ended=true`; the plain list answers everything else. */
function serve(body: LoanList, seen: string[] = [], withEnded: LoanList = body) {
  server.use(http.get('*/api/v1/realestate/loans', ({ request }) => {
    const search = new URL(request.url).search;
    seen.push(search);
    return HttpResponse.json(search.includes('ended=true') ? withEnded : body);
  }));
}

async function rows(): Promise<HTMLElement[]> {
  const ul = await screen.findByRole('list', { name: 'Liste des crédits' });
  return within(ul).getAllByRole('listitem');
}

const ended = summary({
  id: '0192f0aa-0000-7000-8000-00000000d002', name: 'Prêt auto', lender: null, ended: true, nextPaymentOn: null,
  nextPaymentTotal: null, remainingBalance: '0.00', principal: '12000.00', endDate: '2025-01-15',
});

describe('LoansPage', () => {
  it('shows each loan: name and lender, balance on principal, next payment with insurance, rate, end and access', async () => {
    serve(list([summary()]));
    const { container } = await renderApp('/loans');

    const row = (await rows())[0]!;
    expect(within(row).getByRole('link', { name: 'Prêt maison' })).toHaveAttribute('href', expect.stringContaining(`/loans/${LOAN}`));
    expect(row).toHaveTextContent('Banque');
    expect(row).toHaveTextContent('Reste 193 564,23 € sur 200 000,00 €');
    expect(row).toHaveTextContent('Prochaine échéance le 15 octobre 2026 : 1 159,92 €, assurance comprise');
    expect(row).toHaveTextContent('Taux 3,5 %');
    expect(row).toHaveTextContent('Fin le 15 octobre 2045');
    expect(within(row).getByText('Privé')).toBeInTheDocument();
    expect(within(row).queryByText('Terminé')).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('offers « Ajouter un crédit » above the list', async () => {
    serve(list([summary()]));
    await renderApp('/loans');

    await rows();
    expect(screen.getByRole('link', { name: 'Ajouter un crédit' })).toHaveAttribute('href', '/loans/new');
  });

  it('invites to add the first loan when there is none at all', async () => {
    const seen: string[] = [];
    serve(list([]), seen);
    const { container } = await renderApp('/loans');

    expect(await screen.findByRole('heading', { name: 'Aucun crédit pour l’instant' })).toBeInTheDocument();
    expect(screen.getAllByRole('link', { name: 'Ajouter un crédit' })[0]).toHaveAttribute('href', '/loans/new');
    expect(seen).toEqual(['?scope=household', '?scope=household&ended=true']);
    await expectNoAxeViolations(container);
  });

  it('says nothing is running when only ended loans exist, with a link that shows them', async () => {
    const seen: string[] = [];
    serve(list([]), seen, list([ended]));
    const { user, router, container } = await renderApp('/loans');

    expect(await screen.findByRole('heading', { name: 'Aucun crédit en cours' })).toBeInTheDocument();
    expect(seen).toEqual(['?scope=household', '?scope=household&ended=true']);
    await expectNoAxeViolations(container);
    await user.click(screen.getByRole('link', { name: 'Voir les crédits terminés' }));

    await waitFor(() => expect(router.state.location.search).toMatchObject({ ended: 1 }));
    expect(screen.getByRole('checkbox', { name: 'Afficher les crédits terminés' })).toBeChecked();
  });

  it('shows a skeleton while the list loads', async () => {
    server.use(http.get('*/api/v1/realestate/loans', async () => { await delay('infinite'); return HttpResponse.json(list([])); }));
    await renderApp('/loans');

    expect(await screen.findByLabelText('Chargement des crédits')).toHaveAttribute('aria-busy', 'true');
  });

  it('shows a failed load as a problem', async () => {
    server.use(http.get('*/api/v1/realestate/loans', () => HttpResponse.json({ status: 500, code: 'ERROR' }, { status: 500 })));
    await renderApp('/loans');

    expect(await screen.findByRole('alert', {}, { timeout: 8000 })).toBeInTheDocument();
  });

  it('passes the Moi scope to the API', async () => {
    const seen: string[] = [];
    serve(list([summary()]), seen);
    await renderApp('/loans?scope=me');

    await rows();
    expect(seen).toEqual(['?scope=me']);
  });
});

describe('LoansPage, ended loans', () => {
  it('asks for them only when the box is checked, keeps that in the URL, and marks them in words, without next payment or rate', async () => {
    const seen: string[] = [];
    serve(list([summary()]), seen, list([summary(), ended]));
    const { user, router, container } = await renderApp('/loans');

    const box = await screen.findByRole('checkbox', { name: 'Afficher les crédits terminés' });
    expect(box).not.toBeChecked();
    await rows();
    await user.click(box);

    await waitFor(() => expect(seen).toContain('?scope=household&ended=true'));
    expect(router.state.location.search).toMatchObject({ ended: 1 });
    await waitFor(() => expect(within(screen.getByRole('list', { name: 'Liste des crédits' })).getAllByRole('listitem')).toHaveLength(2));
    const items = await rows();
    expect(within(items[1]!).getByText('Terminé')).toBeInTheDocument();
    expect(items[1]).not.toHaveTextContent('Prochaine échéance');
    expect(items[1]).not.toHaveTextContent('Taux');
    expect(items[1]).toHaveTextContent('Fin le 15 janvier 2025');
    expect(screen.getByRole('checkbox', { name: 'Afficher les crédits terminés' })).toHaveFocus();
    await expectNoAxeViolations(container);
  });

  it('starts checked from the URL and ignores any value other than 1', async () => {
    const seen: string[] = [];
    serve(list([summary()]), seen);
    await renderApp('/loans?ended=yes');

    await rows();
    expect(screen.getByRole('checkbox', { name: 'Afficher les crédits terminés' })).not.toBeChecked();
    expect(seen).toEqual(['?scope=household']);
  });
});
