import { screen, waitFor, within } from '@testing-library/react';
import { delay, http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
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

/** The body rows of the table (the phone list below `md:` repeats them, so the table is the one queried). */
async function rows(): Promise<HTMLElement[]> {
  const table = await screen.findByRole('table', { name: 'Liste des crédits' });
  return within(table).getAllByRole('row').slice(1);
}

const ended = summary({
  id: '0192f0aa-0000-7000-8000-00000000d002', name: 'Prêt auto', lender: null, ended: true, nextPaymentOn: null,
  nextPaymentTotal: null, remainingBalance: '0.00', principal: '12000.00', endDate: '2025-01-15',
});

describe('LoansPage', () => {
  beforeAll(async () => { await import('./LoansPage'); }, 30_000);

  it('shows one table row per item with its owner', async () => {
    const alice = '0192f0aa-0000-7000-8000-0000000000a1';
    server.use(http.get('*/api/v1/core/persons', () => HttpResponse.json([{ id: alice, kind: 'adult', displayName: 'Alice', email: null }])));
    serve(list([summary({ propertyName: 'Maison de Nantes', owners: [{ personId: alice, shareBp: 10000 }] })]));
    const { container } = await renderApp('/loans');

    const table = await screen.findByRole('table', { name: 'Liste des crédits' });
    expect(within(table).getAllByRole('columnheader').map((h) => h.textContent))
      .toEqual(['Nom', 'Bien', 'Propriétaire', 'Restant dû', 'Prochaine échéance', 'Taux', 'Fin']);
    const row = (await rows())[0]!;
    expect(within(row).getByRole('link', { name: /Prêt maison/ })).toHaveAttribute('href', expect.stringContaining(`/loans/${LOAN}`));
    expect(row).toHaveTextContent('Banque');
    expect(row).toHaveTextContent('Maison de Nantes');
    await waitFor(() => expect(row).toHaveTextContent('Alice'));
    expect(row).toHaveTextContent('193 564,23 €');
    expect(row).toHaveTextContent('15 oct. 2026');
    expect(row).toHaveTextContent('1 159,92 €');
    expect(row).toHaveTextContent('3,5 %');
    expect(row).toHaveTextContent('15 oct. 2045');
    expect(within(row).getByText('Privé')).toBeInTheDocument();
    expect(within(row).queryByText('Terminé')).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('sorts by the balance left', async () => {
    serve(list([summary({ name: 'Gros' }), summary({ id: 'b', name: 'Petit', remainingBalance: '1000.00' })]));
    const { user } = await renderApp('/loans');

    const names = async () => (await rows()).map((r) => within(r).getAllByRole('cell')[0]!.textContent?.match(/^(Gros|Petit)/)?.[0]);
    expect(await names()).toEqual(['Gros', 'Petit']);
    await user.click(within(screen.getByRole('table', { name: 'Liste des crédits' })).getByRole('button', { name: 'Restant dû' }));
    expect(await names()).toEqual(['Petit', 'Gros']);
  });

  it('the add link is always shown, even when no row is writable', async () => {
    serve(list([summary({ canWrite: false })]));
    await renderApp('/loans');

    await rows();
    expect(screen.getByRole('link', { name: 'Ajouter un crédit' })).toBeInTheDocument();
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
    expect(screen.getByRole('switch', { name: 'Afficher les crédits terminés' })).toBeChecked();
  });

  it('shows a skeleton while the list loads', async () => {
    server.use(http.get('*/api/v1/realestate/loans', async () => { await delay('infinite'); return HttpResponse.json(list([])); }));
    await renderApp('/loans');

    expect(await screen.findByLabelText('Chargement des crédits')).toHaveAttribute('aria-busy', 'true');
  });

  it('shows a failed load as a problem', async () => {
    server.use(http.get('*/api/v1/realestate/loans', () => HttpResponse.json({ status: 500, code: 'ERROR' }, { status: 500 })));
    await renderApp('/loans');

    expect(await screen.findByRole('alert', {})).toBeInTheDocument();
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

    const box = await screen.findByRole('switch', { name: 'Afficher les crédits terminés' });
    expect(box).not.toBeChecked();
    await rows();
    await user.click(box);

    await waitFor(() => expect(seen).toContain('?scope=household&ended=true'));
    expect(router.state.location.search).toMatchObject({ ended: 1 });
    await waitFor(async () => expect(await rows()).toHaveLength(2));
    const items = await rows();
    expect(within(items[1]!).getByText('Terminé')).toBeInTheDocument();
    expect(within(items[1]!).getAllByRole('cell')[4]).toHaveTextContent('—');
    expect(within(items[1]!).getAllByRole('cell')[5]).toHaveTextContent('—');
    expect(items[1]).toHaveTextContent('15 janv. 2025');
    expect(screen.getByRole('switch', { name: 'Afficher les crédits terminés' })).toHaveFocus();
    await expectNoAxeViolations(container);
  });

  it('starts checked from the URL and ignores any value other than 1', async () => {
    const seen: string[] = [];
    serve(list([summary()]), seen);
    await renderApp('/loans?ended=yes');

    await rows();
    expect(screen.getByRole('switch', { name: 'Afficher les crédits terminés' })).not.toBeChecked();
    expect(seen).toEqual(['?scope=household']);
  });
});
