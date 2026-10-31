import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { Detail } from '../api';
import { detail, persons, SUB } from '../testData';

type Insurance = NonNullable<Detail['insurance']>;
const PROPERTY = '0192f0bb-0000-7000-8000-00000000e001';
const POLICY: Insurance = {
  kind: 'home', property: { id: PROPERTY, name: 'Maison de Nantes' }, insuredLabel: 'La maison et son garage',
  policyNumber: 'HAB-42', insurerContact: '01 02 03 04 05',
  guarantees: [{ label: 'Incendie', ceiling: '300 000 €', deductible: null }, { label: 'Vol', ceiling: null, deductible: '10 % avec minimum 300 €' }],
};

function serve(body: Detail, onDelete?: () => void) {
  server.use(
    http.delete('*/api/v1/subscriptions/:id/insurance', () => { onDelete?.(); return new HttpResponse(null, { status: 204 }); }),
    http.get('*/api/v1/subscriptions/providers', () => HttpResponse.json([])),
    http.get('*/api/v1/subscriptions/:id', () => HttpResponse.json(body)),
    http.get('*/api/v1/core/persons', () => HttpResponse.json(persons)),
    http.get('*/api/v1/documents', () => HttpResponse.json({ items: [] })),
  );
}

const open = async () => {
  const app = await renderApp(`/subscriptions/${SUB}`);
  await screen.findByRole('heading', { level: 1, name: 'Spotify Premium Duo' });
  return app;
};

describe('InsuranceSection', () => {
  it('is absent without an insurance', async () => {
    serve(detail());
    await open();
    expect(screen.queryByRole('region', { name: 'Assurance' })).not.toBeInTheDocument();
  });

  it('links the insured property and lists the guarantees', async () => {
    serve(detail({ insurance: POLICY }));
    await open();
    const region = await screen.findByRole('region', { name: 'Assurance' });
    expect(within(region).getByText('Type d’assurance').nextElementSibling).toHaveTextContent('Habitation');
    expect(within(region).getByRole('link', { name: 'Maison de Nantes' })).toHaveAttribute('href', `/properties/${PROPERTY}`);
    expect(within(region).getByText('La maison et son garage')).toBeInTheDocument();
    expect(within(region).getByText('HAB-42')).toBeInTheDocument();
    const rows = within(within(region).getByRole('table', { name: 'Garanties' })).getAllByRole('row');
    expect(rows).toHaveLength(3);
    expect(rows[1]).toHaveTextContent('Incendie');
    expect(rows[1]).toHaveTextContent('300 000 €');
    expect(rows[2]).toHaveTextContent('10 % avec minimum 300 €');
  });

  it('shows the insured label only when the property is not visible to the reader', async () => {
    serve(detail({ insurance: { ...POLICY, property: null } }));
    await open();
    const region = await screen.findByRole('region', { name: 'Assurance' });
    expect(within(region).queryByRole('link')).not.toBeInTheDocument();
    expect(within(region).queryByText('Bien assuré')).not.toBeInTheDocument();
    expect(within(region).getByText('La maison et son garage')).toBeInTheDocument();
  });

  it('labels the header button by what it does', async () => {
    serve(detail({ insurance: POLICY }));
    await open();
    expect(await screen.findByRole('button', { name: 'Modifier l’assurance' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Ajouter l’assurance' })).not.toBeInTheDocument();
  });

  it('offers to add one without insurance', async () => {
    serve(detail());
    await open();
    expect(screen.getByRole('button', { name: 'Ajouter l’assurance' })).toBeInTheDocument();
  });

  it('hides the actions without write access', async () => {
    serve(detail({ insurance: POLICY, canWrite: false }));
    await open();
    await screen.findByRole('region', { name: 'Assurance' });
    expect(screen.queryByRole('button', { name: 'Retirer l’assurance' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^(Ajouter|Modifier) l’assurance$/ })).not.toBeInTheDocument();
  });

  it('asks before removing the insurance', async () => {
    let removed = 0;
    serve(detail({ insurance: POLICY }), () => { removed += 1; });
    const { user } = await open();
    await user.click(await screen.findByRole('button', { name: 'Retirer l’assurance' }));
    const dialog = await screen.findByRole('alertdialog');
    expect(removed).toBe(0);
    await user.click(within(dialog).getByRole('button', { name: 'Confirmer' }));
    await waitFor(() => expect(removed).toBe(1));
  });
});

describe('Documents de l’abonnement', () => {
  it('flips the proposed role back to the invoice once the insurance is removed', async () => {
    let current = detail({ insurance: POLICY });
    server.use(
      http.delete('*/api/v1/subscriptions/:id/insurance', () => { current = detail(); return new HttpResponse(null, { status: 204 }); }),
      http.get('*/api/v1/subscriptions/providers', () => HttpResponse.json([])),
      http.get('*/api/v1/subscriptions/:id', () => HttpResponse.json(current)),
      http.get('*/api/v1/core/persons', () => HttpResponse.json(persons)),
      http.get('*/api/v1/documents', () => HttpResponse.json({ items: [] })),
    );
    const { user } = await open();
    await user.click(await screen.findByRole('button', { name: 'Retirer l’assurance' }));
    await user.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Confirmer' }));
    await waitFor(() => expect(screen.queryByRole('region', { name: 'Assurance' })).not.toBeInTheDocument());

    await user.click(await screen.findByRole('button', { name: /Déposer/ }));
    const role = within(await screen.findByRole('dialog')).getByRole('combobox', { name: 'Type' });
    expect(role).toHaveTextContent('Facture');
  });

  const roleOf = async (body: Detail) => {
    serve(body);
    const { user } = await open();
    await user.click(await screen.findByRole('button', { name: /Déposer/ }));
    const dialog = await screen.findByRole('dialog');
    return within(dialog).getByRole('combobox', { name: 'Type' });
  };

  it('proposes the insurance role when the subscription has an insurance', async () => {
    expect(await roleOf(detail({ insurance: POLICY }))).toHaveTextContent('Assurance');
  });

  it('proposes the invoice role otherwise', async () => {
    expect(await roleOf(detail())).toHaveTextContent('Facture');
  });
});
