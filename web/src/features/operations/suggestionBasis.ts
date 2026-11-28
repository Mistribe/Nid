import type { TFunction } from 'i18next';
import '@/locales/banks';

/** What the server says of a suggestion's basis: `agreement` is a percentage it computed (similarity, share or probability). */
type Basis = { basis: string; placement?: string | null; agreement: number; evidenceCount: number; categoryName: string };

/**
 * #450: how a suggestion's basis is said, a short line and what its `InfoTip` explains: the pattern's own lines, those of patterns near it
 * by meaning, the category's name, or a model's guess on a worker here or away, or by a cloud provider (#812), with its calibrated
 * probability when the model gives one.
 */
export function basisTexts(t: TFunction, s: Basis): { label: string; about: string } {
  const values = { count: s.evidenceCount, agreement: s.agreement, category: s.categoryName };
  if (s.basis === 'ai') {
    const label = s.placement === 'remote' ? 'banks.suggestions.by_ai_remote' : s.placement === 'local' ? 'banks.suggestions.by_ai_local'
      : s.placement === 'cloud' ? 'banks.suggestions.by_ai_cloud' : 'banks.suggestions.by_ai';
    // A model with no scores gives no probability: 0.
    const sure = s.agreement > 0 ? ` ${t('banks.suggestions.by_ai_sure', values)}` : '';
    const about = s.placement === 'cloud' ? 'banks.suggestions.by_ai_cloud_about' : 'banks.suggestions.by_ai_about';
    return { label: t(label, values), about: `${t(about, values)}${sure}` };
  }
  const keys = s.basis === 'meaning' ? ['banks.suggestions.by_meaning', 'banks.suggestions.by_meaning_about'] as const
    : s.basis === 'category_name' ? ['banks.suggestions.by_name', 'banks.suggestions.by_name_about'] as const
    : ['banks.suggestions.evidence', 'banks.suggestions.evidence_about'] as const;
  return { label: t(keys[0], values), about: t(keys[1], values) };
}
