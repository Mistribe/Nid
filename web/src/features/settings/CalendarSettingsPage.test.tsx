import { screen, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';

type Body = Record<string, unknown>;

const CALENDAR = '*/api/v1/core/me/calendar';
const ID = '0192f0a0-0000-7000-8000-0000000000c1';
const URL = 'https://nid.example/api/v1/calendar/nid_cal_abcdefgh_secret.ics';

const feed = (over: Body = {}) => ({
  token: {
    id: ID, name: 'Calendrier', preset: 'calendar', prefix: 'abcdefgh', createdAt: '2026-11-01T08:00:00Z', expiresAt: '2027-11-01T08:00:00Z',
    lastUsedAt: null, revokedAt: null, status: 'active',
  },
  scope: 'household', amounts: false, ...over,
});

function serve(initial: Body | null) {
  let current = initial;
  const posts: Body[] = [];
  const puts: Body[] = [];
  const deletes: string[] = [];
  server.use(
    http.get(CALENDAR, () => HttpResponse.json({ feed: current })),
    http.post(CALENDAR, async ({ request }) => {
      const body = (await request.json()) as Body;
      posts.push(body);
      current = feed(body);
      return HttpResponse.json({ feed: current, url: URL }, { status: 201 });
    }),
    http.put(CALENDAR, async ({ request }) => {
      const body = (await request.json()) as Body;
      puts.push(body);
      current = feed(body);
      return HttpResponse.json(current);
    }),
    http.delete('*/api/v1/core/me/tokens/:id', ({ params }) => { deletes.push(params.id as string); current = null; return new HttpResponse(null, { status: 204 }); }),
  );
  return { posts, puts, deletes };
}

beforeAll(async () => { await import('./CalendarSettingsPage'); }, 30_000);

describe('the calendar settings page', () => {
  it('creates a feed without amounts by default and shows its address once, with a webcal link', async () => {
    const { posts } = serve(null);
    const { user, container } = await renderApp('/settings/calendar');

    expect(await screen.findByRole('link', { name: 'Calendrier' })).toHaveAttribute('aria-current', 'page');
    expect(await screen.findByRole('checkbox', { name: 'Afficher les montants' })).not.toBeChecked();
    await user.click(screen.getByRole('radio', { name: 'Moi : les éléments partagés et les miens' }));
    await user.click(screen.getByRole('button', { name: 'Créer l’adresse' }));

    await waitFor(() => expect(posts).toEqual([{ scope: 'me', amounts: false }]));
    expect(await screen.findByText(URL)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Ouvrir dans le calendrier' })).toHaveAttribute('href', 'webcal://nid.example/api/v1/calendar/nid_cal_abcdefgh_secret.ics');
    await expectNoAxeViolations(container);

    await user.click(screen.getByRole('button', { name: 'Terminé' }));
    expect(screen.queryByText(URL)).not.toBeInTheDocument();
    expect(await screen.findByText('Jamais lue pour l’instant')).toBeInTheDocument();
  });

  it('changes the options of the active feed, its address unchanged', async () => {
    const { puts, posts } = serve(feed());
    const { user } = await renderApp('/settings/calendar');

    const save = await screen.findByRole('button', { name: 'Enregistrer' });
    expect(save).toBeDisabled();
    await user.click(screen.getByRole('checkbox', { name: 'Afficher les montants' }));
    await user.click(save);

    await waitFor(() => expect(puts).toEqual([{ scope: 'household', amounts: true }]));
    expect(posts).toHaveLength(0);
  });

  it('regenerates and revokes after a confirmation', async () => {
    const { posts, deletes } = serve(feed({ amounts: true }));
    const { user } = await renderApp('/settings/calendar');

    await user.click(await screen.findByRole('button', { name: 'Régénérer l’adresse' }));
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));
    await waitFor(() => expect(posts).toEqual([{ scope: 'household', amounts: true }]));
    expect(await screen.findByText(URL)).toBeInTheDocument();

    await user.click(await screen.findByRole('button', { name: 'Supprimer l’abonnement' }));
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));
    await waitFor(() => expect(deletes).toEqual([ID]));
    expect(await screen.findByRole('button', { name: 'Créer l’adresse' })).toBeInTheDocument();
  });
});
