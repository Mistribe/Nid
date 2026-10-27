import { useNavigate, useSearch } from '@tanstack/react-router';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { IncomeDetailSearch, RootSearch } from '@/app/router';
import { Segmented } from '@/components/form/Segmented';
import { Button } from '@/components/ui/button';
import { ProblemMessage } from '@/components/ProblemMessage';
import { useAnchoredDocuments } from '../../documents/api';
import { todayIso } from '../../subscriptions/today';
import { EXPECTED, type IncomeSourceDetail } from '../api';
import { ExpectedTable, type PayslipColumn } from '../expected/ExpectedIncome';
import { EXPECTED_PRESETS, expectedRange, type ExpectedPreset } from '../expected/months';
import { BulkPayslipsDialog } from '../payslips/BulkPayslipsDialog';
import { attachTarget, coverage, isMissing, monthKeyOf } from '../payslips/payslip';
import { OneOffDocument } from '../payslips/OneOffDocument';
import { PayslipCell } from '../payslips/PayslipCell';
import { UndatedPayslips } from '../payslips/UndatedPayslips';
import { Section } from '@/components/page/Section';
import '@/locales/income';

// Option values: `all`, or a label behind a prefix (a label may read « all »).
const ALL = 'all';
const PREFIX = 'label:';

/**
 * Income dashboard §4: every payment of the chosen range, due and upcoming, newest first, without a chart. The range is
 * `?expected=` (the last twelve months by default, out of the URL); the component filter only hides rows.
 */
export function HistoryTab({ source }: { source: IncomeSourceDetail }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const navigate = useNavigate();
  const search: RootSearch & IncomeDetailSearch = useSearch({ strict: false });
  // `strict: false` reads the raw search, so an unknown value is dropped here as well.
  const preset: ExpectedPreset = EXPECTED_PRESETS.find((p) => p === search.expected) ?? 'last12';
  const today = todayIso();
  const { from, to } = expectedRange(preset, today);
  const answer = $api.useQuery('get', EXPECTED, { params: { path: { id: source.id }, query: { from, to } } });
  const [label, setLabel] = useState(ALL);
  const [bulk, setBulk] = useState(false);
  const labels = [...new Set(source.phases.map((p) => p.label))].sort((a, b) => a.localeCompare(b, 'fr'));
  const choose = (next: ExpectedPreset) =>
    void navigate({
      from: '/income/$id', to: '/income/$id', params: { id: source.id }, replace: true,
      search: (prev: RootSearch & IncomeDetailSearch) => ({ ...prev, expected: next === 'last12' ? undefined : next }),
    });
  const { data, error } = answer;
  const documents = useAnchoredDocuments(source.id);
  const payslips = coverage(documents.data?.items ?? [], source.id, new Set(source.phases.map((p) => p.id)));
  const { byMonth, undated, oldest } = payslips;
  // Without the documents (still loading or refused), no column rather than a column that says nothing is attached.
  const payslipColumn: PayslipColumn | undefined = documents.data ? {
    header: t('income_detail.payslips.column'),
    cell: (m) => {
      if (m.occurrences.length === 0) return null;
      const month = monthKeyOf(m.year, m.month);
      const docs = byMonth.get(month) ?? [];
      const missing = isMissing(month, { salary: source.kind === 'salary', pastPayment: m.occurrences.some((o) => o.on < today), oldest, covered: docs.length > 0 });
      return <PayslipCell source={source} month={month} docs={docs} missing={missing} target={attachTarget(m.occurrences, today)} writable={source.canWrite} />;
    },
    oneOff: (f) => <OneOffDocument source={source} oneOff={f} docs={documents.data.items} />,
  } : undefined;

  return (
    <Section title={t('income_detail.history.title')}>
      <div className="flex flex-wrap gap-2">
        <Segmented aria-label={t('income_detail.expected.period')} value={preset} onValueChange={(v) => choose(v as ExpectedPreset)}
          options={EXPECTED_PRESETS.map((p) => ({ value: p, label: t(`income_detail.expected.${p}`) }))} />
        {labels.length > 1 ? (
          <Segmented aria-label={t('income_detail.history.label')} value={label} onValueChange={setLabel}
            options={[{ value: ALL, label: t('income_detail.history.all') }, ...labels.map((l) => ({ value: PREFIX + l, label: l }))]} />
        ) : null}
        {source.canWrite && documents.data ? (
          <Button variant="outline" className="ml-auto" onClick={() => setBulk(true)}>{t('income_detail.payslips.bulk')}</Button>
        ) : null}
      </div>
      {bulk ? <BulkPayslipsDialog source={source} payslips={payslips} onClose={() => setBulk(false)} /> : null}
      <UndatedPayslips docs={undated} />
      {/* A failed refetch keeps the data: the error shows only when there is nothing to show. */}
      {!data && error ? <ProblemMessage problem={present(toProblem(error))} />
        : !data ? <div role="status" aria-busy="true" aria-label={t('income_detail.expected.loading')} className="h-64 shimmer rounded-md border bg-muted" />
          : data.months.every((m) => m.occurrences.length === 0 && m.oneOffs.length === 0)
            ? <p className="text-muted-foreground">{t('income_detail.expected.empty')}</p>
            : <ExpectedTable months={data.months} label={label === ALL ? undefined : label.slice(PREFIX.length)} payslipColumn={payslipColumn} />}
    </Section>
  );
}
