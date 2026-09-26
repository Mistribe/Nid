import { describe, expect, it } from 'vitest';
import i18n from '@/app/i18n';
import { eventLabel, viaLabel } from './eventLabel';

describe('eventLabel', () => {
  it('names what the command line did', () => {
    expect(eventLabel(i18n.t, 'admin_command')).toBe('Commande de secours');
    expect(eventLabel(i18n.t, 'subject_rebound')).toBe('Identité rattachée');
    expect(viaLabel(i18n.t, 'admin-cli')).toBe('Ligne de commande');
  });
});
