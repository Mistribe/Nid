import { screen, waitFor, within } from '@testing-library/react';
import { beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { HttpResponse, http } from 'msw/http';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { resetFocus } from './focus';
import { proposal } from './testData';
import { serve } from './testServe';

/** Spec 4D3: a proposal that replays a web write has one read-only screen. */
describe('Une proposition d’action du web', () => {
  beforeEach(resetFocus);
  beforeAll(async () => { await import('./ProposalDetailPage'); await import('./ProposalsPage'); });

  const creation = () => proposal({
    kind: 'realestate.create_property', targetItemId: null, targetName: null, subjectKey: null, sourceLabel: 'Acte de vente',
    payload: { body: { name: 'Maison', currency: 'EUR', owners: [{ personId: 'a', shareBp: 10000 }], purchasePrice: '250000' } },
  });

  it('shows the action and every field proposed, and approves it as proposed', async () => {
    const p = creation();
    const sent = serve([p], { preview: {} });
    const { user, container } = await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByRole('heading', { level: 1, name: 'Nouveau bien' })).toBeInTheDocument();
    expect(screen.getByRole('row', { name: /^Nom/ })).toHaveTextContent('Maison');
    const owner = within(await screen.findByRole('region', { name: 'Propriétaire 1 — Camille' }));
    expect(owner.getByRole('row', { name: /^Part/ })).toHaveTextContent('100 %');
    expect(screen.queryByRole('form')).not.toBeInTheDocument();
    await expectNoAxeViolations(container);

    await user.click(screen.getByRole('button', { name: 'Approuver' }));
    await waitFor(() => expect(sent.approve).toEqual({ version: 1 }));
  });

  it('names the documents of a filing round by their titles', async () => {
    const p = proposal({
      kind: 'documents.file_documents', targetItemId: null, targetName: null, subjectKey: null,
      payload: { body: { edits: [{ id: 'doc-1', documentDate: '2024-03-31' }] } },
    });
    serve([p], { preview: {} });
    server.use(http.get('*/api/v1/documents', () => HttpResponse.json({ items: [{ id: 'doc-1', title: 'Relevé mars' }] })));
    await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByRole('heading', { level: 1, name: 'Classement de documents' })).toBeInTheDocument();
    const edit = within(await screen.findByRole('region', { name: 'Modification 1 — Relevé mars' }));
    expect(edit.getByRole('row', { name: /^Date du document/ })).toHaveTextContent('31 mars 2024');
  });

  it('is listed by its action and the name it will have', async () => {
    serve([creation()]);
    await renderApp('/proposals');
    const table = within(await screen.findByRole('table', { name: 'Liste des propositions' }));
    expect(table.getByRole('link', { name: 'Nouveau bien — Maison' })).toBeInTheDocument();
  });

  it('links a change to the item it targets', async () => {
    const p = proposal({
      kind: 'income.add_one_off_income', targetItemId: '00000000-0000-7000-8000-0000000000e2', targetName: 'Salaire', subjectKey: null,
      payload: { id: '00000000-0000-7000-8000-0000000000e2', body: { occurredOn: '2025-06-20', amount: { amount: '1500.00', currency: 'EUR' } } },
    });
    serve([p], { preview: {} });
    await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByRole('link', { name: 'Salaire' })).toHaveAttribute('href', `/income/${p.targetItemId}`);
    expect(screen.getByRole('row', { name: /^Montant/ })).toHaveTextContent(/1\s500,00\s€/);
  });
  it('warns that a set_insurance proposal replaces the whole insurance', async () => {
    const id = '00000000-0000-7000-8000-0000000000e3';
    const p = proposal({
      kind: 'subscriptions.set_insurance', targetItemId: id, targetName: 'Assurance habitation', subjectKey: null,
      payload: { id, body: { kind: 'home', keepProperty: true, guarantees: [{ label: 'Incendie' }] } },
    });
    serve([p], { preview: {} });
    await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByText('Remplace toute l’assurance : ce qui n’est pas listé est retiré.')).toBeInTheDocument();
    expect(screen.getByRole('row', { name: /^Garder le bien lié/ })).toHaveTextContent('Oui');
    expect(screen.getByRole('row', { name: /^Type/ })).toHaveTextContent('Habitation');
    expect(screen.getByRole('region', { name: 'Garantie 1 — Incendie' })).toBeInTheDocument();
  });

  it('shows no such notice on another action', async () => {
    const p = creation();
    serve([p], { preview: {} });
    await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByRole('heading', { level: 1, name: 'Nouveau bien' })).toBeInTheDocument();
    expect(screen.queryByText(/Remplace toute l’assurance/)).not.toBeInTheDocument();
  });

  /** Issue #290: a tax notice, the worst case: no item, a year, declarants by person, raw field names. */
  const notice = (kind: string, over: Record<string, unknown> = {}) => proposal({
    kind, targetItemId: null, targetName: null, subjectKey: null, sourceLabel: 'Avis 2025',
    payload: {
      ...(kind === 'tax.add_tax_year' ? {} : { id: '00000000-0000-7000-8000-0000000000f1' }),
      body: {
        year: 2024, issuedYear: 2025, version: 3, shares: '2', netTax: { amount: '4200.00', currency: 'EUR' },
        declarants: [{ personId: 'a', rank: 1, deduction: 'allowance', grossSalaries: { amount: '52000.00', currency: 'EUR' } }],
      },
      ...over,
    },
  });

  /** The notice as recorded, the server's `{ current }` preview of a correction or a deletion (issue #321). */
  const recorded = {
    id: '00000000-0000-7000-8000-0000000000f1', version: 3, year: 2024, issuedYear: 2025, shares: '2', netTax: { amount: '4100.00', currency: 'EUR' },
    declarants: [{ personId: 'a', rank: 1, deduction: 'allowance', grossSalaries: { amount: '52000.00', currency: 'EUR' } }],
  };
  const deletion = () => proposal({
    kind: 'tax.delete_tax_year', targetItemId: null, targetName: null, subjectKey: null, payload: { id: '00000000-0000-7000-8000-0000000000f1' },
  });

  it('names a tax notice by its year in the list, a deletion by the notice it removes', async () => {
    serve([notice('tax.add_tax_year'), notice('tax.correct_tax_year'), deletion()]);
    server.use(http.get('*/api/v1/tax/assessments', () => HttpResponse.json({ items: [recorded] })));
    await renderApp('/proposals');
    const table = within(await screen.findByRole('table', { name: 'Liste des propositions' }));
    expect(table.getByRole('link', { name: 'Nouvel avis d’imposition — Avis d’imposition 2024' })).toBeInTheDocument();
    expect(table.getByRole('link', { name: 'Correction d’un avis d’imposition — Avis d’imposition 2024' })).toBeInTheDocument();
    expect(await table.findByRole('link', { name: 'Suppression d’un avis d’imposition — Avis d’imposition 2024' })).toBeInTheDocument();
    expect(table.queryByText(/Élément inconnu/)).not.toBeInTheDocument();
  });

  it('shows a proposed tax notice as the tax page shows a recorded one, without its menu', async () => {
    const p = notice('tax.correct_tax_year');
    serve([p], { preview: {} });
    const { container } = await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByRole('heading', { level: 1, name: 'Correction d’un avis d’imposition' })).toBeInTheDocument();
    expect(screen.getByText('Avis d’imposition 2024', { selector: 'dd' })).toBeInTheDocument();
    expect(screen.queryByText('Nouvel élément')).not.toBeInTheDocument();
    const figures = within(await screen.findByRole('table', { name: 'Chiffres de l’avis 2024' }));
    expect(await figures.findByRole('columnheader', { name: /Déclarant 1\s*Camille/ })).toBeInTheDocument();
    expect(figures.getByRole('row', { name: /^Impôt net/ })).toHaveTextContent(/4\s200/);
    expect(figures.getByRole('row', { name: /^Salaires/ })).toHaveTextContent(/52\s000/);
    expect(screen.getByText('établi en 2025')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Actions de l’avis 2024' })).not.toBeInTheDocument();
    expect(container).not.toHaveTextContent(/00000000-|personId/);
    await expectNoAxeViolations(container);
  });

  it('shows a new notice without its issued year as issued the next year', async () => {
    const p = notice('tax.add_tax_year');
    delete (p.payload as { body: Record<string, unknown> }).body.issuedYear;
    serve([p], { preview: {} });
    await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByRole('table', { name: 'Chiffres de l’avis 2024' })).toBeInTheDocument();
    expect(screen.getByText('établi en 2025')).toBeInTheDocument();
  });

  it('shows what a correction changes, recorded then proposed, before the whole notice', async () => {
    const p = notice('tax.correct_tax_year');
    serve([p], { preview: { current: recorded } });
    const { container } = await renderApp(`/proposals/${p.id}`);

    const changes = within(await screen.findByRole('table', { name: 'Ce qui change' }));
    const netTax = changes.getByRole('row', { name: /^Impôt net/ });
    expect(within(netTax).getAllByRole('cell').map((c) => c.textContent)).toEqual([expect.stringMatching(/4\s100/), expect.stringMatching(/4\s200/)]);
    // Only what changes: the salaries are the same.
    expect(changes.getAllByRole('row')).toHaveLength(2);
    expect(screen.getByRole('table', { name: 'Chiffres de l’avis 2024' })).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('says so when a correction changes no figure', async () => {
    const p = notice('tax.correct_tax_year');
    serve([p], { preview: { current: { ...recorded, netTax: { amount: '4200.00', currency: 'EUR' } } } });
    await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByText('Aucun chiffre ne change.')).toBeInTheDocument();
  });

  it('shows the notice a deletion removes', async () => {
    const p = deletion();
    serve([p], { preview: { current: recorded } });
    await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByRole('note')).toHaveTextContent('Cet avis sera supprimé avec tous ses chiffres');
    const figures = within(screen.getByRole('table', { name: 'Chiffres de l’avis 2024' }));
    expect(figures.getByRole('row', { name: /^Impôt net/ })).toHaveTextContent(/4\s100/);
    expect(screen.queryByRole('button', { name: 'Actions de l’avis 2024' })).not.toBeInTheDocument();
  });

  it('never calls a deletion a new item', async () => {
    // The notice is gone or unreadable: no preview, nothing named.
    const p = deletion();
    serve([p], { preview: {} });
    await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByRole('heading', { level: 1, name: 'Suppression d’un avis d’imposition' })).toBeInTheDocument();
    expect(screen.queryByText('Nouvel élément')).not.toBeInTheDocument();
    expect(screen.queryByText('Élément inconnu')).not.toBeInTheDocument();
    expect(screen.getByText('Aucun champ.')).toBeInTheDocument();
  });

  it('« Recharger » after a conflict starts the decision again from the new version', async () => {
    let current = proposal({ kind: 'tax.delete_tax_year', targetItemId: null, targetName: null, subjectKey: null, payload: { id: '00000000-0000-7000-8000-0000000000f1' } });
    serve([current], { preview: {} });
    server.use(
      http.get('*/api/v1/core/proposals/:id', () => HttpResponse.json({ proposal: current, preview: {}, canDecide: true })),
      http.post('*/api/v1/core/proposals/:id/approve', () => {
        current = { ...current, version: current.version + 1 };
        return HttpResponse.json({ status: 409, code: 'core.proposal_conflict', title: 'x' }, { status: 409 });
      }),
    );
    const { user } = await renderApp(`/proposals/${current.id}`);

    await user.click(await screen.findByRole('button', { name: 'Approuver' }));
    await user.click(await screen.findByRole('button', { name: 'Recharger' }));

    await waitFor(() => expect(screen.queryByText('Cette proposition a changé.')).not.toBeInTheDocument());
    expect(screen.queryByRole('button', { name: 'Recharger' })).not.toBeInTheDocument();
  });
});
