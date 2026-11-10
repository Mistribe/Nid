import { link, list, summary } from '../documents/testData';
import { cleanup, screen, waitFor, within } from '@testing-library/react';
import { delay } from 'msw';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { expectTrailBack } from '@/test/trail';
import { pick } from '@/test/controls';
import { server } from '@/test/server';
import { toasts } from '@/test/toasts';
import { LOAN } from '../loans/testData';
import type { PropertyDetail } from '@/api/properties';
import { PROPERTY, propertyDetail, rentedFigures } from './testData';

const CHF = '0192f0aa-0000-7000-8000-00000000d002';
const USD = '0192f0aa-0000-7000-8000-00000000d003';

function serve(detail: PropertyDetail = propertyDetail()) {
  server.use(http.get('*/api/v1/realestate/properties/:id', () => HttpResponse.json(detail)));
}

const heading = () => screen.findByRole('heading', { name: 'Maison de Nantes', level: 1 });
const figures = () => screen.getByRole('region', { name: 'Aujourd’hui' });
const figure = (term: string) => within(figures()).getByText(term, { selector: 'dt' }).nextElementSibling;
// The overview's detail: its own dl, under the tab.
const detail = (term: string) => within(screen.getByRole('region', { name: 'Coût réel' })).getByText(term, { selector: 'dt' }).nextElementSibling;
const withFigures = (over: Partial<PropertyDetail['figures']>) => propertyDetail({ figures: { ...propertyDetail().figures, ...over } });

