import { link, list, summary } from '../documents/testData';
import { cleanup, screen, waitFor, within } from '@testing-library/react';
import { delay } from 'msw';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { expectTrailBack } from '@/test/trail';
import { server } from '@/test/server';
import { toasts } from '@/test/toasts';
import type { EnvelopeDetail } from './api';
import { todayIso } from '../subscriptions/today';
import { serve as serveWritable, type Call } from './serveEnvelope';
import { detail, eur, figures, PEA } from './testData';

const serve = (body: EnvelopeDetail) => server.use(http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json(body)));

describe('EnvelopeDetailPage', () => {
  // The lazy route chunk loaded cold inside the first test races its timeouts under load.
  beforeAll(async () => { await import('./EnvelopeDetailPage'); }, 30_000);

  it('loading and error states show no inline back link: the bar leads back to the list', async () => {
    server.use(http.get('*/api/v1/investments/envelopes/:id', async () => { await delay('infinite'); return HttpResponse.json({}); }));
    await renderApp(`/investments/${PEA}`);
    expect(await screen.findByLabelText('Chargement de l’enveloppe')).toBeInTheDocument();
    await waitFor(() => expectTrailBack('Placements', '/investments'));
    cleanup();

    server.use(http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json({ status: 404, code: null, title: 'Not found' }, { status: 404 })));
    await renderApp(`/investments/${PEA}`);
    expect(await screen.findByRole('heading', { level: 1, name: 'Page introuvable' })).toBeInTheDocument();
    await waitFor(() => expectTrailBack('Placements', '/investments'));
  });

  it('shows the figures, before tax, and the two tables newest first', async () => {
    serve(detail());
    const { user, container } = await renderApp(`/investments/${PEA}`);

    expect(await screen.findByRole('heading', { name: 'PEA', level: 1 })).toBeInTheDocument();
    const figuresBlock = screen.getByRole('region', { name: 'Valeur et gain' });
    expect(figuresBlock).toHaveTextContent('25 000,00 €');
    expect(figuresBlock).toHaveTextContent('22 000,00 €');
    expect(figuresBlock).toHaveTextContent('+3 000,00 €');
    expect(figuresBlock).toHaveTextContent('Avant fiscalité');
    await expectNoAxeViolations(container);

    await user.click(screen.getByRole('tab', { name: 'Valeurs (2)' }));
    const values = within(await screen.findByRole('table', { name: 'Valeurs de l’enveloppe' })).getAllByRole('row').slice(1);
    expect(values[0]).toHaveTextContent('30 septembre 2025');
    await user.click(screen.getByRole('tab', { name: 'Flux (2)' }));
    const flows = within(await screen.findByRole('table', { name: 'Flux de l’enveloppe' })).getAllByRole('row').slice(1);
    expect(flows[0]).toHaveTextContent('Versement');
    expect(flows[1]).toHaveTextContent('Versements antérieurs');
    await expectNoAxeViolations(container);
  });

  it('shows a livret\'s ceiling and the year\'s interest as estimates (issues 399, 401)', async () => {
    serve(detail({
      kind: 'savings', regulated: 'livret_a',
      ceiling: { ceiling: eur('22950'), room: eur('3600.0000'), reached: false, since: '2013-01-01', source: 'Banque de France' },
      interest: { year: 2025, toDate: eur('173.33'), expected: eur('215.83'), ratePct: 1.7, source: 'Banque de France' },
    }));
    const { container } = await renderApp(`/investments/${PEA}`);
    const figuresBlock = await screen.findByRole('region', { name: 'Valeur et gain' });
    expect(figuresBlock).toHaveTextContent('22 950,00 € · encore 3 600,00 € versables');
    expect(figuresBlock).toHaveTextContent('Intérêts estimés à ce jour173,33 €');
    expect(figuresBlock).toHaveTextContent('attendus au 31 décembre : 215,83 €');
    expect(figuresBlock).toHaveTextContent('Estimation au taux réglementé (1,7 % aujourd’hui)');
    await expectNoAxeViolations(container);
    cleanup();

    serve(detail({ kind: 'savings', regulated: 'livret_a', ceiling: { ceiling: eur('22950'), room: eur('0'), reached: true, since: '2013-01-01', source: 'Banque de France' } }));
    await renderApp(`/investments/${PEA}`);
    expect(await screen.findByRole('region', { name: 'Valeur et gain' })).toHaveTextContent('22 950,00 € · atteint');
  });

  it('announces the next tax date and leads to the tax on a sale (issue 400)', async () => {
    serve(detail({ kind: 'life_insurance', milestone: { code: 'life_insurance_8y', on: '2028-10-15' } }));
    const { user } = await renderApp(`/investments/${PEA}?tab=values`);
    const figuresBlock = await screen.findByRole('region', { name: 'Valeur et gain' });
    expect(figuresBlock).toHaveTextContent('8 ans le 15 oct. 2028');
    await user.click(within(figuresBlock).getByRole('button', { name: 'Impôt si vente aujourd’hui' }));
    expect(screen.getByRole('tab', { name: 'Vue d’ensemble' })).toHaveAttribute('aria-selected', 'true');
  });

  it('shows the source document next to a value or a flow that has one, and none otherwise', async () => {
    const base = detail();
    serve({ ...base, valuations: [{ ...base.valuations[0]!, documentId: 'd1' }, base.valuations[1]!], flows: [{ ...base.flows[0]!, documentId: 'd2' }, base.flows[1]!] });
    const { user } = await renderApp(`/investments/${PEA}?tab=values`);

    const values = within(await screen.findByRole('table', { name: 'Valeurs de l’enveloppe' })).getAllByRole('row').slice(1);
    expect(within(values[1]!).getByRole('link', { name: 'Source du chiffre' })).toHaveAttribute('href', '/documents/d1');
    expect(within(values[0]!).queryByRole('link', { name: 'Source du chiffre' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('tab', { name: 'Flux (2)' }));
    const flows = within(await screen.findByRole('table', { name: 'Flux de l’enveloppe' })).getAllByRole('row').slice(1);
    expect(within(flows[1]!).getByRole('link', { name: 'Source du chiffre' })).toHaveAttribute('href', '/documents/d2');
    expect(within(flows[0]!).queryByRole('link', { name: 'Source du chiffre' })).not.toBeInTheDocument();
  });

  it('shows the estimated value, converted or not, once the holdings carry one', async () => {
    serve(detail());
    server.use(http.get('*/api/v1/investments/envelopes/:id/holdings', () => HttpResponse.json({
      envelopeId: PEA, currency: 'EUR', holdingsAsOf: '2025-09-30', snapshotId: 's1', complete: true, total: eur('4500.0000'),
      unallocated: null, explicitFees: null, ucSharePct: null, ucShareFloorPct: null, holdings: [], warnings: [],
      estimate: { value: eur('26100.00'), asOf: '2025-10-02', basedOn: '2025-09-30', converted: true, stale: [], missingReason: null },
    })));
    await renderApp(`/investments/${PEA}`);

    await screen.findByRole('heading', { name: 'PEA', level: 1 });
    const figuresBlock = screen.getByRole('region', { name: 'Valeur et gain' });
    await waitFor(() => expect(figuresBlock).toHaveTextContent('Valeur estimée au 2 oct. 2025'));
    expect(figuresBlock).toHaveTextContent('26 100,00 €');
    expect(figuresBlock).toHaveTextContent('convertie');
  });

  it('warns when the opening may be missing', async () => {
    serve(detail({ figures: figures({ missingOpening: true }), flows: [] }));
    await renderApp(`/investments/${PEA}`);

    expect(await screen.findByText('Le gain comprend peut-être des versements faits avant le début du suivi.')).toBeInTheDocument();
  });

  it('shows what was paid in or out since the last value, and fees and income only when there are any', async () => {
    serve(detail({ figures: figures({ contributionsSince: eur('700.0000'), withdrawalsSince: eur('50.0000'), fees: eur('120.0000'), gainAfterFees: eur('2880.0000') }) }));
    await renderApp(`/investments/${PEA}`);

    const block = await screen.findByRole('region', { name: 'Valeur et gain' });
    expect(block).toHaveTextContent('700,00 € versés, 50,00 € retirés');
    expect(block).toHaveTextContent('120,00 €');
    expect(block).not.toHaveTextContent('Revenus perçus');
  });

  it('labels a transfer leg as a transfer', async () => {
    serve(detail({ flows: [{ id: 'f9', occurredOn: '2025-06-15', kind: 'withdrawal', amount: eur('100.0000'), category: 'transfer', externalRef: 'transfer:x', note: null }] }));
    await renderApp(`/investments/${PEA}?tab=flows`);

    const flows = within(await screen.findByRole('table', { name: 'Flux de l’enveloppe' })).getAllByRole('row').slice(1);
    expect(flows[0]).toHaveTextContent('Virement');
  });

  it('gives the other adult a read-only page without any form, action or Actions column', async () => {
    serve(detail({ canWrite: false }));
    const { container } = await renderApp(`/investments/${PEA}`);

    expect(await screen.findByText('Vous pouvez consulter cette enveloppe, pas la modifier.')).toBeInTheDocument();
    const article = screen.getByRole('article');
    expect(within(article).queryByRole('button')).not.toBeInTheDocument();
    expect(within(article).queryByRole('textbox')).not.toBeInTheDocument();
    expect(screen.queryByRole('tab', { name: 'Réglages' })).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('shows a placeholder row in each table when there are no values or flows yet', async () => {
    serve(detail({ valuations: [], flows: [] }));
    const { user, container } = await renderApp(`/investments/${PEA}?tab=values`);

    expect(await screen.findByText('Aucune valeur pour l’instant')).toBeInTheDocument();
    await expectNoAxeViolations(container);
    await user.click(screen.getByRole('tab', { name: 'Flux (0)' }));
    expect(await screen.findByText('Aucun flux pour l’instant')).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('says an unknown envelope is not found', async () => {
    server.use(http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json({ status: 404, code: null, title: 'Not found' }, { status: 404 })));
    await renderApp(`/investments/${PEA}`);

    const heading = await screen.findByRole('heading', { level: 1, name: 'Page introuvable' });
    // Issue 362: the module's tile on the nest, the way to its list; the trail still leads back to it.
    const page = heading.closest('section')!;
    expect(page.querySelector('[data-tile="1"]')).not.toBeNull();
    expect(within(page).getByRole('link', { name: 'Ouvrir Placements' })).toHaveAttribute('href', '/investments');
    expectTrailBack('Placements', '/investments');
    await expectNoAxeViolations(page);
  });

  it('says a server error plainly, with the request id, and tries again on « Réessayer »', async () => {
    let calls = 0;
    server.use(http.get('*/api/v1/investments/envelopes/:id', () => {
      calls += 1;
      return HttpResponse.json({ status: 500, title: 'Boom', traceId: 'req-42' }, { status: 500 });
    }));
    const { user } = await renderApp(`/investments/${PEA}`);

    // A 5xx is retried twice with backoff before the error shows (createQueryClient).
    const heading = await screen.findByRole('heading', { level: 1, name: 'Nid n’a pas pu afficher cette page' }, { timeout: 10_000 });
    const page = within(heading.closest('section')!);
    expect(page.getByText('Identifiant pour le support : req-42')).toBeInTheDocument();
    const before = calls;
    await user.click(page.getByRole('button', { name: 'Réessayer' }));
    await waitFor(() => expect(calls).toBeGreaterThan(before));
  }, 20_000);
  it('shows the key figures inline', async () => {
    serve(detail());
    server.use(http.get('*/api/v1/investments/envelopes/:id/performance', () => HttpResponse.json({ from: '2025-01-01', to: '2025-10-18', performance: eur('439.0000') })));
    await renderApp(`/investments/${PEA}`);

    const line = (await screen.findByText('Valeur au 30 sept. 2025')).closest('dl')!;
    expect(line).toHaveTextContent('25 000,00 €');
    expect(within(line).getByText('Versements nets').nextElementSibling).toHaveTextContent('22 000,00 €');
    expect(within(line).getByText('Gain').nextElementSibling).toHaveTextContent(/\+3\s000,00\s€.*\+13,6\s%/);
    expect(within(line).getByText('Rendement annuel').nextElementSibling).toHaveTextContent(/\+14,2\s%\spar an/);
    await waitFor(() => expect(within(line).getByText('Depuis janvier').nextElementSibling).toHaveTextContent('+439,00 €'));
  });

  it('dates in figure labels are short', async () => {
    serve(detail());
    await renderApp(`/investments/${PEA}`);

    expect(await screen.findByText('Valeur au 30 sept. 2025', { selector: 'dt' })).toBeInTheDocument();
  });

  it('enters a value from the header, in a dialog that closes and updates the values tab', async () => {
    const calls: Call[] = [];
    serveWritable(detail(), calls);
    const { user, container } = await renderApp(`/investments/${PEA}`);

    await user.click(await screen.findByRole('button', { name: 'Saisir une valeur' }));
    const dialog = await screen.findByRole('dialog', { name: 'Nouvelle valeur' });
    expect(within(dialog).getByLabelText('Date')).toHaveValue(todayIso().split('-').reverse().join('/'));
    expect(within(dialog).getByLabelText('Note')).toBeInTheDocument();
    await user.clear(within(dialog).getByLabelText('Montant'));
    await user.type(within(dialog).getByLabelText('Montant'), '25400,10');
    await expectNoAxeViolations(document.body);
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer la valeur' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(await screen.findByRole('tab', { name: 'Valeurs (3)' })).toBeInTheDocument();
    expect(calls).toEqual([{ method: 'POST', path: 'valuations', body: { asOf: todayIso(), amount: { amount: '25400.10', currency: 'EUR' } } }]);
    await waitFor(() => expect(toasts()).toHaveTextContent('Valeur enregistrée.'));
    await expectNoAxeViolations(container);
  });

  it('corrects a value from its row menu', async () => {
    const calls: Call[] = [];
    serveWritable(detail(), calls);
    const { user } = await renderApp(`/investments/${PEA}?tab=values`);

    const table = await screen.findByRole('table', { name: 'Valeurs de l’enveloppe' });
    await user.click(within(table).getByRole('button', { name: /^Actions pour le 30 septembre 2025/ }));
    await user.click(await screen.findByRole('menuitem', { name: 'Corriger' }));
    const dialog = await screen.findByRole('dialog', { name: 'Corriger la valeur' });
    expect(within(dialog).getByLabelText('Date')).toHaveAttribute('readonly');
    await user.clear(within(dialog).getByLabelText('Montant'));
    await user.type(within(dialog).getByLabelText('Montant'), '26000');
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));

    await within(table).findByText('26 000,00 €');
    expect(calls).toEqual([{ method: 'POST', path: 'valuations', body: expect.objectContaining({ asOf: '2025-09-30', amount: { amount: '26000', currency: 'EUR' } }) as unknown }]);
  });

  it('deletes a flow after confirmation', async () => {
    const calls: Call[] = [];
    serveWritable(detail(), calls);
    const { user } = await renderApp(`/investments/${PEA}?tab=flows`);

    const table = await screen.findByRole('table', { name: 'Flux de l’enveloppe' });
    await user.click(within(table).getByRole('button', { name: /^Actions pour le 15 juin 2025/ }));
    await user.click(await screen.findByRole('menuitem', { name: 'Supprimer' }));
    expect(calls).toEqual([]);
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));

    await waitFor(() => expect(calls).toEqual([{ method: 'DELETE', path: 'flows/f2' }]));
    await waitFor(() => expect(screen.getByRole('tab', { name: 'Flux (1)' })).toBeInTheDocument());
  });

  it('puts the transfer and the closing in the ⋯ menu', async () => {
    serve(detail());
    const { user } = await renderApp(`/investments/${PEA}`);

    expect(await screen.findByRole('button', { name: 'Saisir une valeur' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Virer vers une autre enveloppe' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Autres actions' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Virer vers une autre enveloppe' }));
    expect(await screen.findByRole('dialog', { name: 'Virement' })).toBeInTheDocument();
  });

  it('folds « Ajouter un flux » into ⋯ on a phone: the menu starts with it, shown below sm only', async () => {
    serve(detail());
    const { user } = await renderApp(`/investments/${PEA}`);

    expect(await screen.findByRole('button', { name: 'Ajouter un flux' })).toHaveClass('max-sm:hidden');
    await user.click(screen.getByRole('button', { name: 'Autres actions' }));
    const first = (await screen.findAllByRole('menuitem'))[0]!;
    expect(first).toHaveTextContent('Ajouter un flux');
    expect(first).toHaveClass('sm:hidden');
    await user.click(first);
    expect(await screen.findByRole('dialog', { name: 'Nouveau flux' })).toBeInTheDocument();
  });

  it('hands the focus to ⋯ after a flow added while « Ajouter un flux » is folded away', async () => {
    serveWritable(detail(), []);
    const { user } = await renderApp(`/investments/${PEA}`);

    const flow = await screen.findByRole('button', { name: 'Ajouter un flux' });
    Object.defineProperty(flow, 'checkVisibility', { value: () => false });
    await user.click(flow);
    const dialog = within(await screen.findByRole('dialog', { name: 'Nouveau flux' }));
    await user.type(dialog.getByLabelText('Montant'), '150');
    await user.click(dialog.getByRole('button', { name: 'Ajouter le flux' }));

    await waitFor(() => expect(screen.getByRole('button', { name: 'Autres actions' })).toHaveFocus());
  });

  it('offers a reader no write action', async () => {
    serve(detail({ canWrite: false }));
    await renderApp(`/investments/${PEA}`);

    await screen.findByRole('heading', { name: 'PEA', level: 1 });
    for (const name of ['Saisir une valeur', 'Ajouter un flux', 'Autres actions']) expect(screen.queryByRole('button', { name })).not.toBeInTheDocument();
    expect(screen.queryByRole('tab', { name: 'Réglages' })).not.toBeInTheDocument();
  });

  it('saves the settings tab as before', async () => {
    const calls: unknown[] = [];
    serve(detail());
    server.use(http.patch('*/api/v1/investments/envelopes/:id', async ({ request }) => { calls.push(await request.json()); return HttpResponse.json({ id: PEA }); }));
    const { user } = await renderApp(`/investments/${PEA}`);

    await user.click(await screen.findByRole('tab', { name: 'Réglages' }));
    const form = await screen.findByRole('form', { name: 'Réglages' });
    await user.clear(within(form).getByLabelText('Nom'));
    await user.type(within(form).getByLabelText('Nom'), 'PEA Bourse');
    await user.click(within(form).getByRole('button', { name: 'Enregistrer les réglages' }));

    expect(await screen.findByText('Réglages enregistrés.')).toBeInTheDocument();
    expect(calls).toEqual([expect.objectContaining({ displayName: 'PEA Bourse' })]);
  });

  it('has a documents tab before the settings, listing the documents anchored on the envelope', async () => {
    serve(detail());
    server.use(http.get('*/api/v1/documents', () => HttpResponse.json(list([summary({ title: 'Pièce du dossier', links: [link({ targetId: PEA, anchorItemId: PEA })] })]))));
    const { user } = await renderApp(`/investments/${PEA}`);

    await screen.findAllByRole('tab');
    const names = screen.getAllByRole('tab').map((tab) => tab.textContent);
    expect(names.indexOf('Documents')).toBe(names.indexOf('Réglages') - 1);
    await user.click(screen.getByRole('tab', { name: 'Documents' }));
    expect((await screen.findAllByText('Pièce du dossier')).length).toBeGreaterThan(0);
    expect(screen.getByRole('button', { name: 'Déposer' })).toBeInTheDocument();
  });
  it('says which bank account feeds the envelope, its last balance, and a balance left out for a typed value', async () => {
    serve(detail());
    server.use(
      http.get('*/api/v1/banking/status', () => HttpResponse.json({ enabled: true, configured: true })),
      http.get('*/api/v1/banking/accounts', ({ request }) => HttpResponse.json(new URL(request.url).searchParams.get('envelopeId') === PEA ? [{
        id: 'b1', name: 'Compte titres BNP', ibanMasked: null, kind: 'other', currency: 'EUR', envelopeId: PEA, cardOfAccountId: null,
        autoApplyBalance: true, ignored: false, balanceConflictOn: '2025-08-31', lastBalance: '25000.00', lastBalanceOn: '2025-09-30', synced: true, canWrite: true,
      }] : [])),
    );
    await renderApp(`/investments/${PEA}`);

    const figuresBlock = await screen.findByRole('region', { name: 'Valeur et gain' });
    expect(await within(figuresBlock).findByText('Relié à Compte titres BNP · dernier solde du 30/09')).toBeInTheDocument();
    expect(within(figuresBlock).getByText('Solde bancaire du 31/08 non appliqué : une valeur saisie existe à cette date')).toBeInTheDocument();
  });

  it('says nothing about a bank while bank sync is off', async () => {
    serve(detail());
    const { queryClient } = await renderApp(`/investments/${PEA}`);

    await screen.findByRole('region', { name: 'Valeur et gain' });
    await waitFor(() => expect(queryClient.isFetching()).toBe(0));
    expect(screen.queryByText(/Relié à/)).toBeNull();
  });

  it('has an « Opérations » tab once bank sync is on, even without Enable Banking (statement files alone)', async () => {
    serve(detail({ kind: 'checking' }));
    server.use(
      http.get('*/api/v1/banking/status', () => HttpResponse.json({ enabled: true, configured: false })),
      http.get('*/api/v1/banking/envelopes/:id/transactions', () => HttpResponse.json({ items: [], hasMore: false })),
    );
    const { user } = await renderApp(`/investments/${PEA}`);

    await user.click(await screen.findByRole('tab', { name: 'Opérations' }));
    expect(await screen.findByText('Aucune opération.')).toBeInTheDocument();
  });

  it('has no « Opérations » tab while bank sync is off', async () => {
    serve(detail({ kind: 'checking' }));
    const { queryClient } = await renderApp(`/investments/${PEA}`);

    await screen.findByRole('tab', { name: 'Documents' });
    await waitFor(() => expect(queryClient.isFetching()).toBe(0));
    expect(screen.queryByRole('tab', { name: 'Opérations' })).toBeNull();
  });

  it('has no « Opérations » tab on an envelope no bank account goes with (a PEA), bank sync on or not', async () => {
    serve(detail());
    server.use(http.get('*/api/v1/banking/status', () => HttpResponse.json({ enabled: true, configured: true })));
    const { queryClient } = await renderApp(`/investments/${PEA}?tab=operations`);

    expect(await screen.findByRole('tab', { name: 'Vue d’ensemble', selected: true })).toBeInTheDocument();
    await waitFor(() => expect(queryClient.isFetching()).toBe(0));
    expect(screen.queryByRole('tab', { name: 'Opérations' })).toBeNull();
  });
});
