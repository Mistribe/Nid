import { Link, useParams } from '@tanstack/react-router';
import { Printer } from 'lucide-react';
import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { present, toProblem } from '@/api/errors';
import { ErrorPage } from '@/components/ErrorPage';
import { InfoTip } from '@/components/InfoTip';
import { formatLocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { PageHeader } from '@/components/page/PageHeader';
import { PageSkeleton } from '@/components/page/skeletons';
import { Section } from '@/components/page/Section';
import { Button } from '@/components/ui/button';
import { useSettlement } from './api';
import { endReasonLabel, monthTitle, problemText } from './statementText';
import '@/locales/childcare';

/** A settlement line: its label, an explanation for the technical ones, the server's amount. */
function Line({ label, tip, value, strong }: { label: string; tip?: string; value: { amount: string; currency: string }; strong?: boolean }) {
  return (
    <div className={`flex justify-between gap-4 ${strong ? 'border-t border-black pt-1 font-semibold' : ''}`}>
      <dt className="flex items-center gap-1.5">{label}{tip ? <span className="print:hidden"><InfoTip label={label}>{tip}</InfoTip></span> : null}</dt>
      <dd><Money value={value} tabular /></dd>
    </div>
  );
}

/**
 * The end of a childminder's contract: the « reçu pour solde de tout compte » as plain typography to print, every figure the server's
 * (the last month's statement), then the documents the employer hands over. Nothing is computed here.
 */
export function SettlementPage() {
  const { t } = useTranslation();
  const { id } = useParams({ from: '/childcare/$id/settlement' });
  const answer = useSettlement(id);
  const titleId = useId();
  const crumbs = [{ label: t('childcare_list.title'), to: '/childcare' as const }];
  if (answer.error) return <ErrorPage problem={present(toProblem(answer.error))} crumbs={crumbs} onRetry={() => void answer.refetch()} />;
  if (!answer.data) return <PageSkeleton label={t('childcare_settlement.loading')} stats={0} />;
  const view = answer.data;
  const lines = view.lines;
  const statement = <Link to="/childcare/$id/statements/$month" params={{ id, month: view.month }} className="underline">{monthTitle(view.month)}</Link>;

  return (
    <div className="space-y-6">
      <div className="space-y-4 print:hidden">
        <PageHeader crumbs={[...crumbs, { label: view.name, to: '/childcare/$id', params: { id } }]} title={t('childcare_settlement.title')}
          actions={lines ? <Button type="button" variant="outline" onClick={() => window.print()}><Printer aria-hidden="true" />{t('childcare_settlement.print')}</Button> : null} />
        {lines && !view.validated ? <p className="text-sm text-muted-foreground">{t('childcare_settlement.draft')} {statement}</p> : null}
        {lines ? null : (
          <div role="status" className="space-y-1 text-sm">
            <p>{t('childcare_settlement.withheld')} {statement}</p>
            {view.missingMonths.length > 0 ? <p>{t('childcare_statement.missing_months', { months: view.missingMonths.map(monthTitle).join(', ') })}</p> : null}
            {view.problems.map((p) => <p key={p.code}>{problemText(t, p.code)}</p>)}
          </div>
        )}
      </div>

      {lines ? (
        <article aria-labelledby={titleId} className="mx-auto max-w-2xl space-y-6 bg-white p-6 text-black print:max-w-none print:p-0">
          <h2 id={titleId} className="text-2xl font-semibold">{t('childcare_settlement.receipt')}</h2>
          <dl className="space-y-1">
            <div><dt className="inline font-semibold">{t('childcare_settlement.carer')} </dt><dd className="inline">{view.carerName}</dd></div>
            <div><dt className="inline font-semibold">{t('childcare_settlement.child')} </dt><dd className="inline">{view.childName}</dd></div>
            <div><dt className="inline font-semibold">{t('childcare_settlement.period')} </dt>
              <dd className="inline">{t('childcare_settlement.from_to', { start: formatLocalDate(view.start), end: formatLocalDate(view.lastDay) })}</dd></div>
            <div><dt className="inline font-semibold">{t('childcare_settlement.reason')} </dt><dd className="inline">{endReasonLabel(t, view.end.reason)}</dd></div>
            <div><dt className="inline font-semibold">{t('childcare_end.notified')} </dt><dd className="inline">{formatLocalDate(view.end.notified)}</dd></div>
          </dl>
          <dl className="max-w-md space-y-1 tabular-nums">
            <Line label={t('childcare_settlement.last_pay', { month: monthTitle(view.month) })} value={lines.lastPay} />
            <Line label={t('childcare_settlement.regularisation')} tip={t('childcare_settlement.regularisation_tip')} value={lines.regularisation} />
            <Line label={lines.leaveDays ? t('childcare_settlement.leave_days', { count: lines.leaveDays }) : t('childcare_settlement.leave')}
              tip={t('childcare_settlement.leave_tip')} value={lines.leaveIndemnity} />
            <Line label={t('childcare_settlement.rupture')} tip={t('childcare_settlement.rupture_tip')} value={lines.ruptureIndemnity} />
            <Line label={t('childcare_settlement.upkeep')} value={lines.upkeep} />
            <Line label={t('childcare_settlement.meals')} value={lines.meals} />
            <Line label={t('childcare_settlement.mileage')} value={lines.mileage} />
            <Line label={t('childcare_settlement.total')} value={lines.total} strong />
          </dl>
          <p>{t('childcare_settlement.statement', { carer: view.carerName })}</p>
          <p>{t('childcare_settlement.signature')}</p>
        </article>
      ) : null}

      <div className="print:hidden">
        <Section title={t('childcare_settlement.documents')}>
          <ul className="list-disc space-y-1 pl-5 text-sm">
            <li>{t('childcare_settlement.doc_declaration')}</li>
            <li>{t('childcare_settlement.doc_france_travail')}</li>
            <li>{t('childcare_settlement.doc_certificate')}</li>
            <li>{t('childcare_settlement.doc_receipt')}</li>
          </ul>
        </Section>
      </div>
    </div>
  );
}
