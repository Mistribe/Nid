import { describe, expect, it } from 'vitest';
import fr from '../../locales/fr.documents.json';
import { ROLES } from './format';

describe('ROLES', () => {
  it('has a French label for every role', () => {
    const labels: Record<string, string> = fr.documents_page.role;
    expect(ROLES.filter((role) => !labels[role])).toEqual([]);
  });

  it('offers the copropriété roles', () => {
    expect(ROLES).toEqual(expect.arrayContaining(['copro_call', 'copro_minutes', 'copro_accounts']));
  });
});
