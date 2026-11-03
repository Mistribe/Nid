import { screen, waitFor, within } from '@testing-library/react';
import { beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { HttpResponse, http } from 'msw/http';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { resetAnnouncement } from './announce';
import { proposal } from './testData';
import { serve } from './testServe';

/** Spec 4D3: a proposal that replays a web write has one read-only screen. */
describe('Une proposition d’action du web', () => {
  beforeEach(resetAnnouncement);
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

  it('names a tax notice by its year in the list, a deletion without one by its action alone', async () => {
    serve([notice('tax.add_tax_year'), notice('tax.correct_tax_year'), proposal({
      kind: 'tax.delete_tax_year', targetItemId: null, targetName: null, subjectKey: null, payload: { id: '00000000-0000-7000-8000-0000000000f1' },
    })]);
    await renderApp('/proposals');
    const table = within(await screen.findByRole('table', { name: 'Liste des propositions' }));
    expect(table.getByRole('link', { name: 'Nouvel avis d’imposition — Avis d’imposition 2024' })).toBeInTheDocument();
    expect(table.getByRole('link', { name: 'Correction d’un avis d’imposition — Avis d’imposition 2024' })).toBeInTheDocument();
    expect(table.getByRole('link', { name: 'Suppression d’un avis d’imposition' })).toBeInTheDocument();
    expect(table.queryByText(/Élément inconnu/)).not.toBeInTheDocument();
  });

  it('shows a tax correction in French, grouped by declarant, without its technical fields', async () => {
    const p = notice('tax.correct_tax_year');
    serve([p], { preview: {} });
    const { container } = await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByRole('heading', { level: 1, name: 'Correction d’un avis d’imposition' })).toBeInTheDocument();
    expect(screen.getByText('Avis d’imposition 2024', { selector: 'dd' })).toBeInTheDocument();
    expect(screen.queryByText('Nouvel élément')).not.toBeInTheDocument();
    expect(screen.getByRole('row', { name: /^Impôt net/ })).toHaveTextContent(/4\s200,00\s€/);
    const declarant = within(await screen.findByRole('region', { name: 'Déclarant 1 — Camille' }));
    expect(declarant.getByRole('row', { name: /^Salaires bruts/ })).toHaveTextContent(/52\s000,00\s€/);
    expect(declarant.getByRole('row', { name: /^Déduction des frais/ })).toHaveTextContent('Abattement forfaitaire de 10 %');
    const labels = screen.getAllByRole('rowheader').map((h) => h.textContent ?? '');
    expect(labels.filter((l) => /[[.\]]|^[a-z]+[A-Z]|^(id|version|rank|personId)$/.test(l))).toEqual([]);
    expect(screen.getByRole('region', { name: 'Champs proposés' })).not.toHaveTextContent(/00000000-|personId|Version|Rang/);
    await expectNoAxeViolations(container);
  });

  it('never calls a deletion a new item', async () => {
    const p = proposal({ kind: 'tax.delete_tax_year', targetItemId: null, targetName: null, subjectKey: null, payload: { id: '00000000-0000-7000-8000-0000000000f1' } });
    serve([p], { preview: {} });
    await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByRole('heading', { level: 1, name: 'Suppression d’un avis d’imposition' })).toBeInTheDocument();
    expect(screen.queryByText('Nouvel élément')).not.toBeInTheDocument();
    expect(screen.queryByText('Élément inconnu')).not.toBeInTheDocument();
    expect(screen.getByText('Aucun champ.')).toBeInTheDocument();
  });
});
