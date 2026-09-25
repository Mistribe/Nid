import { useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { components } from '@/api/schema';
import { ConfirmAction } from '@/components/ConfirmAction';
import { ProblemMessage } from '@/components/ProblemMessage';
import { invalidateSubscriptions, type Detail, type Purchase } from '../api';
import { PriceChangeForm } from './PriceChangeForm';
import { PurchaseForm } from './PurchaseForm';
import { SettingsForm } from './SettingsForm';

type Person = components['schemas']['PersonDto'];

/** The forms of the detail, shown only when the reader may write (1E2a design §4). */
export function Edits({ detail }: { detail: Detail }) {
  return (
    <div className="grid gap-6 md:grid-cols-2">
      <SettingsForm detail={detail} />
      <PriceChangeForm detail={detail} />
      <PurchaseForm detail={detail} />
    </div>
  );
}

/** With `canWrite` false the actions are absent and the reason is written (screens.md §10), naming who can. */
export function ReadOnly({ detail, persons }: { detail: Detail; persons: Person[] }) {
  const { t } = useTranslation();
  const names = detail.owners.map((o) => persons.find((p) => p.id === o.personId)?.displayName).filter((n): n is string => !!n);
  if (names.length === 0) return null;
  const list = new Intl.ListFormat('fr', { type: 'conjunction' }).format(names);
  return <p className="text-sm text-muted-foreground">{t('edit.read_only', { names: list })}</p>;
}

const PURCHASE = '/api/v1/subscriptions/{id}/purchases/{purchaseId}' as const;

export function DeletePurchase({ detail, purchase }: { detail: Detail; purchase: Purchase }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const remove = $api.useMutation('delete', PURCHASE, { onSuccess: () => void invalidateSubscriptions(queryClient) });
  return (
    <>
      <ConfirmAction label={t('edit.delete_purchase', { label: purchase.label })} confirmation={t('edit.delete_confirm')} pending={remove.isPending}
        onConfirm={() => remove.mutate({ params: { path: { id: detail.id, purchaseId: purchase.id } } })} />
      {remove.error ? <ProblemMessage problem={present(toProblem(remove.error))} /> : null}
    </>
  );
}
