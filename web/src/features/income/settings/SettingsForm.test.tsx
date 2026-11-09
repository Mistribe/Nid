import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { toasts } from '@/test/toasts';
import { detail, SALARY } from '../testData';

function serve(bodies: unknown[], answer: () => Response = () => HttpResponse.json({ id: SALARY })) {
  server.use(
    http.get('*/api/v1/income/sources/:id', () => HttpResponse.json(detail())),
    http.patch('*/api/v1/income/sources/:id', async ({ request }) => { bodies.push(await request.json()); return answer(); }),
  );
}

const settings = () => screen.getByRole('form', { name: 'Réglages' });

describe('income settings', () => {
  beforeAll(async () => { await import('../IncomeSourceDetailPage'); }, 30_000);

  it('sends only what changed, every clear flag stated, and says it is saved', async () => {
    const bodies: unknown[] = [];
    serve(bodies);
    const { user, container } = await renderApp(`/income/${SALARY}?tab=settings`);

    await screen.findByRole('heading', { name: 'Salaire Alice', level: 1 });
    await pick(user, 'Type', 'Pension');
    await user.clear(within(settings()).getByLabelText(/Payeur/));
    await expectNoAxeViolations(container);
    await user.click(within(settings()).getByRole('button', { name: 'Enregistrer les réglages' }));

    await waitFor(() => expect(toasts()).toHaveTextContent('Réglages enregistrés.'));
    expect(bodies).toEqual([{ kind: 'pension', clearPayerLabel: true, clearNote: false }]);
  });

  describe('#69: its owners and sharing', () => {
    const ALICE = '01926b9e-0000-7000-8000-00000000000a';
    const LEA = '01926b9e-0000-7000-8000-0000000000c1';
    const owned = (bodies: unknown[], answer?: () => Response) => {
      serve(bodies, answer);
      server.use(
        http.get('*/api/v1/income/sources/:id', () => HttpResponse.json(detail({ owners: [{ personId: ALICE, shareBp: 10000 }], canChangeOwners: true }))),
        http.get('*/api/v1/core/persons', () => HttpResponse.json([
          { id: ALICE, kind: 'adult', displayName: 'Alice Dev', email: null, retirementAge: 64 },
          { id: LEA, kind: 'dependent', displayName: 'Léa Dev', email: null, retirementAge: 64 },
        ])),
      );
    };

    it('hands the source to a child, sending only the owners', async () => {
      const bodies: unknown[] = [];
      owned(bodies);
      const { user } = await renderApp(`/income/${SALARY}?tab=settings`);

      await screen.findByRole('heading', { name: 'Salaire Alice', level: 1 });
      await pick(user, 'Personne', 'Léa Dev');
      await user.click(within(settings()).getByRole('button', { name: 'Enregistrer les réglages' }));

      await waitFor(() => expect(toasts()).toHaveTextContent('Réglages enregistrés.'));
      expect(bodies).toEqual([{ clearPayerLabel: false, clearNote: false, owners: [{ personId: LEA, shareBp: 10000 }] }]);
    });

    it('shows a refusal to hide the source from the other adult under the owners', async () => {
      owned([], () => HttpResponse.json(
        { status: 422, code: 'core.takes_read_away', title: 'x', errors: [{ path: 'owners', code: 'core.takes_read_away', message: 'x' }] },
        { status: 422 }));
      const { user } = await renderApp(`/income/${SALARY}?tab=settings`);

      await screen.findByRole('heading', { name: 'Salaire Alice', level: 1 });
      await pick(user, 'Personne', 'Léa Dev');
      await user.click(within(settings()).getByRole('button', { name: 'Enregistrer les réglages' }));

      expect(await within(within(settings()).getByRole('group', { name: 'Propriétaires' })).findByRole('alert'))
        .toHaveTextContent('L’autre adulte ne verrait plus cet élément');
    });

    it('is not offered to an adult who may edit without owning', async () => {
      serve([]);
      await renderApp(`/income/${SALARY}?tab=settings`);

      await screen.findByRole('heading', { name: 'Salaire Alice', level: 1 });
      expect(within(settings()).queryByRole('group', { name: 'Propriétaires' })).not.toBeInTheDocument();
    });
  });

  it('says there is nothing to save rather than sending an empty change', async () => {
    const bodies: unknown[] = [];
    serve(bodies);
    const { user } = await renderApp(`/income/${SALARY}?tab=settings`);

    await screen.findByRole('heading', { name: 'Salaire Alice', level: 1 });
    await user.click(within(settings()).getByRole('button', { name: 'Enregistrer les réglages' }));

    await waitFor(() => expect(toasts()).toHaveTextContent('Aucune modification à enregistrer.'));
    expect(bodies).toEqual([]);
  });

  it('shows a server refusal under the field it names', async () => {
    serve([], () => HttpResponse.json(
      { status: 422, code: 'income.invalid_source', title: 'x', errors: [{ path: 'payerLabel', code: 'income.invalid_source', message: 'x' }] },
      { status: 422 }));
    const { user } = await renderApp(`/income/${SALARY}?tab=settings`);

    await screen.findByRole('heading', { name: 'Salaire Alice', level: 1 });
    await user.type(within(settings()).getByLabelText(/Payeur/), ' SA');
    await user.click(within(settings()).getByRole('button', { name: 'Enregistrer les réglages' }));

    expect(await within(settings()).findByText('Cette valeur n’est pas acceptée.')).toBeInTheDocument();
    expect(within(settings()).queryByRole('alert')).not.toBeInTheDocument();
  });

  it('refuses an empty name before sending', async () => {
    const bodies: unknown[] = [];
    serve(bodies);
    const { user } = await renderApp(`/income/${SALARY}?tab=settings`);

    await screen.findByRole('heading', { name: 'Salaire Alice', level: 1 });
    await user.clear(within(settings()).getByLabelText('Nom'));
    await user.click(within(settings()).getByRole('button', { name: 'Enregistrer les réglages' }));

    expect(await within(settings()).findByText('Ce champ est obligatoire.')).toBeInTheDocument();
    expect(bodies).toEqual([]);
  });
});
