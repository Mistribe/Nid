import { beforeAll, describe, expect, it } from 'vitest';
import i18n from '@/app/i18n';
import { declaration2042Csv } from './declaration2042Csv';

describe('declaration2042Csv', () => {
  beforeAll(async () => { await import('@/locales/tax'); });

  it('writes one row per box with its declarant, as the server sent it', () => {
    const declaration = { boxes: [{ code: '1AJ', person: 'a', amount: '42000.00' }, { code: '7UF', person: null, amount: '300.00' }], toEnter: [] };
    const name = (person?: string | null) => (person === 'a' ? 'Alice' : 'Foyer');
    expect(declaration2042Csv(declaration, name, i18n.t)).toBe([
      '﻿Déclarant;Case;Libellé;Montant',
      'Alice;1AJ;Salaires;42000,00',
      'Foyer;7UF;Autres dons;300,00',
      '',
    ].join('\r\n'));
  });
});
