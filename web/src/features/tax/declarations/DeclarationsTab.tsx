import { useNavigate, useSearch } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import type { TaxEstimate } from '@/api/tax';
import type { TaxSearch } from '@/app/router';
import { Segmented } from '@/components/form/Segmented';
import { InfoTip } from '@/components/InfoTip';
import { Loading, SectionCardSkeleton } from '@/components/page/skeletons';
import { Declaration2042 } from './Declaration2042';
import { Rental2044Form } from './Rental2044';
import '@/locales/tax';

/** The forms filed for the year just ended: the 2042's figures to copy, or the 2044's rental statement (offered with a rental property). */
export function DeclarationsTab({ estimate, year, scope }: { estimate: TaxEstimate; year: string; scope: 'me' | 'household' }) {
  const { t } = useTranslation();
  const search: TaxSearch = useSearch({ strict: false });
  const navigate = useNavigate();
  const rental = estimate.rentalProperties.length > 0;
  const form = rental && search.form === 2044 ? '2044' : '2042';
  const onForm = (next: string) => {
    void navigate({ to: '.', replace: true, resetScroll: false, search: ((prev: Record<string, unknown>) => ({ ...prev, form: next === '2044' ? 2044 : undefined })) as never });
  };
  return (
    <>
      <div className="flex flex-wrap items-center gap-3 print:hidden">
        <p className="flex items-center gap-1.5 text-sm text-muted-foreground">
          {t('tax.declarations.question')}<InfoTip label={t('tax.declarations.question')}>{t(`tax.d${form}.tip`)}</InfoTip>
        </p>
        {rental ? <Segmented aria-label={t('tax.declarations.form')} value={form} onValueChange={onForm} options={[{ value: '2042', label: '2042' }, { value: '2044', label: '2044' }]} /> : null}
      </div>
      <h2 className="hidden text-xl font-semibold print:block">{t(`tax.d${form}.print_title`, { year })}</h2>
      {form === '2044' ? <Rental2044Form year={Number(year)} scope={scope} />
        // The page keeps the previous year's estimate while this one loads: its 2042 would export under the wrong year.
        : String(estimate.year) === year ? <Declaration2042 estimate={estimate} />
          : <Loading label={t('tax.loading')}><SectionCardSkeleton /></Loading>}
    </>
  );
}
