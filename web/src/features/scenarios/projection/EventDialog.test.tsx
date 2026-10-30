import { createMemoryHistory, createRootRoute, createRouter, RouterProvider } from '@tanstack/react-router';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createApi } from '@/api/client';
import { createQueryClient } from '@/app/bootstrap';
import { Providers } from '@/app/Providers';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { aliceSession, allCapabilities } from '@/test/fixtures';
import { server } from '@/test/server';
import type { EventKind, ProjectionEvent } from './api';
import { EventDialog } from './EventDialog';
import { ALICE, FLAT, LOAN_B, loans, persons, PURCHASE, properties, scenarioList } from './testData';

const ISO = /^\d{4}-\d{2}-\d{2}$/;

beforeEach(() => {
  server.use(
    http.get('*/api/v1/core/persons', () => HttpResponse.json(persons)),
    http.get('*/api/v1/realestate/loans', () => HttpResponse.json(loans)),
    http.get('*/api/v1/realestate/properties', () => HttpResponse.json(properties)),
    http.get('*/api/v1/scenarios', () => HttpResponse.json(scenarioList)),
  );
});

/** The dialog alone under the real providers, as the creation page and the detail page open it. */
async function open(kind: EventKind, props: { event?: ProjectionEvent; errors?: { path: string; message: string }[] } = {}) {
  const onSave = vi.fn<(event: ProjectionEvent) => void>();
  const onClose = vi.fn();
  const queryClient = createQueryClient();
  const api = createApi({ getCsrf: () => aliceSession.csrf, onUnauthenticated: vi.fn() });
  const location = { pathname: '/', search: '', assign: vi.fn(), reload: vi.fn() };
  const ui = <EventDialog kind={kind} scope="household" onSave={onSave} onClose={onClose} onDone={vi.fn()} {...props} />;
  const router = createRouter({ routeTree: createRootRoute({ component: () => ui }), history: createMemoryHistory() });
  await router.load();
  const utils = render(
    <Providers api={api} queryClient={queryClient} session={aliceSession} capabilities={allCapabilities} location={location}>
      <RouterProvider router={router} />
    </Providers>,
  );
  const dialog = await screen.findByRole('dialog');
  return { ...utils, dialog, onSave, onClose, user: userEvent.setup() };
}

