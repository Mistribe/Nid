import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Section } from '@/components/page/Section';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import type { Declaration as DeclarationView } from './api';
import { frenchNumber, monthTitle } from './statementText';
import '@/locales/childcare';

type Key = keyof DeclarationView;

/** Decision 30: the second column, what is typed by hand field by field; empty leaves the server's figure. */
export type DeclarationEntry = { values: Record<Key, string>; pending: boolean; onChange: (key: Key, value: string) => void; onSave: () => void };

/** Spec §3: Pajemploi's fields in its order; « Copier » copies the server's figure with a decimal comma, no sign. */
const ORDER: Key[] = ['normalHours', 'activityDays', 'complementaryHours', 'overtimeHours', 'paidLeaveDays', 'netPay', 'upkeep', 'meals', 'mileage'];
/** Pajemploi's « Travail à déclarer » asks these as Oui / Non: always shown, « Non » when the server has none. */
const QUESTIONS = ['complementaryHours', 'overtimeHours', 'paidLeaveDays'] as const;
const isQuestion = (key: Key): key is (typeof QUESTIONS)[number] => (QUESTIONS as readonly Key[]).includes(key);
const none = (value: DeclarationView[Key]) => value === null || /^0(\.0+)?$/.test(String(value));

export function Declaration({ declaration, missing, entry }: { declaration: DeclarationView | null; missing: string[]; entry?: DeclarationEntry | null }) {
  const { t } = useTranslation();
  const field = (key: Key, label: string) => (entry ? (
    <dd><Input aria-label={t('childcare_statement.entry_of', { field: label })} inputMode="decimal" className="w-28" value={entry.values[key]} onChange={(e) => entry.onChange(key, e.target.value)} /></dd>
  ) : null);
  return (
    <Section title={t('childcare_statement.declaration')}>
      {declaration === null ? (
        <div className="space-y-1 text-sm">
          <p>{t('childcare_statement.declaration_withheld')}</p>
          {missing.length > 0 ? <p>{t('childcare_statement.missing_months', { months: missing.map(monthTitle).join(', ') })}</p> : null}
        </div>
      ) : (
        <form noValidate className="space-y-2" onSubmit={(e) => { e.preventDefault(); entry?.onSave(); }}>
        {entry ? <p className="text-sm text-muted-foreground">{t('childcare_statement.entry_hint')}</p> : null}
        <dl className="divide-y rounded-md border">
          {ORDER.filter((key) => declaration[key] !== null || isQuestion(key)).map((key) => {
            const label = t(`childcare_statement.field.${key}`);
            const question = isQuestion(key);
            const title = isQuestion(key) ? t(`childcare_statement.question.${key}`) : label;
            if (question && none(declaration[key])) {
              return (
                <div key={key} className="flex items-center justify-between gap-3 px-3 py-2">
                  <dt className="text-sm">{title}</dt>
                  <div className="flex items-center gap-2">
                    <dd className="font-medium">{t('childcare_statement.answer_no')}</dd>
                    {field(key, label)}
                  </div>
                </div>
              );
            }
            const value = frenchNumber(String(declaration[key]));
            return (
              <div key={key} className="flex items-center justify-between gap-3 px-3 py-2">
                <dt className="text-sm">{title}</dt>
                <div className="flex flex-wrap items-center justify-end gap-2">
                <dd className="flex items-center gap-2">
                  {question ? <span className="font-medium">{t('childcare_statement.answer_yes')}</span> : null}
                  <span className="tabular-nums font-medium">{value}</span>
                  <Button type="button" variant="outline" size="sm" aria-label={t('childcare_statement.copy_of', { field: label })}
                    onClick={() => { void navigator.clipboard.writeText(value).then(() => toast.success(t('childcare_statement.copied', { field: label }))); }}>
                    {t('childcare_statement.copy')}
                  </Button>
                </dd>
                {field(key, label)}
                </div>
              </div>
            );
          })}
        </dl>
        {entry ? <Button type="submit" variant="outline" disabled={entry.pending}>{t('childcare_statement.entry_save')}</Button> : null}
        </form>
      )}
    </Section>
  );
}
