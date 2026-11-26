import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { adminCapabilities } from '@/test/fixtures';
import { renderApp } from '@/test/renderApp';
import { pick } from '@/test/controls';
import { server } from '@/test/server';

function serve() {
  server.use(http.get('*/api/v1/ai/assistant/me', () => HttpResponse.json({ enabled: false, offsiteDefault: 'ask', cloudPossible: false })));
}

describe('Réglages › IA', () => {
  it('keeps only what each adult decides: the assistant, their own usage, and says who sets the rest', async () => {
    serve();
    const { container } = await renderApp('/settings/ai');

    expect(await screen.findByRole('switch', { name: "Activer l’assistant pour moi" })).toBeInTheDocument();
    expect(screen.getByText(/réglés par un administrateur/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: "Voir l’utilisation" })).toHaveAttribute('href', '/settings/ai/usage');
    expect(screen.queryByRole('link', { name: "Régler l’IA du foyer" })).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Workers' })).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Modèles' })).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'IA' })).toHaveAttribute('aria-current', 'page');
    await expectNoAxeViolations(container);
  });

  it('points an admin to Administration › IA', async () => {
    serve();
    await renderApp('/settings/ai', { capabilities: adminCapabilities });

    expect(await screen.findByRole('link', { name: "Régler l’IA du foyer" })).toHaveAttribute('href', '/admin/ai');
  });

  it('turns the assistant on for oneself, after saying what may leave when it can reach the cloud, and sets the preference', async () => {
    serve();
    const enabled: unknown[] = [];
    const offsite: unknown[] = [];
    server.use(
      http.get('*/api/v1/ai/assistant/me', () => HttpResponse.json({ enabled: false, offsiteDefault: 'ask', cloudPossible: true })),
      http.put('*/api/v1/ai/assistant/me/enabled', async ({ request }) => {
        enabled.push(await request.json());
        return HttpResponse.json({ enabled: true, offsiteDefault: 'ask', cloudPossible: true });
      }),
      http.put('*/api/v1/ai/assistant/me/offsite', async ({ request }) => {
        offsite.push(await request.json());
        return HttpResponse.json({ enabled: false, offsiteDefault: 'never', cloudPossible: true });
      }),
    );
    const { user } = await renderApp('/settings/ai');

    await user.click(await screen.findByRole('switch', { name: "Activer l’assistant pour moi" }));
    const confirm = await screen.findByRole('alertdialog');
    expect(confirm).toHaveTextContent('jamais les éléments privés de l’autre adulte');
    expect(enabled).toEqual([]);
    await user.click(within(confirm).getByRole('button', { name: 'Confirmer' }));
    await waitFor(() => expect(enabled).toEqual([{ enabled: true }]));

    await pick(user, 'Vos données hors de chez vous', 'Jamais');
    await waitFor(() => expect(offsite).toEqual([{ preference: 'never' }]));
  });

});
