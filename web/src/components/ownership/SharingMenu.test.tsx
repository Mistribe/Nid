import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { detail, GOAL } from '@/features/goals/testData';

const sharing = (over: object = {}) => ({ visibility: 'shared', shareLevel: 'view', canChange: true, ...over });

describe('SharingMenu (#294)', () => {
  beforeAll(async () => { await import('@/features/goals/GoalDetailPage'); }, 30_000);

  it('lets an owner open a shared item to edits, and announces it', async () => {
    const sent: unknown[] = [];
    server.use(
      http.get('*/api/v1/goals/:id', () => HttpResponse.json(detail())),
      http.get('*/api/v1/core/items/:id/sharing', () => HttpResponse.json(sharing())),
      http.put('*/api/v1/core/items/:id/sharing', async ({ request }) => {
        sent.push(await request.json());
        return HttpResponse.json(sharing({ shareLevel: 'edit' }));
      }),
    );
    const { user } = await renderApp(`/goals/${GOAL}`);

    await user.click(await screen.findByRole('button', { name: 'Partage' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Partage…' }));
    const dialog = await screen.findByRole('dialog', { name: 'Partage' });
    expect(await within(dialog).findByText('L’autre adulte le consulte ; seuls les propriétaires le modifient.')).toBeInTheDocument();
    await user.click(await within(dialog).findByRole('radio', { name: 'Lecture et modification' }));
    expect(within(dialog).getByText(/sans en devenir propriétaire/)).toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));

    expect(await screen.findByText('Partage enregistré.')).toHaveAttribute('role', 'status');
    expect(sent).toEqual([{ shareLevel: 'edit' }]);
  });

  it('shows an adult who may only edit the level, without a way to change it', async () => {
    server.use(
      http.get('*/api/v1/goals/:id', () => HttpResponse.json(detail())),
      http.get('*/api/v1/core/items/:id/sharing', () => HttpResponse.json(sharing({ shareLevel: 'edit', canChange: false }))),
    );
    const { user } = await renderApp(`/goals/${GOAL}`);

    await user.click(await screen.findByRole('button', { name: 'Partage' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Partage…' }));
    const dialog = await screen.findByRole('dialog', { name: 'Partage' });
    expect(await within(dialog).findByText('Lecture et modification')).toBeInTheDocument();
    expect(within(dialog).getByText(/Seuls les propriétaires changent le partage/)).toBeInTheDocument();
    expect(within(dialog).queryByRole('radio')).not.toBeInTheDocument();
    expect(within(dialog).queryByRole('button', { name: 'Enregistrer' })).not.toBeInTheDocument();
  });

  it('offers nothing on a private item', async () => {
    server.use(http.get('*/api/v1/goals/:id', () => HttpResponse.json(detail({}, { visibility: 'private' }))));
    await renderApp(`/goals/${GOAL}`);

    await screen.findByRole('heading', { name: 'Voyage', level: 1 });
    expect(screen.queryByRole('button', { name: 'Partage' })).not.toBeInTheDocument();
  });
});
