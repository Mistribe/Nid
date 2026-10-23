import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import type { components } from '@/api/schema';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { PropertyDetail } from '../api';
import { PROPERTY, propertyDetail } from '../testData';

type Body = components['schemas']['EditPropertyBody'];
/** An amount as the server writes it back: two decimals. */
const written = (wire: string) => { const [whole, decimals = ''] = wire.split('.'); return `${whole}.${decimals.padEnd(2, '0')}`; };

/**
 * As the server: a stale version is `409 realestate.property_conflict`; the version moves when a field other than the name
 * changes (a rename is the Core item's, spec 3C1).
 */
function serve(initial: PropertyDetail = propertyDetail()) {
  let current = initial;
  const reads = { detail: 0 };
  const sent: Body[] = [];
  server.use(
    http.get('*/api/v1/realestate/properties/:id', () => { reads.detail += 1; return HttpResponse.json(current); }),
    http.patch('*/api/v1/realestate/properties/:id', async ({ request }) => {
      const body = (await request.json()) as Body;
      sent.push(body);
      if (body.version !== current.version)
        return HttpResponse.json({ status: 409, code: 'realestate.property_conflict', title: 'Changed.' }, { status: 409 });
      const fields = Object.entries(body).some(([k, v]) => k !== 'version' && k !== 'name' && v !== false);
      current = {
        ...current,
        name: body.name ?? current.name,
        purchasePrice: body.purchasePrice ? written(body.purchasePrice) : current.purchasePrice,
        locationLabel: body.clearLocationLabel ? null : (body.locationLabel ?? current.locationLabel),
        rentalStartsOn: body.clearRentalStartsOn ? null : (body.rentalStartsOn ?? current.rentalStartsOn),
        note: body.clearNote ? null : (body.note ?? current.note),
        postalAddress: body.clearPostalAddress ? null : (body.postalAddress ?? current.postalAddress),
        version: fields ? current.version + 1 : current.version,
      };
      return HttpResponse.json({ id: current.id });
    }),
  );
  return { sent, reads, change: (over: Partial<PropertyDetail>) => { current = { ...current, ...over }; } };
}

const settings = () => screen.getByRole('form', { name: 'Réglages' });
const settingsRegion = () => screen.getByRole('region', { name: 'Réglages' });
const save = () => within(settings()).getByRole('button', { name: 'Enregistrer les réglages' });
const heading = (name = 'Maison de Nantes') => screen.findByRole('heading', { name, level: 1 });
const FLAGS = { clearSurfaceM2: false, clearLocationLabel: false, clearStaleAfterDays: false, clearRentalStartsOn: false, clearNote: false, clearEnergyClass: false, clearPostalAddress: false };

