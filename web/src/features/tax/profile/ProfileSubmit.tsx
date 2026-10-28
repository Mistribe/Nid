import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/button';
import { PROFILE_FORM } from './ProfileForm';
import type { Draft } from './useProfileDraft';
import '@/locales/tax';

/** Beside the tab list (#174): the count of unsaved fields, and the button that submits the profile form. */
export function ProfileSubmit({ draft }: { draft: Draft }) {
  const { t } = useTranslation();
  return (
    <div className="flex flex-wrap items-center gap-3">
      {draft.changes > 0 ? <p className="text-sm text-muted-foreground">{t('tax.profile.changes', { count: draft.changes })}</p> : null}
      <Button type="submit" form={PROFILE_FORM} disabled={draft.pending}>{t('tax.profile.save')}</Button>
    </div>
  );
}
