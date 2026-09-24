import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { fakeImportServer, SESSION_ID, session } from '@/test/importServer';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';

describe('Import › one session', () => {
  it('shows an expired import and starts over', async () => {
    server.use(...fakeImportServer({ sessions: [session({ state: 'expired' })] }).handlers);
    const { user, router, container } = await renderApp(`/import/${SESSION_ID}`);

    expect(await screen.findByText('Cet import a expiré. Recommencez avec le fichier.')).toBeInTheDocument();
    await expectNoAxeViolations(container);
    await user.click(screen.getByRole('button', { name: 'Recommencer' }));
    expect(router.state.location.pathname).toBe('/import');
  });

  it('treats an active import past its expiry as expired, as the server does', async () => {
    server.use(...fakeImportServer({ sessions: [session({ state: 'validated', expiresAt: '2020-01-01T00:00:00+00:00' })] }).handlers);
    await renderApp(`/import/${SESSION_ID}`);

    expect(await screen.findByText('Cet import a expiré. Recommencez avec le fichier.')).toBeInTheDocument();
    expect(screen.getByText('État : expiré')).toBeInTheDocument();
  });

  it('shows a cancelled import', async () => {
    server.use(...fakeImportServer({ sessions: [session({ state: 'cancelled' })] }).handlers);
    await renderApp(`/import/${SESSION_ID}`);

    expect(await screen.findByText('Cet import a été annulé.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Nouvel import' })).toBeInTheDocument();
  });

  it('says an unknown import is not found, as for another person’s', async () => {
    server.use(...fakeImportServer().handlers);
    await renderApp(`/import/${SESSION_ID}`);

    expect(await screen.findByRole('alert')).toHaveTextContent('Cet élément est introuvable.');
  });
});
