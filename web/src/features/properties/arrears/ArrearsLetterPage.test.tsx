import { cleanup, screen, waitFor, within } from '@testing-library/react';
import { delay } from 'msw';
import { http, HttpResponse } from 'msw/http';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import type { PropertyArrears } from '../api';
import { expectLabelBefore } from '@/test/filters';
import { renderApp } from '@/test/renderApp';
import { expectTrailBack } from '@/test/trail';
import { server } from '@/test/server';
import { arrears, PROPERTY, propertyDetail } from '../testData';

const serve = (v: PropertyArrears) => server.use(http.get('*/api/v1/realestate/properties/:id/arrears', () => HttpResponse.json(v)));
const PAGE = `/properties/${PROPERTY}/arrears-letter`;
const long = (d: Date) => new Intl.DateTimeFormat('fr-FR', { day: 'numeric', month: 'long', year: 'numeric' }).format(d);
const inDays = (n: number) => { const d = new Date(); d.setDate(d.getDate() + n); return d; };

describe('arrears letter page', () => {
  beforeAll(async () => { await import('./ArrearsLetterPage'); }, 30_000);

  afterEach(() => { vi.restoreAllMocks(); });

  it('loading and error states show no inline back link: the bar leads back to the property, « Bien » until it is read', async () => {
    server.use(http.get('*/api/v1/realestate/properties/:id/arrears', async () => { await delay('infinite'); return HttpResponse.json({}); }));
    await renderApp(PAGE);
    expect(await screen.findByLabelText('Chargement du courrier')).toBeInTheDocument();
    await waitFor(() => expectTrailBack('Bien', `/properties/${PROPERTY}`));
    expectTrailBack('Biens', '/properties');
    cleanup();

    // Coming from the property, its name is already in the cache: no request names it.
    server.use(
      http.get('*/api/v1/realestate/properties/:id', () => HttpResponse.json(propertyDetail())),
      http.get('*/api/v1/realestate/properties/:id/arrears', () => HttpResponse.json({ status: 404, code: null, title: 'Not found' }, { status: 404 })),
    );
    const { router } = await renderApp(`/properties/${PROPERTY}`);
    await screen.findByRole('heading', { level: 1, name: 'Maison de Nantes' });
    await router.navigate({ to: '/properties/$id/arrears-letter', params: { id: PROPERTY } });
    expect(await screen.findByRole('heading', { level: 1, name: 'Page introuvable' })).toBeInTheDocument();
    await waitFor(() => expectTrailBack('Maison de Nantes', `/properties/${PROPERTY}`));
    expectTrailBack('Biens', '/properties');
  });

  it('writes the fixed letter: identity, the months of the letter, the total, the default deadline', async () => {
    serve(arrears());
    const { container } = await renderApp(PAGE);

    const doc = await screen.findByRole('article', { name: 'Relance amiable — loyers impayés' });
    expect(doc).toHaveTextContent('Madame, Monsieur,');
    expect(doc).toHaveTextContent('Sauf erreur de notre part, les sommes suivantes restent dues au titre du logement situé 12 rue des Lilas, 44000 Nantes :');
    expect(doc).toHaveTextContent('Jeanne Martin');
    expect(doc).toHaveTextContent('septembre 2026');
    expect(within(doc).getAllByRole('row')).toHaveLength(2);
    expect(doc).toHaveTextContent('Total restant dû : 950,00 €');
    expect(doc).toHaveTextContent(`au plus tard le ${long(inDays(15))}. Si votre règlement a été effectué entre-temps, nous vous prions de ne pas tenir compte de ce courrier.`);
    expect(doc).toHaveTextContent(/Fait le /);
    expect(within(doc).getByText('Paul Durand', { selector: 'p' })).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('addresses the tenant at the property and closes with the formula before the signature', async () => {
    serve(arrears());
    await renderApp(PAGE);

    const doc = await screen.findByRole('article');
    const addressee = within(doc).getByText(/Jeanne Martin/, { selector: 'p' });
    expect(addressee).toHaveTextContent('Jeanne Martin 12 rue des Lilas, 44000 Nantes');
    const closing = within(doc).getByText('Veuillez agréer, Madame, Monsieur, l’expression de nos salutations distinguées.');
    const signature = within(doc).getAllByText('Paul Durand', { selector: 'p' }).at(-1)!;
    expect(closing.compareDocumentPosition(signature) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('notes a letter to a former tenant, outside the document', async () => {
    serve(arrears({ letter: { ...arrears().letter!, formerTenant: true } }));
    await renderApp(PAGE);

    const doc = await screen.findByRole('article');
    const note = screen.getByText('Ce courrier est adressé à un ancien locataire.');
    expect(doc).not.toContainElement(note);
    expect(note.closest('.print\\:hidden')).not.toBeNull();
  });

  it('says nothing of a former tenant to the tenant in place', async () => {
    serve(arrears());
    await renderApp(PAGE);
    await screen.findByRole('article');
    expect(screen.queryByText(/ancien locataire/)).not.toBeInTheDocument();
  });

  it('puts the delay in the filter row, the label before it, under a trail through the properties', async () => {
    serve(arrears());
    await renderApp(PAGE);

    const days = await screen.findByRole('radiogroup', { name: 'Délai de paiement' });
    expectLabelBefore(within(screen.getByRole('group', { name: 'Filtres' })).getByText('Délai de paiement'), days);
    const trail = screen.getByRole('navigation', { name: 'Fil d’Ariane' });
    expect(within(trail).getByRole('link', { name: 'Biens' })).toHaveAttribute('href', '/properties');
    expect(within(trail).getByRole('link', { name: arrears().propertyName })).toHaveAttribute('href', `/properties/${PROPERTY}`);
  });

  it('moves the deadline and the URL with the delay picked', async () => {
    serve(arrears());
    const { user, router } = await renderApp(PAGE);
    await screen.findByRole('article');

    await user.click(screen.getByRole('radio', { name: '30 jours' }));

    await waitFor(() => expect(router.state.location.search).toMatchObject({ days: 30 }));
    expect(await screen.findByText(new RegExp(`au plus tard le ${long(inDays(30))}`))).toBeInTheDocument();
  });

  it('reads the delay from the URL', async () => {
    serve(arrears());
    await renderApp(`${PAGE}?days=8`);
    expect(await screen.findByText(new RegExp(`au plus tard le ${long(inDays(8))}`))).toBeInTheDocument();
    expect(screen.getByRole('radio', { name: '8 jours' })).toBeChecked();
  });

  it('falls back to 15 days on a delay out of range', async () => {
    serve(arrears());
    const { router } = await renderApp(`${PAGE}?days=90`);
    expect(await screen.findByText(new RegExp(`au plus tard le ${long(inDays(15))}`))).toBeInTheDocument();
    expect(router.state.location.search).not.toMatchObject({ days: 90 });
  });

  it('marks missing identity with « [à compléter] », warns and links to the lease and the settings', async () => {
    serve(arrears({ letter: { ...arrears().letter!, tenantNames: null, postalAddress: null, missing: ['postal_address', 'tenant_names'] } }));
    await renderApp(PAGE);

    const doc = await screen.findByRole('article');
    expect(doc).toHaveTextContent('[à compléter]');
    const warning = screen.getByRole('alert');
    expect(warning).toHaveTextContent('adresse du logement');
    expect(warning).toHaveTextContent('nom du locataire');
    expect(within(warning).getByRole('link', { name: 'Compléter le bail' })).toHaveAttribute('href', `/properties/${PROPERTY}?tab=rental`);
    expect(within(warning).getByRole('link', { name: 'Compléter le bien' })).toHaveAttribute('href', `/properties/${PROPERTY}?tab=settings`);
  });

  it('notes an earlier tenant left out of the letter, outside the document', async () => {
    serve(arrears({ letter: { ...arrears().letter!, leavesOutEarlierTenant: true } }));
    await renderApp(PAGE);

    const doc = await screen.findByRole('article');
    const note = screen.getByText(/précédent locataire/);
    expect(doc).not.toContainElement(note);
    expect(note.closest('.print\\:hidden')).not.toBeNull();
  });

  it('prints from the toolbar, which stays out of the print', async () => {
    serve(arrears());
    const print = vi.spyOn(window, 'print').mockImplementation(() => undefined);
    const { user } = await renderApp(PAGE);

    const button = await screen.findByRole('button', { name: 'Imprimer' });
    expect(button.closest('.print\\:hidden')).not.toBeNull();
    expect(screen.getByRole('radiogroup', { name: 'Délai de paiement' }).closest('.print\\:hidden')).not.toBeNull();
    await user.click(button);
    expect(print).toHaveBeenCalledOnce();
  });

  it('shows « Aucun mois en retard » and no document nor print when nothing is overdue', async () => {
    serve(arrears({ letter: null, overdueTotal: '0.00' }));
    await renderApp(PAGE);

    expect(await screen.findByText('Aucun mois en retard')).toBeInTheDocument();
    expect(screen.queryByRole('article')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Imprimer' })).not.toBeInTheDocument();
  });
});
