import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { adminCapabilities } from '@/test/fixtures';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { toasts } from '@/test/toasts';
import { formatInstant } from '@/lib/format';

const settings = { maxAdults: 2, referenceCurrency: 'EUR', adultCount: 2, pricesEnabled: false, irlEnabled: false, bankingEnabled: false };
const view = {
  values: [
    { month: '2026-09', value: '103.07', publishedOn: '2026-10-15', fetchedOn: '2026-10-16', source: 'insee' },
    { month: '2026-08', value: '102.80', publishedOn: null, fetchedOn: '2026-09-15', source: 'manual' },
  ],
  runs: [{ id: 'r1', startedAt: '2026-10-19T07:00:00+00:00', finishedAt: '2026-10-19T07:00:02+00:00', status: 'succeeded', added: 2, replaced: 1, rejected: 0, message: null }],
};
const serve = (inflationEnabled = true) => server.use(
  http.get('*/api/v1/admin/household', () => HttpResponse.json({ ...settings, inflationEnabled })),
  http.get('*/api/v1/core/inflation/admin', () => HttpResponse.json(view)),
);

describe('Administration › Inflation', () => {
  it('lists the stored months, in the server order, and the runs', async () => {
    serve();
    const { container } = await renderApp('/admin/inflation', { capabilities: adminCapabilities });

    const month = await screen.findByRole('row', { name: /^septembre 2026/ });
    expect(month).toHaveTextContent('103.07');
    expect(month).toHaveTextContent('INSEE');
    expect(screen.getByRole('row', { name: /^août 2026/ })).toHaveTextContent('Saisi à la main');
    const rows = screen.getAllByRole('row').map((r) => r.textContent ?? '');
    expect(rows.findIndex((r) => r.startsWith('septembre 2026'))).toBeLessThan(rows.findIndex((r) => r.startsWith('août 2026')));
    expect(screen.getByRole('row', { name: /réussi/ })).toHaveTextContent(formatInstant(view.runs[0]!.startedAt));
    await expectNoAxeViolations(container);
  });

  it('opens the CPI series', async () => {
    serve();
    const { user, router } = await renderApp('/admin/inflation', { capabilities: adminCapabilities });

    await user.click(await screen.findByRole('link', { name: 'Voir la série' }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/series'));
    expect(router.state.location.search).toMatchObject({ s: 'cpi' });
  });

  it('turns the collection on from the switch', async () => {
    const sent: unknown[] = [];
    serve(false);
    server.use(http.patch('*/api/v1/admin/household', async ({ request }) => {
      sent.push(await request.json());
      return HttpResponse.json({ ...settings, inflationEnabled: true });
    }));
    const { user } = await renderApp('/admin/inflation', { capabilities: adminCapabilities });

    const toggle = await screen.findByRole('switch', { name: "Récupérer l'inflation" });
    expect(screen.getByRole('button', { name: 'Synchroniser maintenant' })).toBeDisabled();
    await user.click(toggle);
    await waitFor(() => expect(sent).toEqual([{ inflationEnabled: true }]));
  });

  it('asks for a collection', async () => {
    let calls = 0;
    serve();
    server.use(http.post('*/api/v1/core/inflation/sync', () => { calls += 1; return HttpResponse.json({ requested: true }, { status: 202 }); }));
    const { user } = await renderApp('/admin/inflation', { capabilities: adminCapabilities });

    await user.click(await screen.findByRole('button', { name: 'Synchroniser maintenant' }));
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));
    await waitFor(() => expect(toasts()).toHaveTextContent('Synchronisation demandée'));
    expect(calls).toBe(1);
  });

  it('saves a month by hand, and shows the server’s refusal on the value', async () => {
    const sent: { url: string; body: unknown }[] = [];
    serve();
    server.use(http.put('*/api/v1/core/inflation/:year/:month', async ({ request }) => {
      sent.push({ url: new URL(request.url).pathname, body: await request.json() });
      return sent.length === 1
        ? HttpResponse.json({ month: '2026-10', value: '103.40', publishedOn: null, fetchedOn: '2026-11-06', source: 'manual' })
        : HttpResponse.json({ status: 422, code: 'core.cpi_from_insee', title: 'x', errors: [] }, { status: 422 }); // the server sends no field for this one: it is the banner
    }));
    const { user } = await renderApp('/admin/inflation', { capabilities: adminCapabilities });

    const form = within(await screen.findByRole('form', { name: 'Saisir un mois' }));
    await pick(user, 'Année', '2026');
    await pick(user, 'Mois', 'octobre');
    await user.type(form.getByLabelText('Valeur'), '103,40');
    await user.click(form.getByRole('button', { name: 'Enregistrer' }));
    await waitFor(() => expect(sent).toEqual([{ url: '/api/v1/core/inflation/2026/10', body: { value: '103.40' } }]));
    await waitFor(() => expect(toasts()).toHaveTextContent('Enregistré'));

    await user.type(form.getByLabelText('Valeur'), '103,40');
    await user.click(form.getByRole('button', { name: 'Enregistrer' }));
    expect(await form.findByRole('alert')).toHaveTextContent('Ce mois vient de l’INSEE : il ne se saisit pas à la main.');
    expect(form.getByLabelText('Valeur')).not.toHaveAccessibleDescription(/\S/);
  });

  it('shows a month not over yet on the month field', async () => {
    serve();
    server.use(http.put('*/api/v1/core/inflation/:year/:month', () => HttpResponse.json(
      { status: 422, code: 'core.invalid_cpi_value', title: 'x', errors: [{ path: 'month', code: 'core.invalid_cpi_value', message: 'Octobre 2026 is not over yet.' }] }, { status: 422 })));
    const { user } = await renderApp('/admin/inflation', { capabilities: adminCapabilities });

    const form = within(await screen.findByRole('form', { name: 'Saisir un mois' }));
    await pick(user, 'Année', '2026');
    await pick(user, 'Mois', 'octobre');
    await user.type(form.getByLabelText('Valeur'), '103,40');
    await user.click(form.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(form.getByRole('combobox', { name: 'Mois' })).toHaveAccessibleDescription('Ce mois n’est pas encore terminé.'));
    expect(form.queryByRole('alert')).toBeNull();
  });

  it('asks for the year, the month and the value before saving', async () => {
    let calls = 0;
    serve();
    server.use(http.put('*/api/v1/core/inflation/:year/:month', () => { calls += 1; return HttpResponse.json({}); }));
    const { user } = await renderApp('/admin/inflation', { capabilities: adminCapabilities });

    const form = within(await screen.findByRole('form', { name: 'Saisir un mois' }));
    await user.click(form.getByRole('button', { name: 'Enregistrer' }));
    expect(form.getByLabelText('Valeur')).toHaveAccessibleDescription(/obligatoire/);
    expect(form.getByRole('combobox', { name: 'Mois' })).toHaveAccessibleDescription(/obligatoire/);
    expect(calls).toBe(0);
  });
});
