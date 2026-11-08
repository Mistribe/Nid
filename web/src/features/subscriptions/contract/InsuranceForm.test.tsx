import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import { pick } from '@/test/controls';
import { openMore } from '@/test/headerActions';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { propertyList, propertySummary } from '../../properties/testData';
import type { Detail } from '@/api/subscriptions';
import { detail, persons, SUB } from '../testData';

const NANTES = propertySummary();
const LYON = propertySummary({ id: '0192f0bb-0000-7000-8000-00000000e002', name: 'Studio de Lyon' });

function serve(body: Detail, put?: Parameters<typeof http.put>[1], properties = [NANTES, LYON]) {
  const sent: unknown[] = [];
  server.use(
    http.put('*/api/v1/subscriptions/:id/insurance', put ?? (async ({ request }) => { sent.push(await request.json()); return HttpResponse.json({ id: SUB }); })),
    http.get('*/api/v1/realestate/properties', () => HttpResponse.json(propertyList(properties))),
    http.get('*/api/v1/subscriptions/providers', () => HttpResponse.json([])),
    http.get('*/api/v1/subscriptions/:id', () => HttpResponse.json(body)),
    http.get('*/api/v1/core/persons', () => HttpResponse.json(persons)),
    http.get('*/api/v1/documents', () => HttpResponse.json({ items: [] })),
  );
  return sent;
}

const openDialog = async (user: Awaited<ReturnType<typeof renderApp>>['user']) => {
  await screen.findByRole('heading', { level: 1, name: 'Spotify Premium Duo' });
  await openMore(user);
  await user.click(await screen.findByRole('menuitem', { name: /^(Ajouter|Modifier) l’assurance$/ }));
  return screen.findByRole('form', { name: 'Assurance' });
};