describe('EventDialog', () => {
  it('sends an early repayment in the wire shape, its defaults kept', async () => {
    const { dialog, onSave, user } = await open('early_repayment');

    expect(within(dialog).getByRole('heading', { name: 'Remboursement anticipé' })).toBeInTheDocument();
    await user.type(within(dialog).getByLabelText('Date du remboursement'), '15/03/2027');
    await user.type(within(dialog).getByLabelText('Montant remboursé'), '20000');
    expect(within(dialog).queryByRole('combobox', { name: 'Prêt' })).not.toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: 'Ajouter l’événement' }));

    await waitFor(() => expect(onSave).toHaveBeenCalledTimes(1));
    expect(onSave.mock.calls[0]![0]).toEqual({
      kind: 'early_repayment', start: { on: '2027-03-15' }, amount: '20000', split: 'highest_rate', effect: 'reduce_term', penalty: '0',
    });
  });

  it('asks for the loan when the split is chosen, and sends it', async () => {
    const { dialog, onSave, user } = await open('early_repayment');

    await user.type(within(dialog).getByLabelText('Date du remboursement'), '15/03/2027');
    await user.type(within(dialog).getByLabelText('Montant remboursé'), '5000,50');
    await pick(user, 'Répartition', 'Un prêt choisi');
    await pick(user, 'Prêt', 'Prêt travaux');
    await user.click(within(dialog).getByRole('radio', { name: 'Réduire la mensualité' }));
    await user.clear(within(dialog).getByLabelText('Indemnités de remboursement anticipé'));
    await user.type(within(dialog).getByLabelText('Indemnités de remboursement anticipé'), '150');
    await user.click(within(dialog).getByRole('button', { name: 'Ajouter l’événement' }));

    await waitFor(() => expect(onSave).toHaveBeenCalledTimes(1));
    expect(onSave.mock.calls[0]![0]).toEqual({
      kind: 'early_repayment', start: { on: '2027-03-15' }, amount: '5000.50', split: 'chosen', loan: LOAN_B, effect: 'reduce_payment', penalty: '150',
    });
  });

  it('dates an income change « à l’âge de » a person, with no end', async () => {
    const { dialog, onSave, user } = await open('income_change');

    expect(within(dialog).getByRole('heading', { name: 'Changement de revenu' })).toBeInTheDocument();
    await user.type(within(dialog).getByLabelText('Libellé'), 'Mi-temps');
    await user.click(within(within(dialog).getByRole('radiogroup', { name: 'Début' })).getByRole('radio', { name: 'À l’âge de' }));
    await pick(user, 'Personne', 'Alice');
    await user.type(within(dialog).getByLabelText('Âge'), '60');
    await user.type(within(dialog).getByLabelText('Montant mensuel'), '-800');
    await user.click(within(dialog).getByRole('button', { name: 'Ajouter l’événement' }));

    await waitFor(() => expect(onSave).toHaveBeenCalledTimes(1));
    expect(onSave.mock.calls[0]![0]).toEqual({ kind: 'income_change', label: 'Mi-temps', start: { person: ALICE, age: 60 }, monthlyAmount: '-800' });
  });

  it('sends the end of a savings change when it has one', async () => {
    const { dialog, onSave, user } = await open('savings_change');

    expect(within(dialog).getByRole('heading', { name: 'Changement d’épargne' })).toBeInTheDocument();
    await user.type(within(dialog).getByLabelText('Date de début'), '01/01/2027');
    await user.click(within(within(dialog).getByRole('radiogroup', { name: 'Fin' })).getByRole('radio', { name: 'À une date' }));
    await user.type(within(dialog).getByLabelText('Date de fin'), '01/01/2030');
    await user.type(within(dialog).getByLabelText('Montant mensuel'), '300');
    await user.click(within(dialog).getByRole('button', { name: 'Ajouter l’événement' }));

    await waitFor(() => expect(onSave).toHaveBeenCalledTimes(1));
    expect(onSave.mock.calls[0]![0]).toEqual({
      kind: 'savings_change', start: { on: '2027-01-01' }, until: { on: '2030-01-01' }, monthlyAmount: '300',
    });
  });

  it('sends a sale without a price when none is typed', async () => {
    const { dialog, onSave, user } = await open('property_sale');

    expect(within(dialog).getByRole('heading', { name: 'Vente d’un bien' })).toBeInTheDocument();
    await pick(user, 'Bien', 'Appartement Lyon');
    await user.type(within(dialog).getByLabelText('Date de la vente'), '30/06/2030');
    await user.clear(within(dialog).getByLabelText('Frais de vente'));
    await user.type(within(dialog).getByLabelText('Frais de vente'), '12000');
    await user.click(within(dialog).getByRole('button', { name: 'Ajouter l’événement' }));

    await waitFor(() => expect(onSave).toHaveBeenCalledTimes(1));
    expect(onSave.mock.calls[0]![0]).toEqual({ kind: 'property_sale', property: FLAT, start: { on: '2030-06-30' }, costs: '12000' });
  });

  it('offers only the saved purchase scenarios, with no date of its own', async () => {
    const { dialog, onSave, user } = await open('property_purchase');

    expect(within(dialog).getByRole('heading', { name: 'Achat (scénario enregistré)' })).toBeInTheDocument();
    expect(within(dialog).queryByRole('radiogroup')).not.toBeInTheDocument();
    expect(within(dialog).queryByLabelText(/^Date/)).not.toBeInTheDocument();
    await user.click(within(dialog).getByRole('combobox', { name: 'Scénario d’achat' }));
    const list = await screen.findByRole('listbox');
    expect(within(list).getAllByRole('option').map((o) => o.textContent)).toEqual(['Studio Lyon']);
    await user.click(within(list).getByRole('option', { name: 'Studio Lyon' }));
    await user.click(within(dialog).getByRole('button', { name: 'Ajouter l’événement' }));

    await waitFor(() => expect(onSave).toHaveBeenCalledTimes(1));
    expect(onSave.mock.calls[0]![0]).toEqual({ kind: 'property_purchase', scenario: PURCHASE, start: { on: expect.stringMatching(ISO) as string } });
  });

  it('refuses a missing amount on its field', async () => {
    const { dialog, onSave, user } = await open('early_repayment');

    await user.type(within(dialog).getByLabelText('Date du remboursement'), '15/03/2027');
    await user.click(within(dialog).getByRole('button', { name: 'Ajouter l’événement' }));

    expect(await within(dialog).findByText('Ce champ est obligatoire.')).toBeInTheDocument();
    expect(within(dialog).getByLabelText('Montant remboursé')).toHaveAttribute('aria-invalid', 'true');
    expect(onSave).not.toHaveBeenCalled();
  });

  it('shows the server’s refusals on their fields, an event read back as typed', async () => {
    const event: ProjectionEvent = { id: 'e1', kind: 'income_change', label: 'Mi-temps', start: { person: ALICE, age: 17 }, monthlyAmount: '-800' };
    const { dialog, onSave, user } = await open('income_change', { event, errors: [{ path: 'start.age', message: 'L’âge va de 18 à 100 ans.' }] });

    expect(within(dialog).getByLabelText('Âge')).toHaveValue('17');
    expect(within(dialog).getByLabelText('Âge')).toHaveAttribute('aria-invalid', 'true');
    expect(within(dialog).getByText('L’âge va de 18 à 100 ans.')).toBeInTheDocument();
    await user.clear(within(dialog).getByLabelText('Âge'));
    await user.type(within(dialog).getByLabelText('Âge'), '60');
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer l’événement' }));

    await waitFor(() => expect(onSave).toHaveBeenCalledTimes(1));
    expect(onSave.mock.calls[0]![0]).toEqual({ id: 'e1', kind: 'income_change', label: 'Mi-temps', start: { person: ALICE, age: 60 }, monthlyAmount: '-800' });
  });

  it('has no accessibility violation', async () => {
    const { dialog } = await open('early_repayment');
    await expectNoAxeViolations(dialog);
  });
});
