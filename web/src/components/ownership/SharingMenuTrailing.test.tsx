import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { SharingMenu } from './SharingMenu';

vi.mock('./SharingBadges', () => ({ useItemSharing: () => ({ data: { canChange: true } }) }));

const open = async (trailing?: { label: string; onSelect: () => void }[]) => {
  const user = userEvent.setup();
  render(<SharingMenu item="goal:1" shared label="Actions" trailing={trailing} onAnnounce={vi.fn()} />);
  await user.click(screen.getByRole('button', { name: 'Actions' }));
};

describe('SharingMenu trailing items', () => {
  it('puts them last, after a separator', async () => {
    await open([{ label: 'Supprimer…', onSelect: vi.fn() }]);
    const items = screen.getAllByRole('menuitem');
    expect(items[items.length - 1]).toHaveTextContent('Supprimer…');
    expect(screen.getAllByRole('separator')).toHaveLength(1);
  });

  it('adds no separator without trailing items', async () => {
    await open();
    expect(screen.queryByRole('separator')).not.toBeInTheDocument();
  });
});
