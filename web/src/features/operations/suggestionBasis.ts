import '@/locales/banks';

/** #450: how a suggestion's basis is said: the pattern's own lines, those of patterns near it by meaning, the category's name, or a model's guess. */
export function basisTexts(basis: string) {
  if (basis === 'ai') return { label: 'banks.suggestions.by_ai', about: 'banks.suggestions.by_ai_about' } as const;
  if (basis === 'meaning') return { label: 'banks.suggestions.by_meaning', about: 'banks.suggestions.by_meaning_about' } as const;
  if (basis === 'category_name') return { label: 'banks.suggestions.by_name', about: 'banks.suggestions.by_name_about' } as const;
  return { label: 'banks.suggestions.evidence', about: 'banks.suggestions.evidence_about' } as const;
}
