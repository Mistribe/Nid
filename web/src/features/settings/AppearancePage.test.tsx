import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { aliceSession } from '@/test/fixtures';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';

type Body = Record<string, unknown>;

function acceptPreferences(sent: Body[]) {
  server.use(http.patch('*/api/v1/core/me/preferences', async ({ request }) => {
    const body = (await request.json()) as Body;
    sent.push({ ...body, csrf: request.headers.get('X-Nid-Csrf') });
    return HttpResponse.json({ theme: 'neutre', mode: 'system', chartPatterns: false, guidesSeen: {}, ...body });
  }));
}

/** Holds every request open until its own `release()` is called, in the order they arrive. */
function queuePreferences(sent: Body[]) {
  const releases: Array<() => void> = [];
  server.use(http.patch('*/api/v1/core/me/preferences', async ({ request }) => {
    const body = (await request.json()) as Body;
    sent.push({ ...body, csrf: request.headers.get('X-Nid-Csrf') });
    await new Promise<void>((resolve) => releases.push(resolve));
    return HttpResponse.json({ theme: 'neutre', mode: 'system', chartPatterns: false, guidesSeen: {}, ...body });
  }));
  return releases;
}

/** Like `queuePreferences`, but the first request held open fails instead of succeeding. */
function queuePreferencesFailingFirst(sent: Body[]) {
  const releases: Array<() => void> = [];
  let index = 0;
  server.use(http.patch('*/api/v1/core/me/preferences', async ({ request }) => {
    const mine = index++;
    const body = (await request.json()) as Body;
    sent.push({ ...body, csrf: request.headers.get('X-Nid-Csrf') });
    await new Promise<void>((resolve) => releases.push(resolve));
    if (mine === 0) return HttpResponse.json({ status: 500, title: 'boom', traceId: '00-t-03' }, { status: 500 });
    return HttpResponse.json({ theme: 'neutre', mode: 'system', chartPatterns: false, guidesSeen: {}, ...body });
  }));
  return releases;
}

beforeAll(() => import('@/features/guides/GuideIntro'));

const html = () => document.documentElement;

