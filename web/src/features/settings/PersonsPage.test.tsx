import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { toasts } from '@/test/toasts';

const alice = { id: '01926b9e-0000-7000-8000-00000000000a', kind: 'adult', displayName: 'Alice Martin', email: 'alice@example.test' };

describe('Réglages › Personnes', () => {
  it('adds a child, who then shows in the list with the « Enfant » badge', async () => {
    const persons: object[] = [alice];
    const sent: unknown[] = [];
    server.use(
      http.get('*/api/v1/core/persons', () => HttpResponse.json(persons)),
      http.post('*/api/v1/core/persons/dependents', async ({ request }) => {
        const body = (await request.json()) as { displayName: string };
        sent.push(body);
        const child = { id: '01926b9e-0000-7000-8000-00000000000c', kind: 'dependent', displayName: body.displayName, email: null };
        persons.push(child);
        return HttpResponse.json(child, { status: 201 });
      }),
    );
    const { user } = await renderApp('/settings/persons');

    // Its own tab under the « Réglages » title (issue 387), no longer at the foot of Apparence.
    expect(await screen.findByRole('heading', { level: 1, name: 'Réglages' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Personnes' })).toHaveAttribute('aria-current', 'page');
    await user.click(await screen.findByRole('button', { name: 'Ajouter un enfant' }));
    const dialog = screen.getByRole('dialog', { name: 'Ajouter un enfant' });
    await user.type(within(dialog).getByLabelText('Prénom et nom'), 'Léo Martin');
    await user.click(within(dialog).getByRole('button', { name: 'Ajouter' }));

    await waitFor(() => expect(toasts()).toHaveTextContent('Léo Martin fait maintenant partie du foyer.'));
    expect(sent).toEqual([{ displayName: 'Léo Martin' }]);
    const list = screen.getByRole('list', { name: 'Personnes' });
    const leo = await within(list).findByText('Léo Martin');
    expect(leo).toHaveTextContent('Enfant');
    expect(within(list).getByText('Alice Martin')).not.toHaveTextContent('Enfant');
  });

  it('shows the API problem when the name is refused, and keeps the dialog open', async () => {
    server.use(http.post('*/api/v1/core/persons/dependents', () => HttpResponse.json(
      { status: 400, code: 'core.name_required', title: 'A name is required.', traceId: '00-t-24' }, { status: 400 })));
    const { user } = await renderApp('/settings/persons');

    await user.click(await screen.findByRole('button', { name: 'Ajouter un enfant' }));
    const dialog = screen.getByRole('dialog', { name: 'Ajouter un enfant' });
    await user.click(within(dialog).getByRole('button', { name: 'Ajouter' }));

    expect(await within(dialog).findByRole('alert')).toHaveTextContent('A name is required.');
    expect(screen.getByRole('dialog', { name: 'Ajouter un enfant' })).toBeInTheDocument();
  });
});
