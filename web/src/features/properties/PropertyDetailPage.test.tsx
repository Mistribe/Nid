import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { LOAN } from '../loans/testData';
import type { PropertyDetail } from './api';
import { PROPERTY, propertyDetail } from './testData';

const CHF = '0192f0aa-0000-7000-8000-00000000d002';
const USD = '0192f0aa-0000-7000-8000-00000000d003';

function serve(detail: PropertyDetail = propertyDetail()) {
  server.use(http.get('*/api/v1/realestate/properties/:id', () => HttpResponse.json(detail)));
}

const heading = () => screen.findByRole('heading', { name: 'Maison de Nantes', level: 1 });
const figures = () => screen.getByRole('region', { name: 'Aujourd’hui' });
const figure = (term: string) => within(figures()).getByText(term, { selector: 'dt' }).nextElementSibling;
const withFigures = (over: Partial<PropertyDetail['figures']>) => propertyDetail({ figures: { ...propertyDetail().figures, ...over } });

describe('PropertyDetailPage', () => {
  // The lazy route chunk loaded cold inside the first test races its timeouts under load.
  beforeAll(async () => { await import('./PropertyDetailPage'); }, 30_000);

  it('shows the header and every figure of today as the server computed them, the real cost’s lines in its order', async () => {
    serve();
    const { container } = await renderApp(`/properties/${PROPERTY}`);

    // The header alone: the settings (Task 7) offer the same type among their options.
    const header = (await heading()).parentElement!;
    expect(header).toHaveTextContent('Résidence principale');
    expect(header).toHaveTextContent('Nantes');
    expect(within(header).getByText('Partagé')).toBeInTheDocument();
    expect(screen.queryByRole('list', { name: 'Avertissements' })).not.toBeInTheDocument();
    expect(screen.queryByText('Vous pouvez consulter ce bien, pas le modifier.')).not.toBeInTheDocument();
    expect(figure('Valeur')).toHaveTextContent('290 000,00 € au 1 juin 2026');
    expect(figure('Valeur')).not.toHaveTextContent('À réestimer');
    expect(figure('Prix de revient')).toHaveTextContent('281 000,00 €');
    expect(figure('Plus-value latente')).toHaveTextContent('+9 000,00 €');
    expect(figure('Capital restant dû des crédits')).toHaveTextContent('193 564,23 €');
    expect(figure('Valeur nette')).toHaveTextContent('96 435,77 €');
    expect(figure('Coût réel')).toHaveTextContent('288 823,35 €');
    expect(figure('Coût réel, loyers déduits')).toHaveTextContent('288 823,35 €');
    const lines = within(figures()).getByRole('heading', { name: 'Détail du coût réel' }).nextElementSibling!;
    expect([...lines.querySelectorAll('dt')].map((dt) => dt.textContent)).toEqual(
      ['Achat et frais', 'Intérêts des crédits', 'Assurance emprunteur', 'Frais de crédit', 'Travaux', 'Taxe foncière']);
    expect(figure('Achat et frais')).toHaveTextContent('269 000,00 €');
    expect(figure('Intérêts des crédits')).toHaveTextContent('6 323,35 €');
    expect(figure('Travaux')).toHaveTextContent('12 000,00 €');
    await expectNoAxeViolations(container);
  });

  it('reads an unknown value, latent gain and net value as « Inconnue », never 0', async () => {
    serve(withFigures({ value: null, valueOn: null, latentGain: null, netValue: null }));
    await renderApp(`/properties/${PROPERTY}`);

    await heading();
    expect(figure('Valeur')).toHaveTextContent('Inconnue');
    expect(figure('Plus-value latente')).toHaveTextContent('Inconnue');
    expect(figure('Valeur nette')).toHaveTextContent('Inconnue');
    expect(figure('Prix de revient')).toHaveTextContent('281 000,00 €');
  });

  it('signs a latent loss, marks a stale value « À réestimer » in words, and reads a line it does not know as its code', async () => {
    serve(withFigures({
      latentGain: '-12000.00', stale: true,
      realCostLines: [...propertyDetail().figures.realCostLines, { code: 'something_new', amount: '10.00' }],
    }));
    await renderApp(`/properties/${PROPERTY}`);

    await heading();
    expect(figure('Plus-value latente')).toHaveTextContent('-12 000,00 €');
    expect(figure('Valeur')).toHaveTextContent('290 000,00 € au 1 juin 2026 — À réestimer');
    expect(figure('something_new')).toHaveTextContent('10,00 €');
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
    expect(within(home!).getByRole('link', { name: 'Prêt maison' })).toHaveAttribute('href', `/loans/${LOAN}`);
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

  it('tells a reader without write access the page is read-only', async () => {
    serve(propertyDetail({ canWrite: false, visibility: 'shared' }));
    const { container } = await renderApp(`/properties/${PROPERTY}`);

    await heading();
    expect(screen.getByText('Vous pouvez consulter ce bien, pas le modifier.')).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('says an unknown or unreadable property is not found', async () => {
    server.use(http.get('*/api/v1/realestate/properties/:id', () => HttpResponse.json({ status: 404, code: null, title: 'Not found' }, { status: 404 })));
    await renderApp(`/properties/${PROPERTY}`);

    expect(await screen.findByText('Cet élément est introuvable.')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Retour aux biens' })).toHaveAttribute('href', '/properties');
  });
});
