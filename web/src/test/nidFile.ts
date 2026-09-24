import { screen } from '@testing-library/react';
import type { UserEvent } from '@testing-library/user-event';

/** A small nid.data file: two adults (one with Alice's email), one subscription, one phase. */
export const workedExample = {
  format: 'nid.data',
  version: 1,
  persons: [
    { ref: 'adult-1', kind: 'adult', display_name: 'Adulte 1', email: 'alice@example.test' },
    { ref: 'adult-2', kind: 'adult', display_name: 'Adulte 2' },
  ],
  subscriptions: [
    { id: 'sub-spotify', provider_key: 'spotify', name: 'Spotify Premium Duo', ownership: [{ person: 'adult-1', share_bp: 10000 }], auto_renew: true },
  ],
  billing_phases: [
    {
      id: 'ph-spotify', subscription_id: 'sub-spotify', starts_on: '2025-06-28', ends_on: null,
      price: { amount: '17.20', currency: 'EUR' }, every: { count: 1, unit: 'month' }, payer: { type: 'household' },
    },
  ],
};

export function nidFile(content: unknown = workedExample, name = 'foyer.nid.json'): File {
  return new File([typeof content === 'string' ? content : JSON.stringify(content)], name, { type: 'application/json' });
}

export async function upload(user: UserEvent, file: File): Promise<void> {
  await user.upload(await screen.findByLabelText('Choisir un fichier'), file);
}
