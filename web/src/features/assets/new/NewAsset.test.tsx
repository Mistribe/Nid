import { screen, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { persons } from '../../subscriptions/testData';
import { ASSET, assetView } from '../testData';

function serve(bodies: unknown[], answer: () => Response = () => HttpResponse.json({ id: ASSET }, { status: 201 })) {
  server.use(
    http.get('*/api/v1/core/persons', () => HttpResponse.json(persons)),
    http.post('*/api/v1/assets', async ({ request }) => { bodies.push(await request.json()); return answer(); }),
    http.get('*/api/v1/assets/:id', () => HttpResponse.json(assetView())),
  );
}

describe('NewAsset', () => {
  beforeAll(async () => { await import('./NewAsset'); }, 30_000);

  it('shows the server’s value_required refusal on the value field', async () => {
    serve([], () => HttpResponse.json({
      status: 422, code: 'assets.value_required', detail: 'An asset has a value.',
      errors: [{ path: 'currentValue', code: 'assets.value_required', message: 'An asset has a value.' }],
    }, { status: 422 }));
    const { user } = await renderApp('/other-assets/new');

    await user.type(await screen.findByLabelText('Nom'), 'Citadine');
    await user.click(screen.getByRole('radio', { name: 'Oui, partagé' }));
    await user.click(screen.getByRole('button', { name: 'Ajouter le bien' }));

    const message = await screen.findByText('Indiquez ce que vaut ce bien : son prix d’achat ou sa valeur actuelle.');
    const amount = screen.getByLabelText('Montant');
    expect(amount).toHaveAttribute('aria-invalid', 'true');
    expect(amount.getAttribute('aria-describedby')).toContain(message.id);
  });

  it('ignores the metal fields once the kind is no longer a precious metal', async () => {
    const bodies: unknown[] = [];
    serve(bodies);
    const { user, router } = await renderApp('/other-assets/new');
    await user.type(await screen.findByLabelText('Nom'), 'Citadine');
    await pick(user, 'Type', 'Métal précieux');
    await user.type(screen.getByLabelText('Poids (grammes)'), 'abc');
    await pick(user, 'Type', 'Véhicule');
    await user.click(screen.getByRole('radio', { name: 'Oui, partagé' }));
    await user.type(screen.getByLabelText('Montant'), '8000');
    await user.type(screen.getByLabelText('Date'), '01/03/2024');
    await user.click(screen.getByRole('button', { name: 'Ajouter le bien' }));

    await waitFor(() => expect(router.state.location.pathname).toBe(`/other-assets/${ASSET}`));
    expect(bodies[0]).not.toHaveProperty('weightGrams');
  });

  it('asks for the weight and the fineness only for a precious metal, and sends them', async () => {
    const bodies: unknown[] = [];
    serve(bodies);
    const { user, router } = await renderApp('/other-assets/new');
    await user.type(await screen.findByLabelText('Nom'), 'Lingot');
    expect(screen.queryByLabelText('Poids (grammes)')).not.toBeInTheDocument();

    await pick(user, 'Type', 'Métal précieux');
    await user.type(screen.getByLabelText('Poids (grammes)'), '31,1');
    await user.type(screen.getByLabelText('Titre (‰)'), '999,9');
    await user.click(screen.getByRole('radio', { name: 'Oui, partagé' }));
    await user.type(screen.getByLabelText('Montant'), '2000');
    await user.type(screen.getByLabelText('Date'), '01/03/2024');
    await user.click(screen.getByRole('button', { name: 'Ajouter le bien' }));

    await waitFor(() => expect(router.state.location.pathname).toBe(`/other-assets/${ASSET}`));
    expect(bodies).toEqual([expect.objectContaining({
      name: 'Lingot', kind: 'precious_metal', weightGrams: 31.1, fineness: 999.9, purchasePrice: '2000', purchasedOn: '2024-03-01', visibility: 'shared',
    })]);
  });

  it('asks for the date when a purchase price has none', async () => {
    const bodies: unknown[] = [];
    serve(bodies);
    const { user } = await renderApp('/other-assets/new');
    await user.type(await screen.findByLabelText('Nom'), 'Citadine');
    await user.click(screen.getByRole('radio', { name: 'Oui, partagé' }));
    await user.type(screen.getByLabelText('Montant'), '8000');
    await user.click(screen.getByRole('button', { name: 'Ajouter le bien' }));

    expect(await screen.findByText('Un achat a un prix et une date, ou ni l’un ni l’autre.')).toBeInTheDocument();
    expect(screen.getByLabelText('Date')).toHaveAttribute('aria-invalid', 'true');
    expect(bodies).toEqual([]);
  });
});
