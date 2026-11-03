import { Eye, PenLine, UserCheck, UserX } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import '@/locales/forms';

export const ITEM_SHARING = '/api/v1/core/items/{id}/sharing';

/** #294: the item's sharing as the server reads it for this person; the badges, the menu and the dialog share the one query. */
export function useItemSharing(item: string, enabled = true) {
  const { $api } = useApi();
  return $api.useQuery('get', ITEM_SHARING, { params: { path: { id: item } } }, { enabled });
}

const chip = 'inline-flex items-center gap-1 rounded-md border px-2 py-0.5 text-sm';

/** Beside a detail page's `AccessBadge`: a shared item's level, and whether the person owns it. Icon AND text, never colour alone. */
export function SharingBadges({ item }: { item: string }) {
  const { t } = useTranslation();
  const sharing = useItemSharing(item).data;
  if (!sharing) return null;
  const edit = sharing.shareLevel === 'edit';
  const Level = edit ? PenLine : Eye;
  const Owner = sharing.isOwner ? UserCheck : UserX;
  return (
    <>
      {sharing.visibility === 'shared' ? (
        <span className={chip}><Level aria-hidden="true" className="size-4" />{t(edit ? 'sharing.edit' : 'sharing.view')}</span>
      ) : null}
      <span className={chip}><Owner aria-hidden="true" className="size-4" />{t(sharing.isOwner ? 'sharing.owner' : 'sharing.not_an_owner')}</span>
    </>
  );
}
