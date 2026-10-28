import type { TaxEstimate } from '../api';

export type ProfileSectionId = 'foyer' | 'activite' | 'retraite' | 'depenses' | 'location' | 'exoneres';
/** The profile tab's sections, in page order; each is the anchor of its region. */
export const SECTIONS: readonly ProfileSectionId[] = ['foyer', 'activite', 'retraite', 'depenses', 'location', 'exoneres'];

export type ReviewReason = 'ceiling_estimated' | 'carried_over';

/** Why a section asks to be reviewed: only what the server's figures tell, never a guess. */
export function reviewReasons(estimate: TaxEstimate): Partial<Record<ProfileSectionId, ReviewReason>> {
  const reasons: Partial<Record<ProfileSectionId, ReviewReason>> = {};
  if (estimate.result.declarants.some((d) => d.per?.source === 'estimated')) reasons.retraite = 'ceiling_estimated';
  // A year's expenses are that year's: carried over from an earlier profile, they are last year's figures.
  if (estimate.profileFrom !== null && estimate.profileFrom !== undefined) reasons.depenses = 'carried_over';
  return reasons;
}
