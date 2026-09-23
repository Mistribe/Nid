import { Baby, Lock, Users } from 'lucide-react';
import { useTranslation } from 'react-i18next';

export type Access = 'private' | 'shared' | 'dependent';

const ICONS = { private: Lock, shared: Users, dependent: Baby } as const;

/** Icon AND text: never colour alone (frontend/README.md §2, principle 6). */
export function AccessBadge({ access }: { access: Access }) {
  const { t } = useTranslation();
  const Icon = ICONS[access];
  return (
    <span className="inline-flex items-center gap-1 rounded-md border px-2 py-0.5 text-sm">
      <Icon aria-hidden="true" className="size-4" />
      {t(`access.${access}`)}
    </span>
  );
}
