import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { SelectField } from '@/components/form/SelectField';
import { PROPERTIES } from '../properties/api';
import { Field } from '@/components/form/Field';
import '@/locales/loans';

/**
 * 3D1 design §7: « Bien financé » — « Aucun » (an empty value), then the readable properties (the shared ones and one's own private ones: scope `me`) whatever the
 * Foyer / Moi selector says. The linked property keeps its own option from the first render, named from the list, else
 * by the loan, else as unreadable: neither a list still loading nor one it is missing from can make a save send « Aucun ».
 */
export function PropertySelect({ value, onValueChange, error, linked }: {
  value: string; onValueChange: (value: string) => void; error?: string; linked?: { id: string; name: string | null };
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const answer = $api.useQuery('get', PROPERTIES, { params: { query: { scope: 'me' } } });
  const items = answer.data?.items ?? [];
  const linkedName = linked ? (items.find((p) => p.id === linked.id)?.name ?? linked.name ?? t('loan_form.property_unreadable')) : null;
  const options = [
    { value: '', label: t('loan_form.property_none') },
    ...(linked ? [{ value: linked.id, label: linkedName ?? '' }] : []),
    ...items.filter((p) => p.id !== linked?.id).map((p) => ({ value: p.id, label: p.name })),
  ];
  return (
    <Field label={t('loan_form.property')} error={error}>
      {(c) => <SelectField {...c} value={value} onValueChange={onValueChange} options={options} />}
    </Field>
  );
}
