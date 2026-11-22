import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { formatInstantDate } from '@/lib/format';

const loadedAt = '2026-11-02T10:00:00+00:00';
const declared = { applicationId: 'app-alice', keyStatus: 'loaded', keyLoadedAt: loadedAt, inUse: false };
const PEM = '-----BEGIN PRIVATE KEY-----\nQUJD\n-----END PRIVATE KEY-----';
const REDIRECT = 'https://nid.example/api/v1/banking/callback';

/** Banking on but not configured for the installation: the very case where an adult needs their own application. */
function serve(initial: object | null, over: { remove?: () => Response; test?: object } = {}) {
  let mine = initial;
  const writes: [string, unknown][] = [];
  server.use(
    http.get('*/api/v1/banking/status', () => HttpResponse.json({ enabled: true, configured: false, redirectUrl: REDIRECT })),
    http.get('*/api/v1/banking/accounts', () => HttpResponse.json([])),
    http.get('*/api/v1/banking/category-rules', () => HttpResponse.json([])),
    http.get('*/api/v1/banking/application', () => (mine ? HttpResponse.json(mine) : new HttpResponse(null, { status: 204 }))),
    http.put('*/api/v1/banking/application', async ({ request }) => {
      const body = await request.json() as { applicationId: string };
      writes.push(['PUT', body]);
      mine = { ...declared, applicationId: body.applicationId };
      return HttpResponse.json(mine);
    }),
    http.delete('*/api/v1/banking/application', () => {
      writes.push(['DELETE', null]);
      return over.remove ? over.remove() : (mine = null, new HttpResponse(null, { status: 204 }));
    }),
    http.post('*/api/v1/banking/application/test', () => HttpResponse.json(over.test ?? { outcome: 'accepted', active: true, redirectUrlAllowed: true })),
  );
  return writes;
}

const open = (step?: number) => renderApp(`/settings/banks?tab=application${step ? `&step=${step}` : ''}`);
const region = () => screen.findByRole('region', { name: 'Mon application Enable Banking' });

