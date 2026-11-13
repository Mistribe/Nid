import { screen, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import i18n from '@/app/i18n';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { list } from '../documents/testData';
import { childcareYearCsv } from './childcareYearCsv';
import { monthTitle } from './statementText';
import { ARRANGEMENT_ID, detail, statements, yearSummary } from './testData';

const url = `/childcare/${ARRANGEMENT_ID}?tab=summary`;

function serve(answer = yearSummary()) {
  const asked: (string | null)[] = [];
  server.use(
    http.get('*/api/v1/childcare/:id', () => HttpResponse.json(detail())),
    http.get('*/api/v1/childcare/:id/statements', () => HttpResponse.json(statements())),
    http.get('*/api/v1/documents', () => HttpResponse.json(list([]))),
    http.get('*/api/v1/childcare/:id/summary', ({ request }) => {
      const year = new URL(request.url).searchParams.get('year');
      asked.push(year);
      return HttpResponse.json(year ? { ...answer, year: Number(year), notValidated: [] } : answer);
    }),
  );
  return asked;
}

describe('YearSummary', () => {
  beforeAll(async () => { await import('./ChildcareDetailPage'); }, 30_000);

  it('shows the server’s year, its figures, the box and the months left out', async () => {
    const asked = serve();
    await renderApp(url);

    expect(await screen.findByRole('heading', { name: 'Récapitulatif 2025' })).toBeInTheDocument();
    expect(asked[0]).toBeNull(); // the year just ended is the server's default
    const lines = screen.getByRole('region', { name: 'Récapitulatif 2025' });
    expect(lines).toHaveTextContent('Salaire net versé');
    expect(lines).toHaveTextContent('1 879,11 €');
    expect(lines).toHaveTextContent('Cotisations socialesPrises en charge par la CMG');
    expect(lines).toHaveTextContent('361,50 €');
    expect(lines).toHaveTextContent('211,20 €');
    expect(lines).toHaveTextContent('172,80 €');
    expect(lines).toHaveTextContent('Reste à charge retenu pour le crédit d’impôt');
    expect(lines).toHaveTextContent('1 728,81 €');
    expect(lines).toHaveTextContent('case 7GA');
    expect(screen.getByText('Mois non validé, hors du récapitulatif : décembre 2025.')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Voir l’estimation d’impôt' })).toHaveAttribute('href', expect.stringMatching(/^\/tax\?year=.*2025/));
  });

  it('shows the contributions left to the household as an amount', async () => {
    serve(yearSummary({ contributions: { amount: '40.00', currency: 'EUR' } }));
    await renderApp(url);
    const lines = await screen.findByRole('region', { name: 'Récapitulatif 2025' });
    expect(lines).toHaveTextContent('Cotisations sociales · À la charge du foyer');
    expect(lines).toHaveTextContent('40,00 €');
  });

  it('shows the exemption on extra hours after the pay', async () => {
    serve(yearSummary({ exemption: { amount: '17.56', currency: 'EUR' } }));
    await renderApp(url);
    const lines = await screen.findByRole('region', { name: 'Récapitulatif 2025' });
    expect(lines).toHaveTextContent(/Salaire net versé.*Exonération des heures en plus.*17,56 €.*Cotisations sociales/);
  });

  it('asks the year picked', async () => {
    const asked = serve();
    const { user } = await renderApp(url);
    await user.click(await screen.findByRole('radio', { name: '2026' }));
    await waitFor(() => expect(asked).toContain('2026'));
    expect(await screen.findByRole('heading', { name: 'Récapitulatif 2026' })).toBeInTheDocument();
  });

  it('says when no month of the year is validated, and has no box past the third child', async () => {
    serve(yearSummary({ validatedMonths: [], notValidated: [], box: null }));
    await renderApp(url);
    expect(await screen.findByText('Aucun mois validé en 2025.')).toBeInTheDocument();
    expect(screen.getByText(/Plus de trois enfants gardés/)).toBeInTheDocument();
  });

  it('exports the lines as the server sent them', async () => {
    await import('@/locales/childcare');
    expect(childcareYearCsv(yearSummary(), i18n.t, monthTitle)).toBe([
      '﻿Ligne;Case;Montant',
      'Salaire net versé;;1879,11',
      'Cotisations sociales;;Prises en charge',
      'CMG perçu;;361,50',
      'Indemnités d’entretien;;211,20',
      'Indemnités de repas;;172,80',
      'Reste à charge retenu pour le crédit d’impôt;7GA;1728,81',
      'Mois non validés;;décembre 2025',
      '',
    ].join('\r\n'));
  });
});
