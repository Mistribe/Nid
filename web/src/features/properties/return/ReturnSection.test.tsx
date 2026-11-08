import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { pick } from '@/test/controls';
import type { PropertyReturn } from '@/api/properties';
import { PROPERTY, propertyDetail, propertyReturn } from '../testData';

const PAGE = `/properties/${PROPERTY}?tab=return`;

function serve(canWrite = true, answer: PropertyReturn = propertyReturn()) {
  const sent: { method: string; body?: unknown }[] = [];
  server.use(
    http.get('*/api/v1/realestate/properties/:id', () => HttpResponse.json(propertyDetail({ type: 'rental', canWrite }))),
    http.get('*/api/v1/realestate/properties/:id/return', () => HttpResponse.json(answer)),
    http.put('*/api/v1/realestate/properties/:id/return-assumptions', async ({ request }) => {
      sent.push({ method: 'PUT', body: await request.json() });
      return HttpResponse.json({ id: PROPERTY });
    }),
    http.delete('*/api/v1/realestate/properties/:id/return-assumptions', () => {
      sent.push({ method: 'DELETE' });
      return HttpResponse.json({ id: PROPERTY });
    }),
  );
  return sent;
}

describe('ReturnSection', () => {
  it('shows the scenario, the sixteen resale years and the warning', async () => {
    serve();
    await renderApp(PAGE);

    expect(await screen.findByText(/Revente en 2033 à 214\s000,00\s€/)).toBeInTheDocument();
    expect(screen.getByText('3,4 %')).toBeInTheDocument();
    const rows = within(screen.getByRole('table', { name: 'Rentabilité selon l’année de revente' })).getAllByRole('row').slice(1);
    expect(rows).toHaveLength(16);
    expect(rows[7]).toHaveAttribute('aria-current', 'true');
    expect(within(rows[7]!).getByText('fin du Pinel')).toBeInTheDocument();
    expect(within(rows[1]!).getByText('reprise Pinel')).toBeInTheDocument();
    expect(screen.getByText(/Pas d’estimation d’impôt/)).toBeInTheDocument();
    expect(screen.getByText('Estimation indicative fondée sur des hypothèses ; ce n’est pas un conseil en investissement.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Modifier les hypothèses' })).toBeInTheDocument();
  });

  it('offers no change to a reader', async () => {
    serve(false);
    await renderApp(PAGE);

    await screen.findByText(/Revente en 2033/);
    expect(screen.queryByRole('button', { name: 'Modifier les hypothèses' })).not.toBeInTheDocument();
  });

  it('sends the typed assumptions', async () => {
    const sent = serve();
    const { user } = await renderApp(PAGE);
    await user.click(await screen.findByRole('button', { name: 'Modifier les hypothèses' }));
    const dialog = within(await screen.findByRole('dialog', { name: 'Hypothèses de calcul' }));

    await user.type(dialog.getByRole('textbox', { name: /Revalorisation du bien/ }), '1,5');
    await pick(user, /Tranche marginale/, '41 %');
    await user.click(dialog.getByRole('button', { name: 'Enregistrer' }));

    await expect.poll(() => sent).toEqual([{ method: 'PUT', body: { valueGrowth: '0.015', marginalRate: '0.41' } }]);
  });

  it('goes back to the defaults', async () => {
    const sent = serve();
    const { user } = await renderApp(PAGE);
    await user.click(await screen.findByRole('button', { name: 'Modifier les hypothèses' }));
    await user.click(await screen.findByRole('button', { name: 'Revenir aux valeurs par défaut' }));

    await expect.poll(() => sent).toEqual([{ method: 'DELETE' }]);
  });

  it('lists the scenario as its own row when no resale year falls on its date', async () => {
    const base = propertyReturn();
    serve(true, { ...base, scenario: { ...base.scenario!, outcome: { ...base.scenario!.outcome, resaleOn: '2033-01-01' } } });
    await renderApp(PAGE);

    await screen.findByText(/Revente en 2033/);
    const rows = within(screen.getByRole('table', { name: 'Rentabilité selon l’année de revente' })).getAllByRole('row').slice(1);
    expect(rows).toHaveLength(17);
    expect(rows[7]).toHaveAttribute('aria-current', 'true');
    expect(within(rows[7]!).getByText('1 janvier 2033')).toBeInTheDocument();
    expect(rows[8]).not.toHaveAttribute('aria-current');
  });

  it('refuses a count that is not a whole number', async () => {
    const sent = serve();
    const { user } = await renderApp(PAGE);
    await user.click(await screen.findByRole('button', { name: 'Modifier les hypothèses' }));
    const dialog = within(await screen.findByRole('dialog', { name: 'Hypothèses de calcul' }));

    await user.type(dialog.getByRole('textbox', { name: /Années de location après le Pinel/ }), '2,5');
    await user.click(dialog.getByRole('button', { name: 'Enregistrer' }));

    expect(await dialog.findByText('Un nombre entier, sans virgule.')).toBeInTheDocument();
    expect(sent).toEqual([]);
  });
});
