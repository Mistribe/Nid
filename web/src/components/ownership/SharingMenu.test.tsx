import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { renderApp } from '@/test/renderApp';
import { toasts } from '@/test/toasts';
import { server } from '@/test/server';
import { detail, GOAL } from '@/features/goals/testData';

const sharing = (over: object = {}) => ({ visibility: 'shared', shareLevel: 'view', canChange: true, isOwner: true, ...over });

describe('SharingMenu (#294)', () => {
  beforeAll(async () => { await import('@/features/goals/GoalDetailPage'); }, 30_000);

  it('lets an owner open a shared item to edits, and announces it', async () => {
    const sent: unknown[] = [];
    let level = 'view';
    server.use(
      http.get('*/api/v1/goals/:id', () => HttpResponse.json(detail())),
      http.get('*/api/v1/core/items/:id/sharing', () => HttpResponse.json(sharing({ shareLevel: level }))),
      http.put('*/api/v1/core/items/:id/sharing', async ({ request }) => {
        sent.push(await request.json());
        level = 'edit';
        return HttpResponse.json(sharing({ shareLevel: level }));
      }),
    );
    const { user } = await renderApp(`/goals/${GOAL}`);
    expect(await screen.findByText('Vous en êtes propriétaire')).toBeInTheDocument();
    expect(screen.getByText('Lecture seule')).toBeInTheDocument();

    // The goal page folds the sharing entry into its « Autres actions » menu, with « Supprimer… » (#23).
    await user.click(await screen.findByRole('button', { name: 'Autres actions' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Partage…' }));
    const dialog = await screen.findByRole('dialog', { name: 'Partage' });
    expect(await within(dialog).findByText('L’autre adulte le consulte ; seuls les propriétaires le modifient.')).toBeInTheDocument();
    await user.click(await within(dialog).findByRole('radio', { name: 'Lecture et modification' }));
    expect(within(dialog).getByText(/sans en devenir propriétaire/)).toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(toasts()).toHaveTextContent('Partage enregistré.'));
    expect(sent).toEqual([{ shareLevel: 'edit' }]);
    expect(await screen.findByText('Lecture et modification')).toBeInTheDocument();
  });

  it('shows an adult who may only edit the item its level and that they do not own it, with no way to change it', async () => {
    server.use(
      http.get('*/api/v1/goals/:id', () => HttpResponse.json(detail())),
      http.get('*/api/v1/core/items/:id/sharing', () => HttpResponse.json(sharing({ shareLevel: 'edit', canChange: false, isOwner: false }))),
    );
    const { user } = await renderApp(`/goals/${GOAL}`);

    expect(await screen.findByText('Vous n’en êtes pas propriétaire')).toBeInTheDocument();
    expect(screen.getByText('Lecture et modification')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Modifier' })).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Autres actions' }));
    expect(await screen.findByRole('menuitem', { name: 'Supprimer…' })).toBeInTheDocument();
    expect(screen.queryByRole('menuitem', { name: 'Partage…' })).not.toBeInTheDocument();
  });

  it('offers nothing on a private item', async () => {
    server.use(http.get('*/api/v1/goals/:id', () => HttpResponse.json(detail({}, { visibility: 'private' }))));
    const { user } = await renderApp(`/goals/${GOAL}`);

    await screen.findByRole('heading', { name: 'Voyage', level: 1 });
    await user.click(screen.getByRole('button', { name: 'Autres actions' }));
    expect(await screen.findByRole('menuitem', { name: 'Supprimer…' })).toBeInTheDocument();
    expect(screen.queryByRole('menuitem', { name: 'Partage…' })).not.toBeInTheDocument();
  });
});
