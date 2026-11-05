import { useNavigate } from '@tanstack/react-router';
import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import type { PropertiesSearch, RootSearch } from '@/app/router';
import { SelectField } from '@/components/form/SelectField';
import { Segmented } from '@/components/form/Segmented';
import '@/locales/properties';

/** Beyond this many properties, the filter is a select rather than buttons (spec §4.3). */
const BUTTONS = 3;

/** Spec §4.3: « Tous » or one property, the page's `?property=`; nothing with fewer than two to choose from. */
export function PropertyFilter({ properties, property }: { properties: { id: string; name: string }[]; property?: string }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const filterId = useId();
  if (properties.length < 2) return null;
  const choose = (next: string) =>
    void navigate({ to: '/properties', replace: true, resetScroll: false, search: (prev: RootSearch & PropertiesSearch) => ({ ...prev, property: next || undefined }) });
  const options = [{ value: '', label: t('properties_rental.all') }, ...properties.map((p) => ({ value: p.id, label: p.name }))];
  return properties.length <= BUTTONS ? (
    <>
      <span id={filterId} className="sr-only">{t('properties_rental.filter')}</span>
      <Segmented value={property ?? ''} onValueChange={choose} aria-labelledby={filterId} options={options} />
    </>
  ) : (
    <div className="w-56"><SelectField id={`${filterId}-select`} aria-label={t('properties_rental.filter')} value={property ?? ''} onValueChange={choose} options={options} /></div>
  );
}
