import { describe, expect, it } from 'vitest';
import { detail } from '../testData';
import { toEditBody, toWireBody } from './settingsBody';

const values = { displayName: 'Salaire Alice', kind: 'salary', payerLabel: 'Employeur', note: '' };

describe('toEditBody', () => {
  it('is null when nothing changed, a trimmed name included', () => {
    expect(toEditBody(detail(), { ...values, displayName: ' Salaire Alice ' })).toBeNull();
  });

  it('sends only what changed', () => {
    expect(toEditBody(detail(), { ...values, kind: 'pension', note: 'Net après impôt' })).toEqual({ kind: 'pension', note: 'Net après impôt' });
  });

  it('says an emptied optional field with its clear flag', () => {
    expect(toEditBody(detail(), { ...values, payerLabel: '  ' })).toEqual({ clearPayerLabel: true });
    expect(toEditBody(detail({ note: 'x' }), values)).toEqual({ clearNote: true });
  });
});

describe('toWireBody', () => {
  it('states every clear flag: the server binds a missing member to null', () => {
    expect(toWireBody({ displayName: 'Salaire' })).toEqual({ displayName: 'Salaire', clearPayerLabel: false, clearNote: false });
  });
});
