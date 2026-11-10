import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import { dashboard } from '@/features/dashboard/testData';
import { adminCapabilities } from '@/test/fixtures';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';

describe('the ⌘K palette', () => {
  it('opens from the top bar and from Ctrl K, filters as one types, and goes to the page chosen', async () => {
    server.use(http.get('*/api/v1/dashboard', () => HttpResponse.json(dashboard())));
    const { router, user } = await renderApp('/dashboard');

    await user.click(await screen.findByRole('button', { name: 'Rechercher ou aller à…' }));
    const palette = await screen.findByRole('dialog', { name: 'Aller à' });
    expect(within(palette).getByRole('option', { name: 'Placements' })).toBeInTheDocument();
    expect(within(palette).getByRole('option', { name: 'Ajouter un crédit' })).toBeInTheDocument();
    expect(within(palette).getByRole('option', { name: 'Nouvel objectif' })).toBeInTheDocument();
    await user.keyboard('{Escape}');
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Aller à' })).toBeNull());

    await user.keyboard('{Control>}k{/Control}');
    const again = await screen.findByRole('dialog', { name: 'Aller à' });
    await user.type(within(again).getByRole('combobox'), 'abonn');
    expect(within(again).queryByRole('option', { name: 'Placements' })).toBeNull();
    await user.click(within(again).getByRole('option', { name: 'Abonnements' }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/subscriptions'));
  });

  it('finds every admin section with the keyword admin', async () => {
    server.use(http.get('*/api/v1/dashboard', () => HttpResponse.json(dashboard())));
    const { user } = await renderApp('/dashboard', { capabilities: adminCapabilities });
    await user.click(await screen.findByRole('button', { name: 'Rechercher ou aller à…' }));
    const palette = await screen.findByRole('dialog', { name: 'Aller à' });
    await user.type(within(palette).getByRole('combobox'), 'admin');
    expect(within(palette).getByRole('option', { name: 'Cours' })).toBeInTheDocument();
  });

  it('the « Aller à » entries show their module tile', async () => {
    server.use(http.get('*/api/v1/dashboard', () => HttpResponse.json(dashboard())));
    const { user } = await renderApp('/dashboard');
    await user.click(await screen.findByRole('button', { name: 'Rechercher ou aller à…' }));
    const palette = await screen.findByRole('dialog', { name: 'Aller à' });
    const group = (name: string) => within(palette).getByRole('group', { name });
    for (const option of within(group('Aller à')).getAllByRole('option')) expect(option.querySelector('[data-tile]'), option.textContent).not.toBeNull();
    for (const option of within(group('Créer')).getAllByRole('option')) expect(option.querySelector('[data-tile]'), option.textContent).toBeNull();
  });

  it('opens the series explorer', async () => {
    server.use(http.get('*/api/v1/dashboard', () => HttpResponse.json(dashboard())));
    const { router, user } = await renderApp('/dashboard');
    await user.click(await screen.findByRole('button', { name: 'Rechercher ou aller à…' }));
    const palette = await screen.findByRole('dialog', { name: 'Aller à' });
    await user.type(within(palette).getByRole('combobox'), 'séries');
    await user.click(within(palette).getByRole('option', { name: 'Explorer les séries' }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/series'));
  });
});
