import { useNavigate, useSearch } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { Segmented } from '@/components/form/Segmented';
import type { RootSearch } from '@/app/router';

export const PRIVATE_ITEMS = '/api/v1/core/me/private-items' as const;

/**
 * Foyer = shared items; Moi = shared and my private ones (IVisibleItems, screens.md §1). Without a private item the two
 * match, so the selector stays hidden (issue 85) — unless the URL already says Moi, to leave a way back.
 */
export function ScopeSwitch() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const search: RootSearch = useSearch({ strict: false });
  const navigate = useNavigate();
  const owns = $api.useQuery('get', PRIVATE_ITEMS).data?.owns;
  const current = search.scope === 'me' ? 'me' : 'household';
  if (current === 'household' && !owns) return null;

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