describe('PropertyDetailPage', () => {
  // The lazy route chunk loaded cold inside the first test races its timeouts under load.
  beforeAll(async () => { await import('./PropertyDetailPage'); }, 30_000);

  it('loading and error states show no inline back link: the bar leads back to the list', async () => {
    server.use(http.get('*/api/v1/realestate/properties/:id', async () => { await delay('infinite'); return HttpResponse.json({}); }));
    await renderApp(`/properties/${PROPERTY}`);
    expect(await screen.findByLabelText('Chargement du bien')).toBeInTheDocument();
    await waitFor(() => expectTrailBack('Biens', '/properties'));
    cleanup();

    server.use(http.get('*/api/v1/realestate/properties/:id', () => HttpResponse.json({ status: 404, code: null, title: 'Not found' }, { status: 404 })));
    await renderApp(`/properties/${PROPERTY}`);
    expect(await screen.findByRole('heading', { level: 1, name: 'Page introuvable' })).toBeInTheDocument();
    await waitFor(() => expectTrailBack('Biens', '/properties'));
  });

  it('overview shows value, net value, latent gain and real cost inline, the real cost’s lines in its order', async () => {
    serve();
    const { container } = await renderApp(`/properties/${PROPERTY}`);

    // The header alone: the settings tab offers the same type among their options.
    const header = (await heading()).closest('header')!;
    expect(header).toHaveTextContent('Résidence principale');
    expect(header).toHaveTextContent('Nantes');
    expect(within(header).getByText('Partagé')).toBeInTheDocument();
    expect(within(within(document.querySelector<HTMLElement>('[data-pane="toolbar"]')!).getByRole('navigation', { name: 'Fil d’Ariane' })).getByRole('link', { name: 'Biens' })).toHaveAttribute('href', '/properties');
    expect(screen.queryByRole('list', { name: 'Avertissements' })).not.toBeInTheDocument();
    expect(screen.queryByText('Vous pouvez consulter ce bien, pas le modifier.')).not.toBeInTheDocument();
    expect(figure('Valeur')).toHaveTextContent(/^290\s000,00\s€$/);
    expect(figure('Valeur')!.nextElementSibling).toHaveTextContent('au 1 juin 2026');
    expect(figure('Valeur')!.nextElementSibling).not.toHaveTextContent('À réestimer');
    expect(figure('Valeur nette')).toHaveTextContent('96 435,77 €');
    expect(figure('Plus-value latente')).toHaveTextContent('+9 000,00 €');
    expect(figure('Coût réel')).toHaveTextContent('288 823,35 €');
    expect(within(figures()).queryByText('Rendement net avant impôt')).not.toBeInTheDocument();
    expect(detail('Prix de revient')).toHaveTextContent('281 000,00 €');
    expect(detail('Capital restant dû des crédits')).toHaveTextContent('193 564,23 €');
    expect(detail('Coût réel, loyers déduits')).toHaveTextContent('288 823,35 €');
    const lines = within(screen.getByRole('region', { name: 'Coût réel' })).getByRole('heading', { name: 'Détail du coût réel' }).nextElementSibling!;
    expect([...lines.querySelectorAll('dt')].map((dt) => dt.textContent)).toEqual(
      ['Achat et frais', 'Intérêts des crédits', 'Assurance emprunteur', 'Frais de crédit', 'Travaux', 'Taxe foncière']);
    expect(detail('Achat et frais')).toHaveTextContent('269 000,00 €');
    expect(detail('Intérêts des crédits')).toHaveTextContent('6 323,35 €');
    expect(detail('Travaux')).toHaveTextContent('12 000,00 €');
    await expectNoAxeViolations(container);
  });

  it('overview shows rent and yield only when rented', async () => {
    serve(propertyDetail({ figures: rentedFigures() }));
    const { container } = await renderApp(`/properties/${PROPERTY}`);

    await heading();
    expect(figure('Loyers attendus sur 12 mois')).toHaveTextContent('10 800,00 €');
    expect(figure('Vacance sur 12 mois')).toHaveTextContent('0 %');
    expect(figure('Vacance sur 12 mois')!.nextElementSibling).toHaveTextContent('0 jour sans bail');
    expect(figure('Rendement net avant impôt')).toHaveTextContent('3,31 %');
    await expectNoAxeViolations(container);
  });

  it('reads an unknown value, latent gain and net value as « Inconnue », never 0', async () => {
    serve(withFigures({ value: null, valueOn: null, latentGain: null, netValue: null }));
    await renderApp(`/properties/${PROPERTY}`);

    await heading();
    expect(figure('Valeur')).toHaveTextContent('Inconnue');
    expect(figure('Plus-value latente')).toHaveTextContent('Inconnue');
    expect(figure('Valeur nette')).toHaveTextContent('Inconnue');
    expect(detail('Prix de revient')).toHaveTextContent('281 000,00 €');
  });

  it('says since when a negative net value is so and when it turns positive, the value held flat', async () => {
    serve(withFigures({ netValue: '-58400.00', netValueTurn: { negativeSince: '2025-06-01', positiveOn: '2031-03-15' } }));
    await renderApp(`/properties/${PROPERTY}`);

    await heading();
    expect(figure('Valeur nette')).toHaveTextContent('Valeur nette négative depuis le 1 juin 2025 · positive vers mars 2031, à valeur constante');
    cleanup();

    serve(withFigures({ netValue: '-58400.00', netValueTurn: { negativeSince: '2025-06-01', positiveOn: null } }));
    await renderApp(`/properties/${PROPERTY}`);
    await heading();
    expect(figure('Valeur nette')).toHaveTextContent('positive au-delà de la fin des crédits, à valeur constante');
  });

  it('signs a latent loss, marks a stale value « À réestimer » in words, and reads a line it does not know as its code', async () => {
    serve(withFigures({
      latentGain: '-12000.00', stale: true,
      realCostLines: [...propertyDetail().figures.realCostLines, { code: 'something_new', amount: '10.00' }],
    }));
    await renderApp(`/properties/${PROPERTY}`);

    await heading();
    expect(figure('Plus-value latente')).toHaveTextContent('-12 000,00 €');
    expect(figure('Valeur')!.nextElementSibling).toHaveTextContent('au 1 juin 2026 — À réestimer');
    expect(detail('something_new')).toHaveTextContent('10,00 €');
  });

  it('lists each warning in French, a loan named from the linked loans, an unknown code as itself', async () => {
    serve(propertyDetail({
      loans: [...propertyDetail().loans, { id: CHF, name: 'Prêt CHF', currency: 'CHF', balance: '50000.00', balanceConverted: null }],
      warnings: [
        { code: 'no_valuation', loanId: null }, { code: 'valuation_stale', loanId: null },
        { code: 'loan_other_currency', loanId: CHF }, { code: 'fx_missing', loanId: CHF },
        { code: 'visibility_mismatch', loanId: LOAN }, { code: 'rental_without_tenancy', loanId: null }, { code: 'something_new', loanId: null },
      ],
    }));
    const { container } = await renderApp(`/properties/${PROPERTY}`);

    const items = within(await screen.findByRole('list', { name: 'Avertissements' })).getAllByRole('listitem');
    const expected = [
      'Pas encore d’estimation : la valeur et la valeur nette sont inconnues',
      'Estimation à refaire (la dernière date du 1 juin 2026)',
      'Le crédit Prêt CHF est converti en EUR au taux du jour',
      'Taux de change manquant pour le crédit Prêt CHF : il n’est pas déduit',
      'Le crédit privé Prêt maison ne sera pas déduit du patrimoine du foyer',
      'Bien locatif sans bail ni date de début de location',
      'something_new',
    ];
    expect(items).toHaveLength(expected.length);
    expected.forEach((text, i) => expect(items[i]).toHaveTextContent(text));
    await expectNoAxeViolations(container);
  });

  it('lists the readable linked loans: a link, the balance, converted in another currency, the missing rate said', async () => {
    serve(propertyDetail({
      loans: [
        { id: LOAN, name: 'Prêt maison', currency: 'EUR', balance: '193564.23', balanceConverted: '193564.23' },
        { id: CHF, name: 'Prêt CHF', currency: 'CHF', balance: '50000.00', balanceConverted: '53210.40' },
        { id: USD, name: 'Prêt USD', currency: 'USD', balance: '10000.00', balanceConverted: null },
      ],
    }));
    const { container } = await renderApp(`/properties/${PROPERTY}`);

    const section = await screen.findByRole('region', { name: 'Crédits liés' });
    const [home, chf, usd] = within(section).getAllByRole('listitem');
    // A grouped list: the row is one link, its name first.
    expect(within(home!).getByRole('link', { name: /^Prêt maison / })).toHaveAttribute('href', `/loans/${LOAN}`);
    expect(home).toHaveTextContent('Capital restant dû 193 564,23 €');
    expect(home).not.toHaveTextContent('soit');
    expect(chf).toHaveTextContent('Capital restant dû 50 000,00 CHF');
    expect(chf).toHaveTextContent('soit 53 210,40 €');
    expect(usd).toHaveTextContent('taux manquant');
    await expectNoAxeViolations(container);
  });

  it('says when no loan is linked', async () => {
    serve(propertyDetail({ loans: [] }));
    await renderApp(`/properties/${PROPERTY}`);

    expect(await within(await screen.findByRole('region', { name: 'Crédits liés' })).findByText('Aucun crédit lié')).toBeInTheDocument();
  });

  it('orders the tabs, « Location » only for a rental or a property with a tenancy', async () => {
    serve(propertyDetail());
    await renderApp(`/properties/${PROPERTY}?tab=rental`);

    await heading();
    const names = () => screen.getAllByRole('tab').map((x) => x.textContent);
    expect(names()).toEqual(['Vue d’ensemble', 'Dépenses', 'Estimations', 'Documents', 'Réglages']);
    expect(screen.getByRole('tab', { name: 'Vue d’ensemble' })).toHaveAttribute('aria-selected', 'true');
  });

  it('shows « Location » after the overview for a rental', async () => {
    serve(propertyDetail({ type: 'rental' }));
    await renderApp(`/properties/${PROPERTY}`);

    await heading();
    expect(screen.getAllByRole('tab').map((x) => x.textContent))
      .toEqual(['Vue d’ensemble', 'Location', 'Rentabilité', 'Dépenses', 'Estimations', 'Documents', 'Réglages']);
  });

  it('a reader sees no write action, nor the settings tab, and is told the page is read-only', async () => {
    serve(propertyDetail({ type: 'rental', canWrite: false, visibility: 'shared' }));
    const { container, user } = await renderApp(`/properties/${PROPERTY}`);

    await heading();
    expect(screen.getByText('Vous pouvez consulter ce bien, pas le modifier.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Ajouter une estimation' })).not.toBeInTheDocument();
    expect(screen.queryByRole('tab', { name: 'Réglages' })).not.toBeInTheDocument();
    for (const tab of ['Location', 'Estimations', 'Dépenses']) {
      await user.click(screen.getByRole('tab', { name: tab }));
      const panel = await screen.findByRole('tabpanel');
      expect(within(panel).queryByRole('button')).not.toBeInTheDocument();
    }
    await expectNoAxeViolations(container);
  });

  it('adds an estimation in a dialog, and says a date already estimated will be replaced', async () => {
    let current = propertyDetail();
    const sent: unknown[] = [];
    server.use(
      http.get('*/api/v1/realestate/properties/:id', () => HttpResponse.json(current)),
      http.post('*/api/v1/realestate/properties/:id/valuations', async ({ request }) => {
        const body = (await request.json()) as { asOf: string; amount: string };
        sent.push(body);
        current = { ...current, valuations: [...current.valuations.filter((v) => v.asOf !== body.asOf), { id: 'v3', asOf: body.asOf, amount: `${body.amount}.00`, note: null }] };
        return HttpResponse.json({ id: 'v3' }, { status: 201 });
      }),
    );
    const { user, container } = await renderApp(`/properties/${PROPERTY}`);

    const open = await screen.findByRole('button', { name: 'Ajouter une estimation' });
    await user.click(open);
    const dialog = within(await screen.findByRole('dialog', { name: 'Nouvelle estimation' }));
    expect(dialog.queryByText('Une estimation existe déjà à cette date : elle sera remplacée.')).not.toBeInTheDocument();
    await user.clear(dialog.getByLabelText('Date'));
    await user.type(dialog.getByLabelText('Date'), '01/06/2026');
    expect(dialog.getByText('Une estimation existe déjà à cette date : elle sera remplacée.')).toBeInTheDocument();
    await user.type(dialog.getByLabelText('Valeur'), '300000');
    await expectNoAxeViolations(container);
    await user.click(dialog.getByRole('button', { name: 'Ajouter' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    await waitFor(() => expect(toasts()).toHaveTextContent('Estimation remplacée.'));
    expect(sent).toEqual([{ asOf: '2026-06-01', amount: '300000' }]);
    await waitFor(() => expect(figure('Valeur')).toHaveTextContent('290 000,00 €'));
    expect(open).toHaveFocus();
  });

  it('adds a lease, a flow and a planned charge in dialogs', async () => {
    const current = propertyDetail({ type: 'rental' });
    const sent: { path: string; body: Record<string, unknown> }[] = [];
    const record = (path: string) => async ({ request }: { request: Request }) => {
      sent.push({ path, body: (await request.json()) as Record<string, unknown> });
      return HttpResponse.json({ id: 'new' }, { status: 201 });
    };
    server.use(
      http.get('*/api/v1/realestate/properties/:id', () => HttpResponse.json(current)),
      http.post('*/api/v1/realestate/properties/:id/tenancies', record('tenancies')),
      http.post('*/api/v1/realestate/properties/:id/flows', record('flows')),
      http.post('*/api/v1/realestate/properties/:id/recurring-costs', record('recurring-costs')),
    );
    const { user } = await renderApp(`/properties/${PROPERTY}`);
    await heading();

    await user.click(screen.getByRole('tab', { name: 'Location' }));
    await user.click(await screen.findByRole('button', { name: 'Ajouter un bail' }));
    let dialog = within(await screen.findByRole('dialog', { name: 'Nouveau bail' }));
    await user.type(dialog.getByLabelText('Libellé'), 'T2');
    await user.clear(dialog.getByLabelText('Début'));
    await user.type(dialog.getByLabelText('Début'), '01/01/2026');
    await user.type(dialog.getByLabelText('Loyer mensuel'), '900');
    await user.click(dialog.getByRole('button', { name: 'Ajouter' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(await screen.findByText('Bail ajouté.')).toBeInTheDocument();
    expect(sent[0]).toMatchObject({ path: 'tenancies', body: { label: 'T2', startsOn: '2026-01-01', rentMonthly: '900', chargesMonthly: '0' } });

    await user.click(screen.getByRole('tab', { name: 'Dépenses' }));
    await user.click(await screen.findByRole('button', { name: 'Ajouter une dépense ou un revenu' }));
    dialog = within(await screen.findByRole('dialog', { name: 'Nouvelle dépense ou nouveau revenu' }));
    await pick(user, 'Type', 'Revenu');
    await pick(user, 'Catégorie', 'Loyer');
    await user.type(dialog.getByLabelText('Montant'), '900');
    await user.click(dialog.getByRole('button', { name: 'Ajouter' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(sent[1]).toMatchObject({ path: 'flows', body: { kind: 'income', category: 'rent', amount: '900' } });

    await user.click(await screen.findByRole('button', { name: 'Ajouter une charge prévue' }));
    dialog = within(await screen.findByRole('dialog', { name: 'Nouvelle charge prévue' }));
    await user.type(dialog.getByLabelText('Montant'), '1200');
    await user.click(dialog.getByRole('button', { name: 'Ajouter' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(sent[2]).toMatchObject({ path: 'recurring-costs', body: { category: 'property_tax', amount: '1200', every: { count: 1, unit: 'year' } } });
  });

  it('says an unknown or unreadable property is not found', async () => {
    server.use(http.get('*/api/v1/realestate/properties/:id', () => HttpResponse.json({ status: 404, code: null, title: 'Not found' }, { status: 404 })));
    await renderApp(`/properties/${PROPERTY}`);

    expect(await screen.findByRole('heading', { level: 1, name: 'Page introuvable' })).toBeInTheDocument();
  });

  it('has a documents tab before the settings, listing the documents anchored on the property', async () => {
    serve();
    server.use(http.get('*/api/v1/documents', () => HttpResponse.json(list([summary({ title: 'Pièce du dossier', links: [link({ targetId: PROPERTY, anchorItemId: PROPERTY })] })]))));
    const { user } = await renderApp(`/properties/${PROPERTY}`);

    await screen.findAllByRole('tab');
    const names = screen.getAllByRole('tab').map((tab) => tab.textContent);
    expect(names.indexOf('Documents')).toBe(names.indexOf('Réglages') - 1);
    await user.click(screen.getByRole('tab', { name: 'Documents' }));
    expect((await screen.findAllByText('Pièce du dossier')).length).toBeGreaterThan(0);
    expect(screen.getByRole('button', { name: 'Déposer' })).toBeInTheDocument();
  });

  it('shows a reader the documents without « Déposer »', async () => {
    serve(propertyDetail({ canWrite: false, visibility: 'shared' }));
    const { user } = await renderApp(`/properties/${PROPERTY}`);

    await heading();
    await user.click(screen.getByRole('tab', { name: 'Documents' }));
    expect(await screen.findByText('Aucun document.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Déposer' })).not.toBeInTheDocument();
  });
  describe('deletion', () => {
    const preview = (over: object = {}) => ({ name: 'Maison de Nantes', version: 4, imported: false, removes: [], blockers: [], ...over });
    const openMenu = async (body: PropertyDetail) => {
      serve(body);
      const { user, router } = await renderApp(`/properties/${PROPERTY}`);
      await heading();
      await user.click(screen.getByRole('button', { name: 'Autres actions' }));
      return { user, router };
    };

    it('Supprimer… is the last menu item', async () => {
      await openMenu(propertyDetail());
      expect(screen.getAllByRole('menuitem').at(-1)).toHaveTextContent('Supprimer…');
    });

    it('a reader has no Supprimer…', async () => {
      serve(propertyDetail({ canWrite: false, visibility: 'private' }));
      await renderApp(`/properties/${PROPERTY}`);
      await heading();
      expect(screen.queryByRole('button', { name: 'Autres actions' })).not.toBeInTheDocument();
    });

    it('deletes after the name is typed, then leaves for the list, with no close hint', async () => {
      server.use(
        http.get('*/api/v1/realestate/properties/:id/deletion', () => HttpResponse.json(preview({ removes: [{ key: 'tenancies', count: 1 }] }))),
        http.delete('*/api/v1/realestate/properties/:id', () => new HttpResponse(null, { status: 204 })),
      );
      const { user, router } = await openMenu(propertyDetail());
      await user.click(await screen.findByRole('menuitem', { name: 'Supprimer…' }));
      const dialog = await screen.findByRole('dialog', { name: 'Supprimer Maison de Nantes' });
      expect(await within(dialog).findByText(/1 bail/)).toBeInTheDocument();
      expect(dialog).not.toHaveTextContent('utilisez plutôt');
      await user.type(await within(dialog).findByLabelText(/Tapez/), 'Maison de Nantes');
      await user.click(within(dialog).getByRole('button', { name: 'Supprimer définitivement' }));
      await waitFor(() => expect(router.state.location.pathname).toBe('/properties'));
    });

    it('a loan blocks: its name and the way out are shown, with no delete button', async () => {
      server.use(http.get('*/api/v1/realestate/properties/:id/deletion', () => HttpResponse.json(preview({
        blockers: [{ key: 'loan', count: 1, items: [{ id: 'l1', name: 'Prêt maison' }] }],
      }))));
      const { user } = await openMenu(propertyDetail());
      await user.click(await screen.findByRole('menuitem', { name: 'Supprimer…' }));
      const dialog = await screen.findByRole('dialog', { name: 'Supprimer Maison de Nantes' });
      expect(await within(dialog).findByText(/Prêt maison/)).toBeInTheDocument();
      expect(within(dialog).getByText('Rattachez ces crédits à un autre bien, ou supprimez-les s’ils ont eux aussi été créés par erreur.')).toBeInTheDocument();
      expect(within(dialog).queryByRole('button', { name: 'Supprimer définitivement' })).not.toBeInTheDocument();
    });
  });
});
