import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { dashboard } from '@/features/dashboard/testData';
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
    await user.keyboard('{Escape}');
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Aller à' })).toBeNull());

    await user.keyboard('{Control>}k{/Control}');
    const again = await screen.findByRole('dialog', { name: 'Aller à' });
    await user.type(within(again).getByRole('combobox'), 'abonn');
    expect(within(again).queryByRole('option', { name: 'Placements' })).toBeNull();
    await user.click(within(again).getByRole('option', { name: 'Abonnements' }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/subscriptions'));
  });
});