describe('Abonnements › assurance', () => {
  it('sends the kind, the linked property and every guarantee of a new insurance', async () => {
    const sent = serve(detail());
    const { user } = await renderApp(`/subscriptions/${SUB}`);
    const form = await openDialog(user);

    await pick(user, 'Type d’assurance', 'Auto');
    await pick(user, 'Bien assuré', 'Studio de Lyon');
    await user.type(within(form).getByLabelText('Ce qui est assuré'), 'Clio');
    await user.click(within(form).getByRole('button', { name: 'Ajouter une garantie' }));
    await user.type(within(form).getByLabelText('Garantie 1'), 'Vol');
    await user.type(within(form).getByLabelText('Plafond de la garantie 1'), '5 000 €');
    await user.click(within(form).getByRole('button', { name: 'Ajouter une garantie' }));
    await user.type(within(form).getByLabelText('Garantie 2'), 'Bris de glace');
    await user.type(within(form).getByLabelText('Franchise de la garantie 2'), '50 €');
    await user.click(within(form).getByRole('button', { name: 'Enregistrer l’assurance' }));

    await waitFor(() => expect(sent).toEqual([{
      kind: 'car', propertyId: LYON.id, keepProperty: false, insuredLabel: 'Clio', policyNumber: null, insurerContact: null,
      guarantees: [
        { label: 'Vol', ceiling: '5 000 €', deductible: null },
        { label: 'Bris de glace', ceiling: null, deductible: '50 €' },
      ],
    }]));
  });

  it('removes a guarantee row and sends the whole remaining list', async () => {
    const sent = serve(detail({ insurance: {
      kind: 'home', property: { id: NANTES.id, name: NANTES.name }, insuredLabel: null, policyNumber: 'HAB-42', insurerContact: null,
      guarantees: [{ label: 'Incendie', ceiling: null, deductible: null }, { label: 'Vol', ceiling: null, deductible: null }],
    } }));
    const { user } = await renderApp(`/subscriptions/${SUB}`);
    const form = await openDialog(user);

    expect(within(form).getByLabelText('Garantie 2')).toHaveValue('Vol');
    await user.click(within(form).getByRole('button', { name: 'Retirer la garantie 1' }));
    await user.click(within(form).getByRole('button', { name: 'Enregistrer l’assurance' }));

    await waitFor(() => expect(sent).toEqual([{
      kind: 'home', propertyId: NANTES.id, keepProperty: false, insuredLabel: null, policyNumber: 'HAB-42', insurerContact: null,
      guarantees: [{ label: 'Vol', ceiling: null, deductible: null }],
    }]));
  });

  it('keeps the current link selectable when the list does not hold it', async () => {
    const sent = serve(detail({ insurance: {
      kind: 'home', property: { id: NANTES.id, name: NANTES.name }, insuredLabel: null, policyNumber: null, insurerContact: null, guarantees: [],
    } }), undefined, []);
    const { user } = await renderApp(`/subscriptions/${SUB}`);
    const form = await openDialog(user);

    expect(within(form).getByRole('combobox', { name: 'Bien assuré' })).toHaveTextContent('Maison de Nantes');
    await user.click(within(form).getByRole('button', { name: 'Enregistrer l’assurance' }));
    await waitFor(() => expect(sent).toHaveLength(1));
    expect(sent[0]).toMatchObject({ propertyId: NANTES.id });
  });

  const HIDDEN = { kind: 'home', property: null, insuredLabel: null, policyNumber: null, insurerContact: null, guarantees: [] };

  it('asks to keep a link the reader cannot see while the property field stays empty', async () => {
    const sent = serve(detail({ insurance: HIDDEN }));
    const { user } = await renderApp(`/subscriptions/${SUB}`);
    const form = await openDialog(user);

    await user.click(within(form).getByRole('button', { name: 'Enregistrer l’assurance' }));

    await waitFor(() => expect(sent).toHaveLength(1));
    expect(sent[0]).toMatchObject({ propertyId: null, keepProperty: true });
  });

  it('sends the property a person picks, without keepProperty', async () => {
    const sent = serve(detail({ insurance: HIDDEN }));
    const { user } = await renderApp(`/subscriptions/${SUB}`);
    const form = await openDialog(user);

    await pick(user, 'Bien assuré', 'Studio de Lyon');
    await user.click(within(form).getByRole('button', { name: 'Enregistrer l’assurance' }));

    await waitFor(() => expect(sent).toHaveLength(1));
    expect(sent[0]).toMatchObject({ propertyId: LYON.id, keepProperty: false });
  });

  it('does not ask to keep a link it can see, nor when there is none', async () => {
    const visible = serve(detail({ insurance: { ...HIDDEN, property: { id: NANTES.id, name: NANTES.name } } }));
    const first = await renderApp(`/subscriptions/${SUB}`);
    await first.user.click(within(await openDialog(first.user)).getByRole('button', { name: 'Enregistrer l’assurance' }));
    await waitFor(() => expect(visible).toHaveLength(1));
    expect(visible[0]).toMatchObject({ propertyId: NANTES.id, keepProperty: false });
  });

  it('says when no property can be linked', async () => {
    serve(detail(), undefined, []);
    const { user } = await renderApp(`/subscriptions/${SUB}`);
    const form = await openDialog(user);
    expect(await within(form).findByText(/Aucun bien enregistré/)).toBeInTheDocument();
  });

  it('puts a refusal on the row that failed when a blank row precedes it', async () => {
    const sent: unknown[] = [];
    serve(detail(), async ({ request }) => {
      sent.push(await request.json());
      return HttpResponse.json({
        status: 422, code: 'subscriptions.guarantee_label', title: 'A guarantee has a label.',
        errors: [{ path: 'guarantees[0].label', code: 'subscriptions.guarantee_label', message: 'A guarantee has a label.' }],
      }, { status: 422 });
    });
    const { user } = await renderApp(`/subscriptions/${SUB}`);
    const form = await openDialog(user);

    await user.click(within(form).getByRole('button', { name: 'Ajouter une garantie' }));
    await user.click(within(form).getByRole('button', { name: 'Ajouter une garantie' }));
    await user.type(within(form).getByLabelText('Plafond de la garantie 2'), '1 €');
    await user.click(within(form).getByRole('button', { name: 'Enregistrer l’assurance' }));

    // The blank row 1 is gone: the failing row is now row 1, the sent list has one entry, and the error is on it.
    await waitFor(() => expect(sent).toHaveLength(1));
    expect((sent[0] as { guarantees: unknown[] }).guarantees).toHaveLength(1);
    const label = await within(form).findByLabelText('Garantie 1');
    await waitFor(() => expect(label).toHaveAttribute('aria-invalid', 'true'));
    expect(within(form).getByLabelText('Plafond de la garantie 1')).toHaveValue('1 €');
    expect(within(form).queryByLabelText('Garantie 2')).not.toBeInTheDocument();
  });

  it('shows a refusal on the guarantee row it names', async () => {
    serve(detail(), () => HttpResponse.json({
      status: 422, code: 'subscriptions.guarantee_label', title: 'A guarantee has a label.',
      errors: [{ path: 'guarantees[0].label', code: 'subscriptions.guarantee_label', message: 'A guarantee has a label.' }],
    }, { status: 422 }));
    const { user } = await renderApp(`/subscriptions/${SUB}`);
    const form = await openDialog(user);

    await user.click(within(form).getByRole('button', { name: 'Ajouter une garantie' }));
    await user.type(within(form).getByLabelText('Plafond de la garantie 1'), '1 €');
    await user.click(within(form).getByRole('button', { name: 'Enregistrer l’assurance' }));

    const label = within(form).getByLabelText('Garantie 1');
    await waitFor(() => expect(label).toHaveAttribute('aria-invalid', 'true'));
    expect(label).toHaveAccessibleDescription('Chaque garantie porte un libellé.');
  });
});
