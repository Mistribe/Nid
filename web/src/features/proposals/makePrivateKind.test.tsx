import { screen, waitFor } from '@testing-library/react';
import { beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { renderApp } from '@/test/renderApp';
import { resetFocus } from './focus';
import { proposal } from './testData';
import { serve } from './testServe';

const ask = () => proposal({
  kind: 'core.make_private', targetName: 'Compte courant', subjectKey: null,
  payload: { ownerId: '01926b9e-0000-7000-8000-00000000000a', ownerName: 'Alice' },
});

/** #384: the owner's request to make a shared item private, which the other adult approves or refuses as it is. */
describe('Un passage en privé', () => {
  beforeEach(resetFocus);
  beforeAll(async () => { await import('./ProposalDetailPage'); await import('./ProposalsPage'); });

  it('is listed as the owner’s request', async () => {
    serve([ask()]);
    await renderApp('/proposals');

    expect((await screen.findAllByRole('link', { name: 'Alice souhaite rendre « Compte courant » privé : vous ne le verrez plus' }))[0]).toBeInTheDocument();
  });

  it('tells the other adult what approving does, and approves it as proposed', async () => {
    const p = ask();
    const sent = serve([p], { preview: {} });
    const { user } = await renderApp(`/proposals/${p.id}`);

    expect(await screen.findByRole('heading', { level: 1, name: 'Passage en privé' })).toBeInTheDocument();
    expect(screen.getByText(/seul Alice le verra, vous ne le verrez plus/)).toBeInTheDocument();
    expect(screen.queryByRole('textbox')).toBeNull();

    await user.click(screen.getByRole('button', { name: 'Approuver' }));
    await waitFor(() => expect(sent.approve).toEqual({ version: 1 }));
  });
});
