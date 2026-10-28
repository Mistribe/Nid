import { Check } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import type { ProfileSectionId } from './review';
import '@/locales/tax';

/** The profile's sections as anchors, each marked « à revoir » when the server gives a reason, else checked. */
export function ProfileNav({ sections }: { sections: { id: ProfileSectionId; label: string; review: boolean }[] }) {
  const { t } = useTranslation();
  return (
    <nav aria-label={t('tax.profile.nav')} className="flex flex-wrap gap-1 lg:flex-col">
      {sections.map((s) => (
        <a key={s.id} href={`#${s.id}`}
          className="flex min-h-10 items-center justify-between gap-2 rounded-md px-3 py-2 text-sm text-muted-foreground hover:bg-muted hover:text-foreground">
          <span>{s.label}</span>
          {s.review
            ? <span className="rounded-full bg-status-warning/10 px-2 text-xs font-medium text-status-warning-text">{t('tax.profile.review_badge')}</span>
            : <Check aria-hidden="true" className="size-4 text-status-good-text" />}
        </a>
      ))}
    </nav>
  );
}
