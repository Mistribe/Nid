import { useNavigate, useSearch } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import type { RootSearch } from '@/app/router';

const SCOPES = ['household', 'me'] as const;

/** Foyer = shared items; Moi = shared and my private ones (IVisibleItems, screens.md §1). */
export function ScopeSwitch() {
  const { t } = useTranslation();
  const search: RootSearch = useSearch({ strict: false });
  const navigate = useNavigate();
  const current = search.scope === 'me' ? 'me' : 'household';

  return (
    <fieldset data-guide="dashboard.scope" className="flex rounded-md border border-input p-0.5">
      <legend className="sr-only">{t('scope.label')}</legend>
      {SCOPES.map((scope) => (
        <label
          key={scope}
          className="cursor-pointer rounded px-3 py-1 text-sm has-[:checked]:bg-primary has-[:checked]:text-primary-foreground has-[:focus-visible]:ring-2 has-[:focus-visible]:ring-ring"
        >
          <input
            type="radio"
            name="scope"
            value={scope}
            checked={current === scope}
            onChange={() => void navigate({ to: '.', search: (prev) => ({ ...prev, scope: scope === 'me' ? 'me' : undefined }) })}
            className="sr-only"
          />
          {t(`scope.${scope}`)}
        </label>
      ))}
    </fieldset>
  );
}