describe('Réglages › Apparence', () => {
  it('applies a theme at once, saves it, and remembers it for the next load', async () => {
    const sent: Body[] = [];
    acceptPreferences(sent);
    const { user } = await renderApp('/settings/appearance');

    await user.click(screen.getByRole('radio', { name: 'Sauge' }));

    expect(html().dataset.theme).toBe('sauge');
    await waitFor(() => expect(screen.getByRole('radio', { name: 'Sauge' })).toBeChecked());
    expect(sent).toEqual([{ theme: 'sauge', csrf: 'csrf-alice' }]);
    expect(localStorage.getItem('nid.theme')).toBe('sauge');
  });

  it('switches to dark mode without a reload', async () => {
    const sent: Body[] = [];
    acceptPreferences(sent);
    const { user } = await renderApp('/settings/appearance');

    await user.click(screen.getByRole('radio', { name: 'Sombre' }));

    expect(html().classList.contains('dark')).toBe(true);
    await waitFor(() => expect(sent).toEqual([{ mode: 'dark', csrf: 'csrf-alice' }]));
  });

  it('previews a theme on hover and restores the saved one after', async () => {
    const { user } = await renderApp('/settings/appearance');
    const terre = screen.getByRole('radio', { name: 'Terre' }).closest('label')!;

    await user.hover(terre);
    expect(html().dataset.theme).toBe('terre');
    await user.unhover(terre);
    expect(html().dataset.theme).toBe('neutre');
  });

  it('puts the saved theme back and says so when saving fails', async () => {
    server.use(http.patch('*/api/v1/core/me/preferences', () =>
      HttpResponse.json({ status: 500, title: 'boom', traceId: '00-t-01' }, { status: 500 })));
    const { user } = await renderApp('/settings/appearance');

    await user.click(screen.getByRole('radio', { name: 'Ardoise' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Une erreur inattendue est survenue');
    expect(html().dataset.theme).toBe('neutre');
    expect(screen.getByRole('radio', { name: 'Neutre' })).toBeChecked();
    expect(localStorage.getItem('nid.theme')).toBeNull();
  });

  it('previews a theme on focus and restores the current choice on blur', async () => {
    await renderApp('/settings/appearance');
    const terre = screen.getByRole('radio', { name: 'Terre' });

    fireEvent.focus(terre);
    expect(html().dataset.theme).toBe('terre');
    fireEvent.blur(terre);
    expect(html().dataset.theme).toBe('neutre');
  });

  it('keeps dark applied through a hover preview while a mode save is pending', async () => {
    const sent: Body[] = [];
    const releases = queuePreferences(sent);
    const { user } = await renderApp('/settings/appearance');

    await user.click(screen.getByRole('radio', { name: 'Sombre' }));
    expect(html().classList.contains('dark')).toBe(true);

    const terre = screen.getByRole('radio', { name: 'Terre' }).closest('label')!;
    await user.hover(terre);
    expect(html().classList.contains('dark')).toBe(true);
    await user.unhover(terre);
    expect(html().classList.contains('dark')).toBe(true);

    releases[0]?.();
    await waitFor(() => expect(sent).toEqual([{ mode: 'dark', csrf: 'csrf-alice' }]));
  });

  it('serializes overlapping theme choices and ends on the latest one', async () => {
    const sent: Body[] = [];
    const releases = queuePreferences(sent);
    const { user } = await renderApp('/settings/appearance');

    await user.click(screen.getByRole('radio', { name: 'Sauge' }));
    await waitFor(() => expect(sent).toHaveLength(1));

    await user.click(screen.getByRole('radio', { name: 'Terre' }));
    expect(sent).toHaveLength(1);

    releases[0]?.();
    await waitFor(() => expect(sent).toHaveLength(2));
    // The first save has landed, but the newer choice is still the one on screen and on file.
    expect(html().dataset.theme).toBe('terre');
    expect(localStorage.getItem('nid.theme')).not.toBe('sauge');
    releases[1]?.();

    await waitFor(() => expect(screen.getByRole('radio', { name: 'Terre' })).toBeChecked());
    expect(sent).toEqual([
      { theme: 'sauge', csrf: 'csrf-alice' },
      { theme: 'terre', csrf: 'csrf-alice' },
    ]);
    expect(html().dataset.theme).toBe('terre');
    expect(localStorage.getItem('nid.theme')).toBe('terre');
  });

  it('puts the saved mode back and says so when saving fails', async () => {
    server.use(http.patch('*/api/v1/core/me/preferences', () =>
      HttpResponse.json({ status: 500, title: 'boom', traceId: '00-t-02' }, { status: 500 })));
    const { user } = await renderApp('/settings/appearance');

    await user.click(screen.getByRole('radio', { name: 'Sombre' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Une erreur inattendue est survenue');
    expect(html().classList.contains('dark')).toBe(false);
    expect(screen.getByRole('radio', { name: 'Système' })).toBeChecked();
  });

  it('keeps a newer queued choice applied when an older save fails', async () => {
    const sent: Body[] = [];
    const releases = queuePreferencesFailingFirst(sent);
    const { user } = await renderApp('/settings/appearance');

    await user.click(screen.getByRole('radio', { name: 'Ardoise' }));
    await waitFor(() => expect(sent).toHaveLength(1));

    await user.click(screen.getByRole('radio', { name: 'Terre' }));
    expect(sent).toHaveLength(1);

    releases[0]?.();
    expect(await screen.findByRole('alert')).toHaveTextContent('Une erreur inattendue est survenue');
    expect(html().dataset.theme).toBe('terre');

    await waitFor(() => expect(sent).toHaveLength(2));
    releases[1]?.();

    await waitFor(() => expect(screen.getByRole('radio', { name: 'Terre' })).toBeChecked());
    expect(html().dataset.theme).toBe('terre');
    expect(localStorage.getItem('nid.theme')).toBe('terre');
    await waitFor(() => expect(screen.queryByRole('alert')).not.toBeInTheDocument());
  });

  it('saves the chart patterns option', async () => {
    const sent: Body[] = [];
    acceptPreferences(sent);
    const { user } = await renderApp('/settings/appearance');

    await user.click(screen.getByRole('checkbox', { name: 'Motifs dans les graphiques' }));

    await waitFor(() => expect(sent).toEqual([{ chartPatterns: true, csrf: 'csrf-alice' }]));
  });

  it('resets every guide', async () => {
    const sent: Body[] = [];
    acceptPreferences(sent);
    const { container, router, user } = await renderApp('/settings/appearance');

    await user.click(screen.getByRole('button', { name: 'Revoir tous les guides' }));

    expect(await screen.findByRole('status')).toHaveTextContent('Les guides se rejoueront à votre prochaine visite de chaque module.');
    expect(sent).toEqual([{ guidesSeen: {}, csrf: 'csrf-alice' }]);
    await expectNoAxeViolations(container);

    await router.navigate({ to: '/loans' });
    expect(await screen.findByRole('dialog', { name: 'Crédits : premiers pas' })).toBeInTheDocument();
  });

  it('a theme save answered after a guide was marked seen keeps the guide seen', async () => {
    const sent: Body[] = [];
    const releases = queuePreferences(sent);
    const { router, user } = await renderApp('/settings/appearance', { session: { ...aliceSession, preferences: { ...aliceSession.preferences, guidesSeen: {} } } });

    await user.click(screen.getByRole('radio', { name: 'Sauge' }));
    await waitFor(() => expect(sent).toHaveLength(1));
    await router.navigate({ to: '/loans' });
    await user.click(within(await screen.findByRole('dialog', { name: 'Crédits : premiers pas' })).getByRole('button', { name: 'Passer' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());

    // The theme's response carries the guides as the server had them before the guide was closed.
    releases[0]?.();
    await waitFor(() => expect(sent).toHaveLength(2));
    releases[1]?.();

    await router.navigate({ to: '/settings/appearance' });
    await router.navigate({ to: '/loans' });
    await screen.findByRole('button', { name: 'Guide de cette page' });
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('a guide closed while a reset is in flight stays seen', async () => {
    const sent: Body[] = [];
    const releases = queuePreferences(sent);
    const { router, user } = await renderApp('/settings/appearance');

    await user.click(screen.getByRole('button', { name: 'Revoir tous les guides' }));
    await waitFor(() => expect(sent).toHaveLength(1));
    // The reset is local at once: the guide plays before the server has answered.
    await router.navigate({ to: '/loans' });
    await user.click(within(await screen.findByRole('dialog', { name: 'Crédits : premiers pas' })).getByRole('button', { name: 'Passer' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());

    // The reset's response carries the empty map from before the guide was closed.
    releases[0]?.();
    await waitFor(() => expect(sent).toHaveLength(2));
    expect(sent[1]).toEqual({ guidesSeen: { loans: 1 }, csrf: 'csrf-alice' });
    releases[1]?.();

    await router.navigate({ to: '/settings/appearance' });
    await router.navigate({ to: '/loans' });
    await screen.findByRole('button', { name: 'Guide de cette page' });
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('has no accessibility violation', async () => {
    const { container } = await renderApp('/settings/appearance');
    await expectNoAxeViolations(container);
  });
});