describe('property settings', () => {
  beforeAll(async () => { await import('../PropertyDetailPage'); }, 30_000);

  it('sends only what changed with the version it opened on, says it is saved and shows the stored property', async () => {
    const { sent, reads } = serve();
    const { user, container } = await renderApp(`/properties/${PROPERTY}?tab=settings`);

    await heading();
    await user.clear(within(settings()).getByLabelText('Prix d’achat'));
    await user.type(within(settings()).getByLabelText('Prix d’achat'), '255000');
    await user.clear(within(settings()).getByLabelText('Commune ou repère'));
    await user.type(within(settings()).getByLabelText('Note'), 'toiture refaite');
    await expectNoAxeViolations(container);
    await user.click(save());

    await waitFor(() => expect(within(settingsRegion()).getByRole('status')).toHaveTextContent('Réglages enregistrés.'));
    await waitFor(() => {
      expect(sent).toEqual([{ ...FLAGS, version: 7, purchasePrice: '255000', clearLocationLabel: true, note: 'toiture refaite' }]);
      expect(reads.detail).toBeGreaterThan(1);
      expect(within(settings()).getByLabelText('Prix d’achat')).toHaveValue('255000.00');
      expect(screen.getByRole('heading', { name: 'Réglages' })).toHaveFocus();
    });
  });

  it('sends the postal address typed, then its clearing once emptied', async () => {
    const { sent } = serve();
    const { user } = await renderApp(`/properties/${PROPERTY}?tab=settings`);

    await heading();
    await user.type(within(settings()).getByLabelText('Adresse postale'), '12 rue des Lilas');
    await user.click(save());
    await waitFor(() => expect(sent).toEqual([{ ...FLAGS, postalAddress: '12 rue des Lilas', version: 7 }]));
    await waitFor(() => expect(within(settings()).getByLabelText('Adresse postale')).toHaveValue('12 rue des Lilas'));

    await user.clear(within(settings()).getByLabelText('Adresse postale'));
    await user.click(save());
    await waitFor(() => expect(sent[1]).toEqual({ ...FLAGS, clearPostalAddress: true, version: 8 }));
  });

  it('says there is nothing to save rather than sending an empty change', async () => {
    const { sent } = serve();
    const { user } = await renderApp(`/properties/${PROPERTY}?tab=settings`);

    await heading();
    await user.click(save());

    await waitFor(() => expect(within(settingsRegion()).getByRole('status')).toHaveTextContent('Aucune modification à enregistrer.'));
    expect(sent).toEqual([]);
  });

  it('saves twice in a row, the second time with the version the first produced', async () => {
    const { sent } = serve();
    const { user } = await renderApp(`/properties/${PROPERTY}?tab=settings`);

    await heading();
    await user.type(within(settings()).getByLabelText('Note'), 'agrandie');
    await user.click(save());
    await waitFor(() => expect(within(settingsRegion()).getByRole('status')).toHaveTextContent('Réglages enregistrés.'));
    await user.clear(within(settings()).getByLabelText('Nom'));
    await user.type(within(settings()).getByLabelText('Nom'), 'Maison bleue');
    await user.click(save());

    expect(await heading('Maison bleue')).toBeInTheDocument();
    await waitFor(() => expect(sent.map((b) => b.version)).toEqual([7, 8]));
    expect(within(settings()).queryByRole('alert')).not.toBeInTheDocument();
  });

  it('sends the version it opened on though the property was read again meanwhile, says the conflict, and « Recharger » shows what is stored', async () => {
    const { sent, change, reads } = serve();
    const { user, queryClient } = await renderApp(`/properties/${PROPERTY}?tab=settings`);

    await heading();
    await user.type(within(settings()).getByLabelText('Note'), 'ma note');
    change({ version: 8, note: 'changée ailleurs' });
    await queryClient.invalidateQueries();
    await waitFor(() => expect(reads.detail).toBeGreaterThan(1));
    expect(within(settings()).getByLabelText('Note')).toHaveValue('ma note');
    await user.click(save());

    expect(await within(settings()).findByText('Ce bien a changé entre-temps : rechargez la page.')).toBeInTheDocument();
    await waitFor(() => expect(sent.map((b) => b.version)).toEqual([7]));
    await user.click(within(settings()).getByRole('button', { name: 'Recharger' }));

    await waitFor(() => expect(within(settings()).getByLabelText('Note')).toHaveValue('changée ailleurs'));
    expect(within(settings()).queryByRole('alert')).not.toBeInTheDocument();
  });

  it('shows a server refusal under the field it names', async () => {
    serve();
    server.use(http.patch('*/api/v1/realestate/properties/:id', () => HttpResponse.json(
      { status: 422, code: 'realestate.invalid_property_field', title: 'x', errors: [{ path: 'purchaseDate', code: 'realestate.invalid_property_field', message: 'x' }] },
      { status: 422 })));
    const { user } = await renderApp(`/properties/${PROPERTY}?tab=settings`);

    await heading();
    await user.clear(within(settings()).getByLabelText('Date d’achat'));
    await user.type(within(settings()).getByLabelText('Date d’achat'), '2021-01-10');
    await user.click(save());

    expect(await within(settings()).findByText('Cette valeur n’est pas acceptée.')).toBeInTheDocument();
    expect(within(settings()).getByLabelText('Date d’achat')).toHaveAttribute('aria-invalid', 'true');
    expect(within(settings()).queryByRole('alert')).not.toBeInTheDocument();
  });

  it('sends the rental start typed, then its clearing once emptied, each with the version it opened on', async () => {
    const { sent } = serve();
    const { user, container } = await renderApp(`/properties/${PROPERTY}?tab=settings`);

    await heading();
    expect(within(settings()).getByText('Date de mise en location, si elle précède le premier bail.')).toBeInTheDocument();
    await user.type(within(settings()).getByLabelText('Début de location'), '01/01/2021');
    await expectNoAxeViolations(container);
    await user.click(save());

    await waitFor(() => expect(within(settingsRegion()).getByRole('status')).toHaveTextContent('Réglages enregistrés.'));
    await waitFor(() => expect(within(settings()).getByLabelText('Début de location')).toHaveValue('01/01/2021'));
    await user.clear(within(settings()).getByLabelText('Début de location'));
    await user.click(save());

    await waitFor(() => expect(sent).toEqual([
      { ...FLAGS, version: 7, rentalStartsOn: '2021-01-01' },
      { ...FLAGS, version: 8, clearRentalStartsOn: true },
    ]));
    await waitFor(() => expect(within(settings()).getByLabelText('Début de location')).toHaveValue(''));
    await expectNoAxeViolations(container);
  });

  it('shows a rental start the server refuses (before the purchase) under « Début de location »', async () => {
    serve();
    server.use(http.patch('*/api/v1/realestate/properties/:id', () => HttpResponse.json(
      { status: 422, code: 'realestate.invalid_property_field', title: 'x', errors: [{ path: 'rentalStartsOn', code: 'realestate.invalid_property_field', message: 'x' }] },
      { status: 422 })));
    const { user } = await renderApp(`/properties/${PROPERTY}?tab=settings`);

    await heading();
    await user.type(within(settings()).getByLabelText('Début de location'), '01/01/2019');
    await user.click(save());

    await waitFor(() => {
      expect(within(settings()).getByText('Cette valeur n’est pas acceptée.')).toBeInTheDocument();
      expect(within(settings()).getByLabelText('Début de location')).toHaveAttribute('aria-invalid', 'true');
      expect(within(settings()).queryByRole('alert')).not.toBeInTheDocument();
    });
  });

  it('refuses an empty name before sending', async () => {
    const { sent } = serve();
    const { user } = await renderApp(`/properties/${PROPERTY}?tab=settings`);

    await heading();
    await user.clear(within(settings()).getByLabelText('Nom'));
    await user.click(save());

    expect(await within(settings()).findByText('Ce champ est obligatoire.')).toBeInTheDocument();
    expect(sent).toEqual([]);
  });

  it('gives a reader no settings', async () => {
    serve(propertyDetail({ canWrite: false }));
    await renderApp(`/properties/${PROPERTY}?tab=settings`);

    await heading();
    expect(screen.queryByRole('region', { name: 'Réglages' })).not.toBeInTheDocument();
  });
});
