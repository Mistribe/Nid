import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { adminCapabilities } from '@/test/fixtures';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';

const adults = [
  { id: 'a', displayName: 'Alice', email: 'alice@example.test', status: 'active', isAdmin: true },
  { id: 'b', displayName: 'Bob', email: 'bob@example.test', status: 'active', isAdmin: false },
];

describe('Administration › Journal de sécurité', () => {
  it('names the adults and shows the identifiers only', async () => {
    server.use(
      http.get('*/api/v1/admin/users', () => HttpResponse.json(adults)),
      http.get('*/api/v1/admin/security-journal', () => HttpResponse.json({
        entries: [
          { seq: 3, at: '2026-09-26T08:00:00+00:00', event: 'session_revoked', actor: { kind: 'person', personId: 'a', via: 'ui' }, subjectPersonId: 'b', details: {} },
          { seq: 2, at: '2026-09-26T07:00:00+00:00', event: 'login_refused', actor: { kind: 'system', personId: null, via: 'job' }, subjectPersonId: null, details: { subject: 'sub-x' } },
        ],
        next: null,
      })),
    );
    const { container } = await renderApp('/admin/security', { capabilities: adminCapabilities });

    const revoked = await screen.findByRole('row', { name: /Sessions fermées/ });
    expect(within(revoked).getByText('Bob')).toBeInTheDocument();
    expect(within(revoked).getByText('Alice')).toBeInTheDocument();
    expect(screen.getByRole('row', { name: /Connexion refusée/ })).toHaveTextContent('sub-x');
    await expectNoAxeViolations(container);
  });

  it('filters by event', async () => {
    const asked: Array<string | null> = [];
    server.use(
      http.get('*/api/v1/admin/users', () => HttpResponse.json(adults)),
      http.get('*/api/v1/admin/security-journal', ({ request }) => {
        asked.push(new URL(request.url).searchParams.get('event'));
        return HttpResponse.json({ entries: [], next: null });
      }),
    );
    const { user } = await renderApp('/admin/security', { capabilities: adminCapabilities });

    await user.selectOptions(await screen.findByLabelText('Événement'), 'access_violation');

    await expect.poll(() => asked.at(-1)).toBe('access_violation');
  });
});
