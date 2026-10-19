import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { ComboboxField } from './ComboboxField';
import type { FieldControl } from './Field';

/** A searchable currency picker: every ISO code the platform knows, labelled « EUR — euro »; the search matches the code or the name. */
export function CurrencyField({ value, onValueChange, ...control }: FieldControl & { value: string; onValueChange: (v: string) => void }) {
  const { t } = useTranslation();
  const options = useMemo(() => {
    const names = new Intl.DisplayNames(['fr'], { type: 'currency' });
    const codes = Intl.supportedValuesOf('currency');
    return (codes.includes(value) || !value ? codes : [value, ...codes]).map((code) => ({ value: code, label: t('form.currency_option', { code, name: names.of(code) ?? code }) }));
  }, [t, value]);
  return <ComboboxField {...control} value={value} onValueChange={onValueChange} options={options}
    searchPlaceholder={t('form.search')} empty={t('form.no_result')} />;
}
