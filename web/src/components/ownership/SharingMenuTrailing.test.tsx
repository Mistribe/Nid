import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { createApi } from '@/api/client';
import { createQueryClient } from '@/app/bootstrap';
import { Providers } from '@/app/Providers';
import { aliceSession, allCapabilities } from '@/test/fixtures';
import { SharingMenu } from './SharingMenu';

// The default MSW handler answers the sharing read with canChange true.
const open = async (trailing?: { label: string; onSelect: () => void }[]) => {
  const user = userEvent.setup();
  const api = createApi({ getCsrf: () => aliceSession.csrf, onUnauthenticated: vi.fn() });
  const location = { pathname: '/', search: '', assign: vi.fn(), reload: vi.fn() };
  render(
    <Providers api={api} queryClient={createQueryClient()} session={aliceSession} capabilities={allCapabilities} location={location}>
      <SharingMenu item="goal:1" shared label="Actions" trailing={trailing} onAnnounce={vi.fn()} />
    </Providers>,
  );
  await user.click(await screen.findByRole('button', { name: 'Actions' }));
};

describe('SharingMenu trailing items', () => {
  it('puts them last, after a separator', async () => {
    await open([{ label: 'Supprimer…', onSelect: vi.fn() }]);
    const items = await screen.findAllByRole('menuitem');
    expect(items[items.length - 1]).toHaveTextContent('Supprimer…');
    expect(screen.getAllByRole('separator')).toHaveLength(1);
  });

  it('adds no separator without trailing items', async () => {
    await open();
    await screen.findAllByRole('menuitem');
    expect(screen.queryByRole('separator')).not.toBeInTheDocument();
  });
});
