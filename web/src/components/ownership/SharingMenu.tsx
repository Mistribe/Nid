import { lazy, Suspense, useRef, useState, type ComponentProps, type RefObject } from 'react';
import { useTranslation } from 'react-i18next';
import { RowMenu } from '@/components/page/RowMenu';
import '@/locales/forms';
import { useItemSharing } from './SharingBadges';

const SharingDialog = lazy(() => import('./SharingDialog').then((m) => ({ default: m.SharingDialog })));

/**
 * #294: a detail page's `⋯` with « Partage… » after its own entries when the item is shared and the person may change its
 * level (an owner; an adult who may only edit it sees the level in the page's badges instead). The menu owns the dialog and the
 * focus coming back to it; the page's live region says what was saved (`onAnnounce`, '' as the dialog opens).
 */
export function SharingMenu({ item, shared, label, items = [], triggerRef, onAnnounce }: {
  item: string; shared: boolean; label?: string; items?: ComponentProps<typeof RowMenu>['items']; triggerRef?: RefObject<HTMLButtonElement | null>;
  onAnnounce: (message: string) => void;
}) {
  const { t } = useTranslation();
  const own = useRef<HTMLButtonElement>(null);
  const trigger = triggerRef ?? own;
  const [open, setOpen] = useState(false);
  const canChange = useItemSharing(item, shared).data?.canChange === true;
  const close = (message?: string) => { setOpen(false); if (message) onAnnounce(message); trigger.current?.focus(); };
  return (
    <>
      <RowMenu label={label ?? t('sharing.level')} triggerRef={trigger}
        items={shared && canChange ? [...items, { label: t('sharing.menu'), onSelect: () => { onAnnounce(''); setOpen(true); } }] : items} />
      {open ? <Suspense fallback={null}><SharingDialog item={item} onClose={() => close()} onDone={close} /></Suspense> : null}
    </>
  );
}
