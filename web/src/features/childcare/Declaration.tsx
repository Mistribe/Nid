import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Section } from '@/components/page/Section';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import type { Declaration as DeclarationView } from './api';
import { frenchNumber, monthTitle } from './statementText';
import '@/locales/childcare';

type Key = keyof DeclarationView;
/** The last declaration's end-of-contract fields: shown only on that month, never typed by hand. */
type EndKey = 'leaveIndemnity' | 'ruptureIndemnity';
type EntryKey = Exclude<Key, EndKey>;
const isEntry = (key: Key): key is EntryKey => key !== 'leaveIndemnity' && key !== 'ruptureIndemnity';

/** Decision 30: the entry column, what is typed by hand field by field; empty leaves the server's figure. */
export type DeclarationEntry = { values: Record<EntryKey, string>; pending: boolean; onChange: (key: EntryKey, value: string) => void; onSave: () => void };

/** Spec §3: Pajemploi's fields in its order, the end-of-contract indemnities after the salary; « Copier » copies the server's figure with a decimal comma, no sign. */
const ORDER: Key[] = ['normalHours', 'activityDays', 'complementaryHours', 'overtimeHours', 'paidLeaveDays', 'netPay', 'leaveIndemnity', 'ruptureIndemnity', 'upkeep', 'meals', 'mileage'];
/** Pajemploi's « Travail à déclarer » asks these as Oui / Non: always shown, « Non » when the server has none. */
const QUESTIONS = ['complementaryHours', 'overtimeHours', 'paidLeaveDays'] as const;
const isQuestion = (key: Key): key is (typeof QUESTIONS)[number] => (QUESTIONS as readonly Key[]).includes(key);
const none = (value: DeclarationView[Key]) => value === null || /^0(\.0+)?$/.test(String(value));

/**
 * #32: three columns per field, Nid's figure (<c>computed</c>, the server's without the fields typed by hand), the value the month
 * uses, marked when it differs from Nid's, and the entry. The column names show above the rows, beside each cell on a phone.
 */
export function Declaration({ declaration, computed, mealCharges, missing, entry }: {
  declaration: DeclarationView | null; computed?: DeclarationView | null; mealCharges?: { lunch: string; snack: string } | null; missing: string[]; entry?: DeclarationEntry | null;
}) {
  const { t } = useTranslation();
  const nid = computed ?? declaration;
  // Each row is its own grid: only fixed tracks line the rows up.
  const columns = entry ? 'sm:grid-cols-[minmax(0,1fr)_7rem_13rem_7rem]' : 'sm:grid-cols-[minmax(0,1fr)_7rem_13rem]';
  const caption = (key: 'computed' | 'used') => <span className="text-xs text-muted-foreground sm:sr-only">{t(`childcare_statement.column.${key}`)}</span>;
  const shown = (key: Key, value: DeclarationView[Key]) => (isQuestion(key) && none(value)
    ? t('childcare_statement.answer_no')
    : `${isQuestion(key) ? `${t('childcare_statement.answer_yes')} · ` : ''}${frenchNumber(String(value))}`);
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
          <div aria-hidden="true" className={`hidden gap-3 px-3 py-2 text-xs text-muted-foreground sm:grid ${columns}`}>
            <span />
            <span>{t('childcare_statement.column.computed')}</span>
            <span>{t('childcare_statement.column.used')}</span>
            {entry ? <span>{t('childcare_statement.column.entry')}</span> : null}
          </div>
          {ORDER.filter((key) => (declaration[key] ?? null) !== null || isQuestion(key)).map((key) => {
            const label = t(`childcare_statement.field.${key}`);
            const title = isQuestion(key) ? t(`childcare_statement.question.${key}`) : label;
            const differs = String(declaration[key]) !== String(nid![key]);
            const value = frenchNumber(String(declaration[key]));
            return (
              <div key={key} className={`grid grid-cols-2 items-center gap-x-3 gap-y-1 px-3 py-2 ${columns}`}>
                <dt className="col-span-2 text-sm sm:col-span-1">
                  {title}
                  {key === 'meals' && mealCharges ? <span className="block text-xs text-muted-foreground">{t('childcare_statement.meal_parts', { lunch: frenchNumber(mealCharges.lunch), snack: frenchNumber(mealCharges.snack) })}</span> : null}
                </dt>
                <dd className="flex flex-col">{caption('computed')}<span className="tabular-nums">{shown(key, nid![key])}</span></dd>
                <dd className="flex flex-col">
                  {caption('used')}
                  <span className="flex flex-wrap items-center gap-2 sm:grid sm:grid-cols-[minmax(0,1fr)_auto]">
                    <span className={`tabular-nums font-medium ${differs ? 'text-status-warning-text' : ''}`}>{shown(key, declaration[key])}</span>
                    {differs ? <span className="sr-only">{t('childcare_statement.differs')}</span> : null}
                    {isQuestion(key) && none(declaration[key]) ? null : (
                      <Button type="button" variant="outline" size="sm" className="sm:col-start-2" aria-label={t('childcare_statement.copy_of', { field: label })}
                        onClick={() => { void navigator.clipboard.writeText(value).then(() => toast.success(t('childcare_statement.copied', { field: label }))); }}>
                        {t('childcare_statement.copy')}
                      </Button>
                    )}
                  </span>
                </dd>
                {entry ? (
                  <dd className="col-span-2 sm:col-span-1">
                    {isEntry(key) ? (
                      <Input aria-label={t('childcare_statement.entry_of', { field: label })} inputMode="decimal" className="w-28" value={entry.values[key]}
                        onChange={(e) => entry.onChange(key, e.target.value)} />
                    ) : null}
                  </dd>
                ) : null}
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
