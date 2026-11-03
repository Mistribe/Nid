import { screen, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { adminCapabilities } from '@/test/fixtures';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { toasts } from '@/test/toasts';

const settings = { maxAdults: 2, referenceCurrency: 'EUR', adultCount: 2 };

describe('Administration › Foyer', () => {
  it('shows the cap and the currency, with the reconversion warning', async () => {
    server.use(http.get('*/api/v1/admin/household', () => HttpResponse.json(settings)));
    const { container } = await renderApp('/admin/household', { capabilities: adminCapabilities });

    expect(await screen.findByLabelText("Plafond d'adultes")).toHaveValue(2);
    expect(screen.getByLabelText('Devise de référence')).toHaveValue('EUR');
    expect(screen.getByText(/tous les totaux seront reconvertis/)).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('sends only what changed', async () => {
    const sent: unknown[] = [];
    server.use(
      http.get('*/api/v1/admin/household', () => HttpResponse.json(settings)),
      http.patch('*/api/v1/admin/household', async ({ request }) => {
        const body = (await request.json()) as Record<string, unknown>;
        sent.push(body);
        return HttpResponse.json({ ...settings, ...body });
      }),
    );
    const { user } = await renderApp('/admin/household', { capabilities: adminCapabilities });

    const cap = await screen.findByLabelText("Plafond d'adultes");
    await user.clear(cap);
    await user.type(cap, '4');
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(sent).toEqual([{ maxAdults: 4 }]));
    await waitFor(() => expect(toasts()).toHaveTextContent('Réglages enregistrés'));
  });

  it('shows the refusal the server answers', async () => {
    server.use(
      http.get('*/api/v1/admin/household', () => HttpResponse.json(settings)),
      http.patch('*/api/v1/admin/household', () => HttpResponse.json(
        { status: 422, code: 'core.unknown_currency', title: 'The reference currency is a three-letter ISO 4217 code, in capitals.' },
        { status: 422 })),
    );
    const { user } = await renderApp('/admin/household', { capabilities: adminCapabilities });

    // XYZ is three uppercase letters: Currency.TryCreate accepts it. EU1 is a value the real server refuses.
    const currency = await screen.findByLabelText('Devise de référence');
    await user.clear(currency);
    await user.type(currency, 'EU1');
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('three-letter ISO 4217 code');
  });
});
