import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import { dashboard } from '@/features/dashboard/testData';
import { list, summary } from '@/features/documents/testData';
import { adminCapabilities, allCapabilities } from '@/test/fixtures';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';

describe('the ⌘K palette', () => {
  it('opens from the top bar and from Ctrl K, filters as one types, and goes to the page chosen', async () => {
    server.use(http.get('*/api/v1/dashboard', () => HttpResponse.json(dashboard())));
    const { router, user } = await renderApp('/dashboard');

    await user.click(await screen.findByRole('button', { name: 'Rechercher ou aller à…' }));
    const palette = await screen.findByRole('dialog', { name: 'Aller à' });
    expect(within(palette).getByRole('option', { name: 'Comptes et placements' })).toBeInTheDocument();
    expect(within(palette).getByRole('option', { name: 'Ajouter un crédit' })).toBeInTheDocument();
    expect(within(palette).getByRole('option', { name: 'Nouvel objectif' })).toBeInTheDocument();
    await user.keyboard('{Escape}');
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Aller à' })).toBeNull());

    await user.keyboard('{Control>}k{/Control}');
    const again = await screen.findByRole('dialog', { name: 'Aller à' });
    await user.type(within(again).getByRole('combobox'), 'abonn');
    expect(within(again).queryByRole('option', { name: 'Comptes et placements' })).toBeNull();
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

  it('finds documents by their text, after a pause in the typing, and opens one or the whole search (#170)', async () => {
    const asked: string[] = [];
    server.use(
      http.get('*/api/v1/dashboard', () => HttpResponse.json(dashboard())),
      http.get('*/api/v1/documents', ({ request }) => {
        asked.push(new URL(request.url).search);
        return HttpResponse.json({
          ...list([summary({ id: 'scan', title: 'Scan 0042', match: { snippet: 'Intervention sur la \uE000chaudière\uE001', page: 2, ocr: true, byMeaning: false } })], 7),
          meaningSearch: 'used',
        });
      }),
    );
    const { router, user } = await renderApp('/dashboard');
    await user.click(await screen.findByRole('button', { name: 'Rechercher ou aller à…' }));
    const palette = await screen.findByRole('dialog', { name: 'Aller à' });

    await user.type(within(palette).getByRole('combobox'), 'chaudiere');
    const group = await within(palette).findByRole('group', { name: 'Documents' });
    const scan = within(group).getByRole('option', { name: /Scan 0042/ });
    expect(scan).toHaveTextContent('page 2 · lu par OCR');
    expect(within(scan).getByText('chaudière').tagName).toBe('MARK');
    // One search for the word typed, over every document the person reads.
    expect(asked).toHaveLength(1);
    expect(asked[0]).toContain('q=chaudiere');
    expect(asked[0]).toContain('scope=me');

    await user.click(within(group).getByRole('option', { name: 'Voir les 7 documents trouvés dans Documents' }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/documents'));
    expect(router.state.location.search).toMatchObject({ q: 'chaudiere' });
  });

  it('opens a document found, and asks nothing without the Documents module or for one letter (#170)', async () => {
    const asked: string[] = [];
    server.use(
      http.get('*/api/v1/dashboard', () => HttpResponse.json(dashboard())),
      http.get('*/api/v1/documents', ({ request }) => {
        asked.push(new URL(request.url).search);
        return HttpResponse.json(list([summary({ id: 'scan', title: 'Scan 0042' })]));
      }),
    );
    const first = await renderApp('/dashboard');
    await first.user.click(await screen.findByRole('button', { name: 'Rechercher ou aller à…' }));
    const palette = await screen.findByRole('dialog', { name: 'Aller à' });
    await first.user.type(within(palette).getByRole('combobox'), 's');
    await new Promise((done) => setTimeout(done, 400));
    expect(asked).toEqual([]);
    await first.user.type(within(palette).getByRole('combobox'), 'can');
    await first.user.click(await within(palette).findByRole('option', { name: /Scan 0042/ }));
    await waitFor(() => expect(first.router.state.location.pathname).toBe('/documents/scan'));
    first.unmount();

    asked.length = 0;
    const without = await renderApp('/dashboard', { capabilities: { ...allCapabilities, modules: allCapabilities.modules.filter((m) => m !== 'documents') } });
    await without.user.click(await screen.findByRole('button', { name: 'Rechercher ou aller à…' }));
    await without.user.type(within(await screen.findByRole('dialog', { name: 'Aller à' })).getByRole('combobox'), 'scan');
    await new Promise((done) => setTimeout(done, 400));
    expect(asked).toEqual([]);
  });
});