describe('Réglages › Banques › Mon application', () => {
  beforeAll(async () => { await import('./BanksPage'); await import('./ApplicationTab'); }, 30_000);

  it('opens the wizard at its first step when there is no application, even with banking not configured', async () => {
    serve(null);
    const { container } = await open();

    expect(await screen.findByText(/Nid lit vos soldes et vos opérations, en lecture seule/)).toBeInTheDocument();
    expect(screen.getByText('Étape 1 sur 9')).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('keeps the step reached in the address, so the adult comes back to it from the portal', async () => {
    serve(null);
    const { user, router } = await open();

    await user.click(await screen.findByRole('button', { name: 'Continuer' }));

    expect(await screen.findByRole('link', { name: 'Ouvrir le portail Enable Banking' })).toHaveAttribute('href', 'https://enablebanking.com/cp/applications');
    await waitFor(() => expect(router.state.location.search).toEqual({ tab: 'application', step: 2 }));
  });

  it('gives the redirect, privacy and terms addresses to copy', async () => {
    serve(null);
    await open(4);

    expect(await screen.findByRole('textbox', { name: /Adresse de retour/ })).toHaveValue(REDIRECT);
    expect(screen.getByRole('textbox', { name: /Politique de confidentialité/ })).toHaveValue('https://github.com/Mistribe/Nid/blob/main/PRIVACY.md');
    expect(screen.getByRole('textbox', { name: /Conditions d’utilisation/ })).toHaveValue('https://github.com/Mistribe/Nid/blob/main/LICENSE');
    expect(screen.getAllByRole('button', { name: /^Copier/ })).toHaveLength(3);
  });

  it('declares the application at its step, then goes on to the test', async () => {
    const writes = serve(null);
    const { user } = await open(7);

    await user.type(await screen.findByRole('textbox', { name: /Identifiant/ }), 'app-alice');
    await user.click(screen.getByRole('textbox', { name: /Clé privée/ }));
    await user.paste(PEM);
    await user.click(screen.getByRole('button', { name: 'Continuer' }));

    await waitFor(() => expect(writes).toEqual([['PUT', { applicationId: 'app-alice', privateKey: PEM }]]));
    expect(await screen.findByText('Étape 8 sur 9')).toBeInTheDocument();
    expect(screen.queryByText(PEM)).toBeNull();
  });

  it('says a refused key under its field and stays on the step', async () => {
    serve(null);
    server.use(http.put('*/api/v1/banking/application', () => HttpResponse.json(
      { status: 422, code: 'banking.key_invalid', detail: 'Bad.', errors: [{ path: 'privateKey', code: 'banking.key_invalid', message: 'Bad.' }] }, { status: 422 })));
    const { user } = await open(7);

    await user.type(await screen.findByRole('textbox', { name: /Identifiant/ }), 'app-alice');
    await user.click(screen.getByRole('textbox', { name: /Clé privée/ }));
    await user.paste('nope');
    await user.click(screen.getByRole('button', { name: 'Continuer' }));

    expect(await screen.findByText(/clé n’est pas une clé privée valide/)).toBeInTheDocument();
    expect(screen.getByText('Étape 7 sur 9')).toBeInTheDocument();
  });

  it('sends back to the step that fixes what the test found', async () => {
    serve(declared, { test: { outcome: 'accepted', active: true, redirectUrlAllowed: false } });
    const { user } = await open(8);

    await user.click(await screen.findByRole('button', { name: 'Tester la connexion' }));
    await user.click(await screen.findByRole('button', { name: 'Revenir à l’étape 4 : Renseigner les adresses' }));

    expect(await screen.findByRole('textbox', { name: /Adresse de retour/ })).toHaveValue(REDIRECT);
  });

  it('ends on the summary once the bank step is done', async () => {
    serve(declared);
    const { user } = await open(9);

    expect(await screen.findByRole('button', { name: 'Relier une banque' })).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Terminer' }));

    expect(await screen.findByText(`Clé chargée le ${formatInstantDate(loadedAt)}`)).toBeInTheDocument();
  });

  it('warns about an unreadable key', async () => {
    serve({ ...declared, keyStatus: 'unreadable' });
    await open();

    expect(await screen.findByText('Clé illisible : envoyez-la à nouveau')).toBeInTheDocument();
  });

  it('replaces the key with the identifier prefilled and the key field empty', async () => {
    const writes = serve(declared);
    const { user } = await open();

    await user.click(await screen.findByRole('button', { name: 'Actions sur mon application' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Remplacer la clé' }));
    const dialog = await screen.findByRole('dialog', { name: 'Remplacer la clé' });
    expect(within(dialog).getByRole('textbox', { name: /Identifiant/ })).toHaveValue('app-alice');
    expect(within(dialog).getByRole('textbox', { name: /Clé privée/ })).toHaveValue('');
    await user.click(within(dialog).getByRole('textbox', { name: /Clé privée/ }));
    await user.paste(PEM);
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(writes).toEqual([['PUT', { applicationId: 'app-alice', privateKey: PEM }]]));
  });

  it('removes the application after a confirmation', async () => {
    const writes = serve(declared);
    const { user } = await open();

    await user.click(await screen.findByRole('button', { name: 'Actions sur mon application' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Retirer' }));
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));

    await waitFor(() => expect(writes).toEqual([['DELETE', null]]));
    expect(await screen.findByText('Étape 1 sur 9')).toBeInTheDocument();
  });

  it('tells to remove the linked banks first when the application is in use', async () => {
    serve({ ...declared, inUse: true }, {
      remove: () => HttpResponse.json({ status: 409, code: 'banking.application_in_use', detail: 'In use.' }, { status: 409 }),
    });
    const { user } = await open();

    await user.click(await screen.findByRole('button', { name: 'Actions sur mon application' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Retirer' }));
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));

    expect(await screen.findByText('Supprimez d’abord les banques reliées avec cette application.')).toBeInTheDocument();
  });

  it('tests the connection and says it is accepted', async () => {
    serve(declared);
    const { user } = await open();

    await user.click(await screen.findByRole('button', { name: 'Tester la connexion' }));

    expect(await screen.findByText('Enable Banking accepte l’identifiant et la clé.')).toBeInTheDocument();
  });

  it('refuses a key file over 16 KiB before sending it', async () => {
    const writes = serve(null);
    const { user } = await open(7);

    const dialog = await region();
    await user.upload(within(dialog).getByLabelText('Choisir le fichier de la clé'), new File(['x'.repeat(16 * 1024 + 1)], 'big.pem'));

    expect(await within(dialog).findByText('Le fichier dépasse 16 Kio : ce n’est pas une clé privée.')).toBeInTheDocument();
    expect(within(dialog).getByRole('textbox', { name: /Clé privée/ })).toHaveValue('');
    expect(writes).toEqual([]);
  });

  it('reads a key file into the field', async () => {
    serve(null);
    const { user } = await open(7);

    const dialog = await region();
    await user.upload(within(dialog).getByLabelText('Choisir le fichier de la clé'), new File([PEM], 'key.pem'));

    await waitFor(() => expect(within(dialog).getByRole('textbox', { name: /Clé privée/ })).toHaveValue(PEM));
  });

  it('words a 413 and a 415 from the server', async () => {
    serve(null);
    server.use(http.put('*/api/v1/banking/application', () => HttpResponse.json({ status: 413 }, { status: 413 })));
    const { user } = await open(7);

    const dialog = await region();
    await user.type(within(dialog).getByRole('textbox', { name: /Identifiant/ }), 'app-alice');
    await user.click(within(dialog).getByRole('button', { name: 'Continuer' }));
    expect(await within(dialog).findByText('Le fichier dépasse 16 Kio : ce n’est pas une clé privée.')).toBeInTheDocument();

    server.use(http.put('*/api/v1/banking/application', () => HttpResponse.json({ status: 415 }, { status: 415 })));
    await user.click(within(dialog).getByRole('button', { name: 'Continuer' }));
    expect(await within(dialog).findByText('Envoi invalide, réessayez.')).toBeInTheDocument();
  });
});
