import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import type { Preferences } from '@/app/session';
import { expectNoAxeViolations } from '@/test/axe';
import { aliceSession } from '@/test/fixtures';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { allGuidesSeen, guideFor } from './guides';

beforeAll(() => import('./GuideIntro'));

const n = guideFor('/loans')!.screens;

/** Renders `path` with these guides seen; the PATCH bodies of the preferences land in the returned array. */
async function render(path: string, guidesSeen: Preferences['guidesSeen'], status = 200) {
  const bodies: unknown[] = [];
  server.use(http.patch('*/api/v1/core/me/preferences', async ({ request }) => {
    const body = await request.json() as Partial<Preferences>;
    bodies.push(body);
    return status === 200
      ? HttpResponse.json({ ...aliceSession.preferences, ...body })
      : HttpResponse.json({ title: 'Erreur', status }, { status });
  }));
  return { bodies, ...await renderApp(path, { session: { ...aliceSession, preferences: { ...aliceSession.preferences, guidesSeen } } }) };
}

const loansGuide = () => screen.findByRole('dialog', { name: 'Crédits : premiers pas' });

describe('the module guides', () => {
  it('plays the guide the first time a module is opened', async () => {
    const { user } = await render('/loans', {});
    const dialog = await loansGuide();
    expect(within(dialog).getByText(`1 sur ${n}`)).toBeInTheDocument();

    await user.click(within(dialog).getByRole('button', { name: 'Suivant' }));
    expect(within(dialog).getByText(`2 sur ${n}`)).toBeInTheDocument();

    await user.click(within(dialog).getByRole('button', { name: 'Précédent' }));
    expect(within(dialog).getByText(`1 sur ${n}`)).toBeInTheDocument();
  });

  it('keeps the focus on the main button from screen to screen', async () => {
    const { bodies, user } = await render('/loans', {});
    const dialog = await loansGuide();
    const main = within(dialog).getByRole('button', { name: 'Suivant' });

    main.focus();
    for (let i = 1; i < n; i++) await user.keyboard('{Enter}');
    expect(within(dialog).getByText(`${n} sur ${n}`)).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Commencer' })).toHaveFocus();

    within(dialog).getByRole('button', { name: 'Précédent' }).focus();
    for (let i = 1; i < n; i++) await user.keyboard('{Enter}');
    expect(within(dialog).getByText(`1 sur ${n}`)).toBeInTheDocument();
    expect(main).toHaveFocus();

    for (let i = 1; i <= n; i++) await user.keyboard('{Enter}');
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    await waitFor(() => expect(bodies).toEqual([{ guidesSeen: { loans: 1 } }]));
  });

  it('does not play a guide already seen, and plays it again after a version bump', async () => {
    const seen = await render('/loans', allGuidesSeen());
    await screen.findByRole('button', { name: 'Guide de cette page' });
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    seen.unmount();

    await render('/loans', { loans: 0 });
    expect(await loansGuide()).toBeInTheDocument();
  });

  it.each([
    ['« Passer »', 'Passer'],
    ['« Commencer »', 'Commencer'],
    ['Escape', null],
  ])('closing by %s marks it seen', async (_, button) => {
    const { bodies, router, user } = await render('/loans', {});
    const dialog = await loansGuide();
    if (button === 'Commencer') {
      for (let i = 1; i < n; i++) await user.click(within(dialog).getByRole('button', { name: 'Suivant' }));
    }
    if (button) {
      await user.click(within(dialog).getByRole('button', { name: button }));
    } else {
      within(dialog).getByRole('button', { name: 'Suivant' }).focus();
      await user.keyboard('{Escape}');
    }

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    await waitFor(() => expect(bodies).toEqual([{ guidesSeen: { loans: 1 } }]));

    await router.navigate({ to: '/security' });
    await router.navigate({ to: '/loans' });
    await screen.findByRole('button', { name: 'Guide de cette page' });
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('marks the first guide seen and opens the next section’s guide', async () => {
    const { bodies, router, user } = await render('/loans', {});
    await loansGuide();

    await router.navigate({ to: '/income' });
    const income = await screen.findByRole('dialog', { name: 'Revenus : premiers pas' });
    expect(screen.queryByRole('dialog', { name: 'Crédits : premiers pas' })).not.toBeInTheDocument();
    await waitFor(() => expect(bodies).toEqual([{ guidesSeen: { loans: 1 } }]));

    await user.click(within(income).getByRole('button', { name: 'Passer' }));
    await waitFor(() => expect(bodies[1]).toEqual({ guidesSeen: { loans: 1, income: 1 } }));
  });

  it('a failed save says nothing and does not replay the guide in this session', async () => {
    const { bodies, router, user } = await render('/loans', { income: 1 }, 500);
    await user.click(within(await loansGuide()).getByRole('button', { name: 'Passer' }));

    await waitFor(() => expect(bodies).toHaveLength(1));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.queryAllByRole('status').map((s) => s.textContent).join('')).toBe('');

    await router.navigate({ to: '/income' });
    await router.navigate({ to: '/loans' });
    await screen.findByRole('button', { name: 'Guide de cette page' });
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('« ? » replays a seen guide and gives the focus back', async () => {
    const { bodies, queryClient, user } = await render('/loans', allGuidesSeen());
    const help = await screen.findByRole('button', { name: 'Guide de cette page' });

    await user.click(help);
    await user.click(within(await loansGuide()).getByRole('button', { name: 'Passer' }));

    await waitFor(() => expect(help).toHaveFocus());
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    // A save would be in the mutation cache at once, before its request lands.
    expect(queryClient.getMutationCache().getAll()).toHaveLength(0);
    expect(bodies).toEqual([]);
  });

  it('« ? » is absent where no guide exists', async () => {
    await render('/security', {});
    expect(screen.queryByRole('button', { name: 'Guide de cette page' })).not.toBeInTheDocument();
  });

  it('is accessible', async () => {
    await render('/loans', {});
    await loansGuide();
    // The dialog sits in a portal, outside the app's container.
    await expectNoAxeViolations(document.body);
  });
});
