import type { UseFormRegisterReturn } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { PROPERTIES } from '../properties/api';
import { Field } from '../subscriptions/edits/Field';

/**
 * 3D1 design §7: « Bien financé » — « Aucun » (an empty value), then the readable properties (the shared ones and one's own private ones: scope `me`) whatever the
 * Foyer / Moi selector says. The linked property keeps its own option from the first render, named from the list, else
 * by the loan, else as unreadable: neither a list still loading nor one it is missing from can make a save send « Aucun ».
 */
export function PropertySelect({ registration, error, linked }: {
  registration: UseFormRegisterReturn; error?: string; linked?: { id: string; name: string | null };
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const answer = $api.useQuery('get', PROPERTIES, { params: { query: { scope: 'me' } } });
  const items = answer.data?.items ?? [];
  const linkedName = linked ? (items.find((p) => p.id === linked.id)?.name ?? linked.name ?? t('loan_form.property_unreadable')) : null;
  return (
    <Field label={t('loan_form.property')} error={error}>
      {(c) => (
        <select {...c} {...registration} className="rounded-md border px-3 py-2">
          <option value="">{t('loan_form.property_none')}</option>
          {linked ? <option value={linked.id}>{linkedName}</option> : null}
          {items.filter((p) => p.id !== linked?.id).map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
        </select>
      )}
    </Field>
  );
}
