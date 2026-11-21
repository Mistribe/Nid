import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { SelectField, type Option } from '@/components/form/SelectField';
import { PROPERTIES } from '@/api/properties';
import { ENVELOPES } from '@/api/investments';
import { Field } from '@/components/form/Field';
import '@/locales/loans';

/** What a loan finances: a property or (#180) an SCPI envelope, '' for none; never both. */
export type Financed = { propertyId: string; envelopeId: string };

type Linked = { id: string; name: string | null };

/**
 * 3D1 design §7: « Bien financé » — « Aucun » (an empty value), then the readable properties (the shared ones and one's own private ones: scope `me`) whatever the
 * Foyer / Moi selector says; #180: then the SCPI envelopes, under their own heading, listed by the server (`kind=scpi`). The
 * linked item keeps its own option from the first render, named from the list, else by the loan, else as unreadable: neither a
 * list still loading nor one it is missing from can make a save send « Aucun ».
 */
export function FinancedSelect({ value, onValueChange, error, linked }: {
  value: Financed; onValueChange: (value: Financed) => void; error?: string; linked?: { property?: Linked; envelope?: Linked };
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const properties = $api.useQuery('get', PROPERTIES, { params: { query: { scope: 'me' } } }).data?.items ?? [];
  const scpis = $api.useQuery('get', ENVELOPES, { params: { query: { scope: 'me', kind: 'scpi' } } }).data?.items ?? [];
  const names = new Map([...properties.map((p) => [p.id, p.name] as const), ...scpis.map((s) => [s.id, s.displayName] as const)]);
  const named = (link: Linked, unreadable: string) => names.get(link.id) ?? link.name ?? unreadable;
  const property = linked?.property;
  const envelope = linked?.envelope;
  const envelopeIds = new Set([...(envelope ? [envelope.id] : []), ...scpis.map((s) => s.id)]);
  // Headings only once an SCPI is listed: a household without one sees the list it always had.
  const grouped = envelopeIds.size > 0;
  const propertyGroup = grouped ? t('loan_form.financed_properties') : undefined;
  const scpiGroup = grouped ? t('loan_form.financed_scpis') : undefined;
  const options: Option[] = [
    { value: '', label: t('loan_form.property_none') },
    ...(property ? [{ value: property.id, label: named(property, t('loan_form.property_unreadable')), group: propertyGroup }] : []),
    ...properties.filter((p) => p.id !== property?.id).map((p) => ({ value: p.id, label: p.name, group: propertyGroup })),
    ...(envelope ? [{ value: envelope.id, label: named(envelope, t('loan_form.scpi_unreadable')), group: scpiGroup }] : []),
    ...scpis.filter((s) => s.id !== envelope?.id).map((s) => ({ value: s.id, label: s.displayName, group: scpiGroup })),
  ];
  const change = (id: string) => onValueChange(
    id === '' ? { propertyId: '', envelopeId: '' } : envelopeIds.has(id) ? { propertyId: '', envelopeId: id } : { propertyId: id, envelopeId: '' });
  return (
    <Field label={t('loan_form.property')} error={error}>
      {(c) => <SelectField {...c} value={value.envelopeId || value.propertyId} onValueChange={change} options={options} />}
    </Field>
  );
}
