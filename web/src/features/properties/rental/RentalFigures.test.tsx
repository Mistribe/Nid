import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { PropertyDetail } from '../api';
import { PROPERTY, propertyDetail, rentedFigures } from '../testData';

function serve(detail: PropertyDetail) {
  server.use(http.get('*/api/v1/realestate/properties/:id', () => HttpResponse.json(detail)));
}

const heading = () => screen.findByRole('heading', { name: 'Maison de Nantes', level: 1 });
const region = () => screen.getByRole('region', { name: 'Location' });
const figure = (term: string) => within(region()).getByText(term, { selector: 'dt' }).nextElementSibling;

describe('rental figures', () => {
  // The lazy route chunk loaded cold inside the first test races its timeouts under load.
  beforeAll(async () => { await import('../PropertyDetailPage'); }, 30_000);

  it('shows the rental start and the last 12 months as the server computed them, its rates in %', async () => {
    serve(propertyDetail({ figures: rentedFigures() }));
    const { container } = await renderApp(`/properties/${PROPERTY}`);

    await heading();
    expect(figure('Début de location')).toHaveTextContent('1 janvier 2021');
    expect(figure('Loyers attendus sur 12 mois')).toHaveTextContent('10 800,00 €');
    expect(figure('Loyers perçus sur 12 mois')).toHaveTextContent('10 800,00 €');
    expect(figure('Impayés sur 12 mois')).toHaveTextContent('0,00 €');
    expect(figure('Impayés sur 12 mois')).not.toHaveTextContent('plus perçu');
    expect(figure('Vacance sur 12 mois')).toHaveTextContent('0 jour (0 %)');
    expect(figure('Rendement brut')).toHaveTextContent('3,84 %');
    expect(figure('Rendement net avant impôt')).toHaveTextContent('3,31 %');
    expect(within(region()).queryByText('Ce bien n’est pas loué.')).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('signs arrears, reads a negative one as more received than expected, and says yields of a short rental are not significant', async () => {
    serve(propertyDetail({ figures: rentedFigures({
      rentedSince: '2026-03-01', expectedRent: '6300.00', rentReceived: '7200.00', arrears: '-900.00', vacantDays: 30,
      vacancyRate: '0.0822', grossYield: null, netYieldBeforeTax: null,
    }) }));
    await renderApp(`/properties/${PROPERTY}`);

    await heading();
    expect(figure('Impayés sur 12 mois')).toHaveTextContent('-900,00 € (plus perçu que prévu)');
    expect(figure('Vacance sur 12 mois')).toHaveTextContent('30 jours (8,22 %)');
    expect(figure('Rendement brut')).toHaveTextContent('Non significatif (moins de 12 mois de location)');
    expect(figure('Rendement net avant impôt')).toHaveTextContent('Non significatif (moins de 12 mois de location)');
  });

  it('signs arrears still owed', async () => {
    serve(propertyDetail({ figures: rentedFigures({ rentReceived: '9900.00', arrears: '900.00' }) }));
    await renderApp(`/properties/${PROPERTY}`);

    await heading();
    expect(figure('Impayés sur 12 mois')).toHaveTextContent('+900,00 €');
  });

  it('reads a rental start after today as an unknown vacancy and yields not significant, never 0 %', async () => {
    serve(propertyDetail({ figures: rentedFigures({
      rentedSince: '2099-01-01', expectedRent: '0.00', rentReceived: '0.00', arrears: '0.00', vacantDays: null, vacancyRate: null,
      grossYield: null, netYieldBeforeTax: null,
    }) }));
    await renderApp(`/properties/${PROPERTY}`);

    await heading();
    expect(figure('Début de location')).toHaveTextContent('1 janvier 2099');
    expect(figure('Vacance sur 12 mois')).toHaveTextContent('Inconnue');
    expect(figure('Rendement brut')).toHaveTextContent('Non significatif (moins de 12 mois de location)');
    expect(within(region()).queryByText(/0 %/)).not.toBeInTheDocument();
  });

  it('says a property without a rental start is not rented, with no figure', async () => {
    serve(propertyDetail());
    const { container } = await renderApp(`/properties/${PROPERTY}`);

    await heading();
    expect(within(region()).getByText('Ce bien n’est pas loué.')).toBeInTheDocument();
    expect(within(region()).queryByText('Rendement brut')).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });
});
