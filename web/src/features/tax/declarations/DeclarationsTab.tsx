import { useTranslation } from 'react-i18next';
import type { TaxEstimate } from '@/api/tax';
import { Segmented } from '@/components/form/Segmented';
import { Declaration2042Section } from '../Declaration2042Section';
import { Rental2044Section } from '../Rental2044Section';
import '@/locales/tax';

type Form = '2042' | '2044';

/** The forms filed for the year just ended: the 2042's figures to copy, or the 2044's rental statement. */
export function DeclarationsTab({ estimate, year, scope, form, onForm }: {
  estimate: TaxEstimate; year: string; scope: 'me' | 'household'; form: Form; onForm: (form: Form) => void;
}) {
  const { t } = useTranslation();
  return (
    <>
      <div className="print:hidden">
        <Segmented aria-label={t('tax.declarations.form')} value={form} onValueChange={(v) => onForm(v as Form)} options={[{ value: '2042', label: '2042' }, { value: '2044', label: '2044' }]} />
      </div>
      {form === '2042' ? (
        <>
          <h2 className="hidden text-xl font-semibold print:block">{t('tax.d2042.print_title', { year })}</h2>
          <Declaration2042Section estimate={estimate} />
        </>
      ) : (
        <>
          {/* The tab list does not print: the printed 2044 keeps its title. */}
          <h2 className="hidden text-xl font-semibold print:block">{t('tax.d2044.print_title', { year })}</h2>
          <Rental2044Section year={Number(year)} scope={scope} />
        </>
      )}
    </>
  );
}
