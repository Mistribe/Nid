import { useTranslation } from 'react-i18next';
import { present, toProblem } from '@/api/errors';
import { ProblemMessage } from '@/components/ProblemMessage';
import { AlertTriangle } from 'lucide-react';
import type { TaxEstimate } from '../api';
import { ActivitySection } from './ActivitySection';
import { ExemptSection } from './ExemptSection';
import { ExpensesSection } from './ExpensesSection';
import { HouseholdSection } from './HouseholdSection';
import { RentalSection } from './RentalSection';
import { RetirementSection } from './RetirementSection';
import { ProfileNav } from './ProfileNav';
import { reviewReasons, SECTIONS, type ProfileSectionId } from './review';
import type { Draft } from './useProfileDraft';
import '@/locales/tax';

export const PROFILE_FORM = 'tax-profile';
const TITLES = {
  foyer: 'tax.profile.household', activite: 'tax.profile.activity', retraite: 'tax.profile.retirement', depenses: 'tax.profile.expenses',
  location: 'tax.profile.rental', exoneres: 'tax.profile.exempt_title',
} as const satisfies Record<ProfileSectionId, string>;

/** Tax profile spec §3: the household's declarations for the year, in sections; it is submitted from beside the tabs. */
export function ProfileForm({ estimate, draft }: { estimate: TaxEstimate; draft: Draft }) {
  const { t } = useTranslation();
  const props = { estimate, draft };
  const reasons = reviewReasons(estimate);
  const sections = SECTIONS.filter((id) => id !== 'location' || estimate.rentalProperties.length)
    .map((id) => ({ id, label: t(TITLES[id]), review: Boolean(reasons[id]) }));
  return (
    <div className="space-y-4">
      {estimate.profileFrom ? (
        <div className="flex gap-3 rounded-xl border border-status-warning/60 bg-status-warning/10 p-4 text-sm">
          <AlertTriangle aria-hidden="true" className="mt-0.5 size-4 shrink-0 text-status-warning" />
          <p>{t('tax.profile.from', { year: estimate.profileFrom })}</p>
        </div>
      ) : <p className="text-sm text-muted-foreground">{t('tax.profile.help')}</p>}
      <p role="status" aria-live="polite">{draft.announcement}</p>
      {draft.problem ? <ProblemMessage problem={present(toProblem(draft.problem))} /> : null}
      <div className="flex flex-wrap items-start gap-10">
        <div className="flex-1 basis-48 lg:sticky lg:top-4 lg:max-w-56"><ProfileNav sections={sections} /></div>
        <form id={PROFILE_FORM} aria-label={t('tax.profile.title')} noValidate className="min-w-0 flex-[999_1_40rem] space-y-8"
          onSubmit={(e) => {
            e.preventDefault();
            // Refused here, nothing is sent: the first field to fix takes the focus (a folded part holding one is open).
            if (!draft.submit()) e.currentTarget.querySelector<HTMLElement>('[aria-invalid="true"]')?.focus();
          }}>
          <HouseholdSection {...props} />
          <ActivitySection {...props} />
          <RetirementSection {...props} />
          <ExpensesSection {...props} />
          <RentalSection {...props} />
          <ExemptSection {...props} />
        </form>
      </div>
    </div>
  );
}
