import { useQueryClient, type QueryClient } from '@tanstack/react-query';
import type { TFunction } from 'i18next';
import { lazy, Suspense, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { Presentation } from '@/api/errors';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Reload } from '@/features/subscriptions/edits/Reload';
import { RowMenu } from './RowMenu';

const ConfirmDialog = lazy(() => import('@/components/ConfirmDialog'));

/**
 * A row's `⋯` menu: the table's own `items`, then « Supprimer » after a confirmation. The table's mutation comes in as
 * `remove`; a refusal reads through `present`, and a conflict or a row already gone offers « Recharger », which runs
 * `invalidate` then `onReloaded`.
 */
export function RowActions({ menuId, menuLabel, items, confirmation, remove, onConfirm, onDeleteStarted, present, invalidate, onReloaded }: {
  menuId: string; menuLabel: string; items: { label: string; onSelect: () => void }[]; confirmation: string;
  remove: { isPending: boolean; error: unknown; reset: () => void };
  /** Sends the deletion; `onDeleteStarted` has already run. */
  onConfirm: () => void;
  /** Runs the instant the deletion is confirmed, so the section's live region empties before its next message. */
  onDeleteStarted: () => void;
  present: (t: TFunction, error: unknown) => Presentation;
  invalidate: (queryClient: QueryClient) => Promise<void>;
  onReloaded?: () => void;
}) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [asking, setAsking] = useState(false);
  const problem = remove.error ? present(t, remove.error) : null;
  return (
    <span className="flex flex-wrap items-center justify-end gap-2">
      <RowMenu id={menuId} label={menuLabel} items={[...items, { label: t('common.delete'), onSelect: () => setAsking(true), destructive: true }]} />
      {asking ? (
        <Suspense fallback={null}>
          <ConfirmDialog label={t('common.delete')} confirmation={confirmation} pending={remove.isPending}
            onClose={() => setAsking(false)} onConfirm={() => { setAsking(false); onDeleteStarted(); onConfirm(); }} />
        </Suspense>
      ) : null}
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {problem.kind === 'conflict' || problem.kind === 'not_found'
            ? <Reload onReload={() => { remove.reset(); return invalidate(queryClient).then(onReloaded); }} />
            : null}
        </>
      ) : null}
    </span>
  );
}
