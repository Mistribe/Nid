import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { persons } from './testData';

const LABELS = [
  { key: 'other', name: 'Other', color: '#9E9E9E' },
  { key: 'internet', name: 'Internet', color: '#1E88E5' },
];

function serve(create: Parameters<typeof http.post>[1], subscriptions: unknown[] = []) {
  server.use(
    http.get('*/api/v1/core/persons', () => HttpResponse.json(persons)),
    http.get('*/api/v1/subscriptions/providers', () => HttpResponse.json([])),
    http.get('*/api/v1/subscriptions/labels', () => HttpResponse.json(LABELS)),
    http.post('*/api/v1/subscriptions/providers', create),
    http.post('*/api/v1/subscriptions', async ({ request }) => {
      subscriptions.push(await request.json());
      return HttpResponse.json({ id: '0192f0aa-0000-7000-8000-0000000000c1' }, { status: 201 });
    }),
  );
}

async function openDialog(typed = 'Ma box') {
  const utils = await renderApp('/subscriptions/new');
  await screen.findByRole('heading', { level: 1, name: 'Ajouter un abonnement' });
  await screen.findByRole('radio', { name: /Récurrent/ }); // the step waits for the household's persons before it shows
  await utils.user.click(screen.getByRole('button', { name: 'Continuer' }));
  await screen.findByRole('group', { name: 'Propriétaires' });
  await utils.user.type(screen.getByLabelText('Fournisseur'), typed);
  await utils.user.click(await screen.findByRole('button', { name: `Créer « ${typed} »` }));
  const dialog = await screen.findByRole('dialog', { name: 'Nouveau fournisseur' });
  return { ...utils, dialog };
}

describe('Abonnements › nouveau fournisseur', () => {
  it('offers the creation under the results once a text is typed, even when nothing matches', async () => {
    serve(() => HttpResponse.json({}, { status: 500 }));
    const utils = await renderApp('/subscriptions/new');
    await screen.findByRole('heading', { level: 1, name: 'Ajouter un abonnement' });
    await screen.findByRole('radio', { name: /Récurrent/ }); // the step waits for the household's persons before it shows
    await utils.user.click(screen.getByRole('button', { name: 'Continuer' }));
    await screen.findByRole('group', { name: 'Propriétaires' });
    expect(screen.queryByRole('button', { name: /^Créer/ })).not.toBeInTheDocument();
    await utils.user.type(screen.getByLabelText('Fournisseur'), 'Ma box');
    expect(await screen.findByText('Aucun fournisseur trouvé.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Créer « Ma box »' })).toBeInTheDocument();
  });

  it('creates the provider, chooses it, never submits the wizard, and gives the focus back to the search', async () => {
    const sent: unknown[] = [];
    const subscriptions: unknown[] = [];
    serve(async ({ request }) => {
      sent.push(await request.json());
      return HttpResponse.json({ key: 'ma-box-x1y2z3', name: 'Ma box', origin: 'custom', labels: [LABELS[1]], website: null,
        archived: false, iconUrl: null }, { status: 201 });
    }, subscriptions);
    const { user, dialog, container } = await openDialog();

    expect(within(dialog).getByLabelText('Nom')).toHaveValue('Ma box');
    await expectNoAxeViolations(container);
    await user.click(within(dialog).getByRole('checkbox', { name: 'Internet' }));
    await user.type(within(dialog).getByLabelText('Site web'), 'https://ma-box.example');
    await user.click(within(dialog).getByRole('button', { name: 'Créer' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(sent).toEqual([{ name: 'Ma box', website: 'https://ma-box.example', pricingPage: null, labelKeys: ['internet'] }]);
    expect(screen.getByText('Fournisseur choisi : Ma box')).toBeInTheDocument();
    expect(screen.getByLabelText('Fournisseur')).toHaveFocus();
    expect(subscriptions).toEqual([]);
  });

  it('refuses a blank name, no label and a website without scheme under their fields, and sends nothing', async () => {
    const sent: unknown[] = [];
    serve(async ({ request }) => { sent.push(await request.json()); return HttpResponse.json({}, { status: 201 }); });
    const { user, dialog } = await openDialog();

    await user.clear(within(dialog).getByLabelText('Nom'));
    await user.type(within(dialog).getByLabelText('Nom'), '   ');
    await user.type(within(dialog).getByLabelText('Site web'), 'ma-box.example');
    await user.click(within(dialog).getByRole('button', { name: 'Créer' }));

    expect(await within(dialog).findByText('Donnez un nom au fournisseur.')).toBeInTheDocument();
    expect(within(dialog).getByText('Choisissez au moins un libellé.')).toBeInTheDocument();
    expect(within(dialog).getByText('Une adresse commençant par http:// ou https://.')).toBeInTheDocument();
    expect(within(dialog).getByLabelText('Nom')).toHaveAttribute('aria-invalid', 'true');
    expect(sent).toEqual([]);
  });

  it('shows a refusal from the server inside the dialog and keeps the fields', async () => {
    serve(() => HttpResponse.json({ status: 422, title: "'internet' is not a label.", code: 'subscriptions.invalid_provider' },
      { status: 422, headers: { 'Content-Type': 'application/problem+json' } }));
    const { user, dialog } = await openDialog();

    await user.click(within(dialog).getByRole('checkbox', { name: 'Internet' }));
    await user.click(within(dialog).getByRole('button', { name: 'Créer' }));

    expect(await within(dialog).findByRole('alert')).toBeInTheDocument();
    expect(within(dialog).getByLabelText('Nom')).toHaveValue('Ma box');
    expect(within(dialog).getByRole('checkbox', { name: 'Internet' })).toBeChecked();
  });

  it.each([['Annuler'], ['Escape']])('closes on %s without creating anything, the focus back in the search', async (how) => {
    const sent: unknown[] = [];
    serve(async ({ request }) => { sent.push(await request.json()); return HttpResponse.json({}, { status: 201 }); });
    const { user, dialog } = await openDialog();

    if (how === 'Escape') await user.keyboard('{Escape}');
    else await user.click(within(dialog).getByRole('button', { name: 'Annuler' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(sent).toEqual([]);
    expect(screen.getByLabelText('Fournisseur')).toHaveFocus();
    expect(screen.queryByText(/Fournisseur choisi/)).not.toBeInTheDocument();
  });
});
