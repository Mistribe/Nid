import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { Detail } from '../api';
import { detail, persons, SUB } from '../testData';

const TERMS = { anniversaryOn: '2026-12-01', notice: { count: 2, unit: 'month' }, commitmentEndsOn: null, cancellableFrom: null };
const DEADLINE = { status: 'deadline', lastDayToCancel: '2026-10-01', effectiveOn: '2026-12-01', freeOn: null };

function serve(body: Detail, patch?: Parameters<typeof http.patch>[1]) {
  const sent: unknown[] = [];
  server.use(
    http.patch('*/api/v1/subscriptions/:id', patch ?? (async ({ request }) => { sent.push(await request.json()); return HttpResponse.json({ id: SUB }); })),
    http.get('*/api/v1/subscriptions/providers', () => HttpResponse.json([])),
    http.get('*/api/v1/subscriptions/:id', () => HttpResponse.json(body)),
    http.get('*/api/v1/core/persons', () => HttpResponse.json(persons)),
  );
  return sent;
}

const openDialog = async (user: Awaited<ReturnType<typeof renderApp>>['user']) => {
  await screen.findByRole('heading', { level: 1, name: 'Spotify Premium Duo' });
  await user.click(screen.getByRole('button', { name: 'Conditions du contrat' }));
  return screen.findByRole('form', { name: 'Conditions du contrat' });
};

describe('Abonnements › conditions du contrat', () => {
  it('hides the button without write access', async () => {
    serve(detail({ canWrite: false }));
    await renderApp(`/subscriptions/${SUB}`);
    await screen.findByRole('heading', { level: 1, name: 'Spotify Premium Duo' });
    expect(screen.queryByRole('button', { name: 'Conditions du contrat' })).not.toBeInTheDocument();
  });

  it('shows the last day to cancel in the key figures only for a deadline', async () => {
    serve(detail({ contractTerms: TERMS, cancellation: DEADLINE }));
    await renderApp(`/subscriptions/${SUB}`);
    expect((await screen.findByText('Dernier jour pour résilier')).nextElementSibling).toHaveTextContent('1 octobre 2026');
  });

  it('keeps the key figures free of it when it is free to cancel', async () => {
    serve(detail({ contractTerms: { ...TERMS, notice: null }, cancellation: { status: 'free_now' } }));
    await renderApp(`/subscriptions/${SUB}`);
    await screen.findByRole('region', { name: 'Contrat' });
    expect(screen.queryByText('Dernier jour pour résilier')).not.toBeInTheDocument();
  });

  it('sends the dates and the notice of a subscription without terms', async () => {
    const sent = serve(detail());
    const { user } = await renderApp(`/subscriptions/${SUB}`);
    const form = await openDialog(user);

    await user.type(within(form).getByLabelText('Date anniversaire'), '2026-12-01');
    await user.type(within(form).getByLabelText('Préavis'), '2');
    await pick(user, 'Unité du préavis', 'mois');
    await user.click(within(form).getByRole('button', { name: 'Enregistrer les conditions' }));

    await waitFor(() => expect(sent).toEqual([{
      anniversaryOn: '2026-12-01', notice: { count: 2, unit: 'month' },
      clearAnniversaryOn: false, clearNotice: false, clearCommitmentEndsOn: false, clearCancellableFrom: false,
    }]));
  });

  it('sends only what changed, and the matching clear flag for an emptied date', async () => {
    const sent = serve(detail({ contractTerms: { ...TERMS, commitmentEndsOn: '2027-06-01' }, cancellation: DEADLINE }));
    const { user } = await renderApp(`/subscriptions/${SUB}`);
    const form = await openDialog(user);

    await user.clear(within(form).getByLabelText('Fin d’engagement'));
    await user.click(within(form).getByRole('button', { name: 'Enregistrer les conditions' }));

    await waitFor(() => expect(sent).toEqual([{
      clearAnniversaryOn: false, clearNotice: false, clearCommitmentEndsOn: true, clearCancellableFrom: false,
    }]));
  });

  it('clears the notice when its count is emptied', async () => {
    const sent = serve(detail({ contractTerms: TERMS, cancellation: DEADLINE }));
    const { user } = await renderApp(`/subscriptions/${SUB}`);
    const form = await openDialog(user);

    await user.clear(within(form).getByLabelText('Préavis'));
    await user.click(within(form).getByRole('button', { name: 'Enregistrer les conditions' }));

    await waitFor(() => expect(sent).toEqual([{
      clearAnniversaryOn: false, clearNotice: true, clearCommitmentEndsOn: false, clearCancellableFrom: false,
    }]));
  });

  it('shows a refusal of the notice on its field', async () => {
    serve(detail(), () => HttpResponse.json({
      status: 422, code: 'subscriptions.notice_without_date', title: 'A notice needs an anniversary or an end of commitment.',
      errors: [{ path: 'notice', code: 'subscriptions.notice_without_date', message: 'A notice needs an anniversary or an end of commitment.' }],
    }, { status: 422 }));
    const { user } = await renderApp(`/subscriptions/${SUB}`);
    const form = await openDialog(user);

    await user.type(within(form).getByLabelText('Préavis'), '2');
    await user.click(within(form).getByRole('button', { name: 'Enregistrer les conditions' }));

    const notice = within(form).getByLabelText('Préavis');
    await waitFor(() => expect(notice).toHaveAttribute('aria-invalid', 'true'));
    expect(notice).toHaveAccessibleDescription(/préavis a besoin d’une date/);
  });
});
