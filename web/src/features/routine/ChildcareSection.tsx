import { Link } from '@tanstack/react-router';
import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { useChildcareList } from '@/api/childcare';
import { present, toProblem } from '@/api/errors';
import { ProblemMessage } from '@/components/ProblemMessage';
import { monthLabel } from '@/lib/format';
import { elision } from '@/lib/elision';
import '@/locales/routine';

/** #19: the routine's « Garde » — the month's statements to prepare, each arrangement's older ones counted in one row. */
export function ChildcareSection({ on }: { on: string }) {
  const { t } = useTranslation();
  const headingId = useId();
  // The reader's own arrangements, closed ones too: the month one ends in is still to prepare (decision 18).
  const answer = useChildcareList('me', true);
  const month = on.slice(0, 7);
  const [year = 0, m = 1] = month.split('-').map(Number);
  const monthText = monthLabel(year, m);
  const rows = (answer.data?.items ?? []).filter((c) => c.canWrite).flatMap((c) => {
    const late = c.toPrepare.filter((x) => x < month).length;
    return [
      ...(c.toPrepare.includes(month) ? [
        <li key={`${c.id}:due`} className="py-2">
          <Link to="/childcare/$id/statements/$month" params={{ id: c.id, month }} className="underline underline-offset-2">
            {t('routine_childcare.due', { month: monthText, name: c.name, context: elision(monthText) })}
          </Link>
        </li>] : []),
      ...(late > 0 ? [
        <li key={`${c.id}:late`} className="py-2">
          <Link to="/childcare/$id" params={{ id: c.id }} className="underline underline-offset-2">
            {t('routine_childcare.late', { count: late, name: c.name })}
          </Link>
        </li>] : []),
    ];
  });

  return (
    <section aria-labelledby={headingId} className="space-y-4 border-t pt-4">
      <h2 id={headingId} className="text-xl font-semibold">{t('routine_childcare.title')}</h2>
      {answer.error && !answer.data ? <ProblemMessage problem={present(toProblem(answer.error))} /> : null}
      {answer.data && rows.length === 0 ? <p>{t('routine_childcare.empty')}</p> : null}
      {rows.length > 0 ? <ul className="divide-y">{rows}</ul> : null}
    </section>
  );
}
