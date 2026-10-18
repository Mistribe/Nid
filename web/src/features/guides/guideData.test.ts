import { describe, expect, it } from 'vitest';
import i18n from '@/app/i18n';
import { NAV, section } from '@/app/nav';
import { GUIDES, allGuidesSeen, guideFor, isUnseen } from './guideData';

describe('guides', () => {
  it('every guide’s section is an entry of the navigation', () => {
    const sections = NAV.map((n) => section(n.to));
    for (const g of GUIDES) expect(sections).toContain(g.section);
  });

  it('every key a guide uses exists in fr.json', () => {
    const keys = [
      ...['step', 'previous', 'next', 'skip', 'start', 'show', 'finish', 'close', 'help', 'title'].map((k) => `guides.common.${k}`),
      ...['title', 'replay_all', 'replayed'].map((k) => `guides.settings.${k}`),
    ];
    for (const g of GUIDES) {
      for (let i = 1; i <= g.screens; i++) keys.push(`guides.${g.id}.screens.${i}.title`, `guides.${g.id}.screens.${i}.body`);
      for (const t of g.tour) keys.push(`guides.${g.id}.tour.${t}.title`, `guides.${g.id}.tour.${t}.body`);
      if (g.hint) keys.push(`guides.${g.id}.hint`);
    }
    expect(keys.filter((k) => !i18n.exists(k))).toEqual([]);
  });

  it('knows which guides are unseen', () => {
    const loans = guideFor('/loans')!;
    expect(isUnseen(loans, {})).toBe(true);
    expect(isUnseen(loans, { loans: 1 })).toBe(false);
    expect(isUnseen({ ...loans, version: 2 }, { loans: 1 })).toBe(true);
    expect(Object.keys(allGuidesSeen())).toHaveLength(9);
  });

  it('has no guide for a section without one', () => {
    expect(guideFor('/security')).toBeUndefined();
  });
});
