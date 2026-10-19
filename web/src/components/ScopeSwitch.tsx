import { useNavigate, useSearch } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { Segmented } from '@/components/form/Segmented';
import type { RootSearch } from '@/app/router';

/** Foyer = shared items; Moi = shared and my private ones (IVisibleItems, screens.md §1). */
export function ScopeSwitch() {
  const { t } = useTranslation();
  const search: RootSearch = useSearch({ strict: false });
  const navigate = useNavigate();
  const current = search.scope === 'me' ? 'me' : 'household';

  return (
    <div data-guide="dashboard.scope">
      <Segmented
        aria-label={t('scope.label')}
        value={current}
        options={[{ value: 'household', label: t('scope.household') }, { value: 'me', label: t('scope.me') }]}
        onValueChange={(scope) => void navigate({ to: '.', search: (prev) => ({ ...prev, scope: scope === 'me' ? 'me' : undefined }) })}
      />
    </div>
  );
}
