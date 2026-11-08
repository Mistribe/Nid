import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { adminCapabilities } from '@/test/fixtures';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { toasts } from '@/test/toasts';
import { formatInstant } from '@/lib/format';

const settings = { maxAdults: 2, referenceCurrency: 'EUR', adultCount: 2, pricesEnabled: false };
const view = {
  values: [
    { quarter: '2026-Q2', value: '148.37', publishedOn: '2026-07-12', fetchedOn: '2026-07-13', source: 'insee' },
    { quarter: '2026-Q1', value: '146.80', publishedOn: null, fetchedOn: '2026-04-20', source: 'manual' },
  ],
  runs: [{ id: 'r1', startedAt: '2026-10-19T07:00:00+00:00', finishedAt: '2026-10-19T07:00:02+00:00', status: 'succeeded', added: 2, replaced: 1, rejected: 0, message: null }],
};
const serve = (irlEnabled = true) => server.use(
  http.get('*/api/v1/admin/household', () => HttpResponse.json({ ...settings, irlEnabled })),
  http.get('*/api/v1/realestate/irl', () => HttpResponse.json(view)),
);

describe('Administration › IRL', () => {
  it('lists the stored quarters and the runs', async () => {
    serve();
    const { container } = await renderApp('/admin/irl', { capabilities: adminCapabilities });

    const quarter = await screen.findByRole('row', { name: /2026-T2/ });
    expect(quarter).toHaveTextContent('148.37');
    expect(quarter).toHaveTextContent('INSEE');
    expect(screen.getByRole('row', { name: /2026-T1/ })).toHaveTextContent('Saisi à la main');
    expect(screen.getByRole('row', { name: /réussi/ })).toHaveTextContent(formatInstant(view.runs[0]!.startedAt));
    await expectNoAxeViolations(container);
  });

  it('turns the collection on from the switch', async () => {
    const sent: unknown[] = [];
    serve(false);
    server.use(http.patch('*/api/v1/admin/household', async ({ request }) => {
      sent.push(await request.json());
      return HttpResponse.json({ ...settings, irlEnabled: true });
    }));
    const { user } = await renderApp('/admin/irl', { capabilities: adminCapabilities });

    const toggle = await screen.findByRole('switch', { name: "Récupérer l'IRL" });
    expect(screen.getByRole('button', { name: 'Mettre à jour maintenant' })).toBeDisabled();
    await user.click(toggle);
    await waitFor(() => expect(sent).toEqual([{ irlEnabled: true }]));
  });

  it('asks for a collection', async () => {
    let calls = 0;
    serve();
    server.use(http.post('*/api/v1/realestate/irl/sync', () => { calls += 1; return HttpResponse.json({ requested: true }, { status: 202 }); }));
    const { user } = await renderApp('/admin/irl', { capabilities: adminCapabilities });

    await user.click(await screen.findByRole('button', { name: 'Mettre à jour maintenant' }));
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));
    await waitFor(() => expect(toasts()).toHaveTextContent('Mise à jour demandée'));
    expect(calls).toBe(1);
  });

  it('saves a quarter by hand, and shows the server’s refusal on the value', async () => {
    const sent: { url: string; body: unknown }[] = [];
    serve();
    server.use(http.put('*/api/v1/realestate/irl/:year/:quarter', async ({ request }) => {
      sent.push({ url: new URL(request.url).pathname, body: await request.json() });
      return sent.length === 1
        ? HttpResponse.json({ quarter: '2026-Q3', value: '149.10', publishedOn: '2026-10-15', fetchedOn: '2026-10-23', source: 'manual' })
        : HttpResponse.json({ status: 422, code: 'realestate.irl_from_insee', title: 'x', errors: [{ path: 'value', code: 'realestate.irl_from_insee', message: 'x' }] }, { status: 422 });
    }));
    const { user } = await renderApp('/admin/irl', { capabilities: adminCapabilities });

    const form = within(await screen.findByRole('form', { name: 'Saisir un trimestre' }));
    await user.type(form.getByLabelText('Année'), '2026');
    await user.click(form.getByRole('radio', { name: 'T3' }));
    await user.type(form.getByLabelText('Valeur'), '149,10');
    await user.type(form.getByLabelText('Date de parution (JO)'), '15/10/2026');
    await user.click(form.getByRole('button', { name: 'Enregistrer' }));
    await waitFor(() => expect(sent).toEqual([{ url: '/api/v1/realestate/irl/2026/3', body: { value: '149.10', publishedOn: '2026-10-15' } }]));
    await waitFor(() => expect(toasts()).toHaveTextContent('Enregistré'));

    await user.type(form.getByLabelText('Valeur'), '149,10');
    await user.click(form.getByRole('button', { name: 'Enregistrer' }));
    expect(await form.findByText(/INSEE/, { selector: 'span' })).toBeInTheDocument();
  });

  it('asks for the Journal officiel date before saving', async () => {
    let calls = 0;
    serve();
    server.use(http.put('*/api/v1/realestate/irl/:year/:quarter', () => { calls += 1; return HttpResponse.json({}); }));
    const { user } = await renderApp('/admin/irl', { capabilities: adminCapabilities });

    const form = within(await screen.findByRole('form', { name: 'Saisir un trimestre' }));
    await user.type(form.getByLabelText('Année'), '2026');
    await user.type(form.getByLabelText('Valeur'), '149,10');
    await user.click(form.getByRole('button', { name: 'Enregistrer' }));
    expect(form.getByLabelText('Date de parution (JO)')).toHaveAccessibleDescription(/obligatoire/);
    expect(calls).toBe(0);
  });
});
