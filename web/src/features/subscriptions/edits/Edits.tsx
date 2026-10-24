import { useQueryClient } from '@tanstack/react-query';
import { lazy, Suspense, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { components } from '@/api/schema';
import { RowMenu } from '@/components/page/RowMenu';
import { ProblemMessage } from '@/components/ProblemMessage';
import { invalidateSubscriptions, type Detail, type Purchase } from '../api';
import '@/locales/forms';

const ConfirmDialog = lazy(() => import('@/components/ConfirmDialog'));

type Person = components['schemas']['PersonDto'];

/** With `canWrite` false the actions are absent and the reason is written (screens.md §10), naming who can. */
export function ReadOnly({ detail, persons }: { detail: Detail; persons: Person[] }) {
  const { t } = useTranslation();
  const names = detail.owners.map((o) => persons.find((p) => p.id === o.personId)?.displayName).filter((n): n is string => !!n);
  if (names.length === 0) return null;
  const list = new Intl.ListFormat('fr', { type: 'conjunction' }).format(names);
  return <p className="text-sm text-muted-foreground">{t('edit.read_only', { names: list })}</p>;
}

const PURCHASE = '/api/v1/subscriptions/{id}/purchases/{purchaseId}' as const;

/** A purchase's `⋯` menu: « Supprimer » asks first, then the page announces it. */
export function DeletePurchase({ detail, purchase, onDeleted }: { detail: Detail; purchase: Purchase; onDeleted: (message: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const [asking, setAsking] = useState(false);
  const remove = $api.useMutation('delete', PURCHASE, { onSuccess: () => { void invalidateSubscriptions(queryClient); onDeleted(t('edit.purchase_deleted')); } });
  return (
    <span className="flex flex-wrap items-center justify-end gap-2">
      <RowMenu label={t('edit.purchase_menu', { label: purchase.label })} items={[{ label: t('edit.delete_purchase'), onSelect: () => setAsking(true), destructive: true }]} />
      {asking ? (
        <Suspense fallback={null}>
          <ConfirmDialog label={t('edit.delete_purchase')} confirmation={t('edit.delete_confirm')} pending={remove.isPending} onClose={() => setAsking(false)}
            onConfirm={() => { setAsking(false); remove.mutate({ params: { path: { id: detail.id, purchaseId: purchase.id } } }); }} />
        </Suspense>
      ) : null}
      {remove.error ? <ProblemMessage problem={present(toProblem(remove.error))} /> : null}
    </span>
  );
}
