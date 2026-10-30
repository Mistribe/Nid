import type { TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import { formatLocalDate, LocalDate } from '@/components/LocalDate';
import { Section } from '@/components/page/Section';
import type { Detail } from '../api';
import { durationText } from '../format';
import '@/locales/subscriptions';

/** The cancellation status as one sentence; `none` (no date to count from) says nothing. The server owns every date. */
function sentence(t: TFunction, c: Detail['cancellation']): string | null {
  switch (c.status) {
    case 'deadline':
      return c.lastDayToCancel && c.effectiveOn
        ? t('contract_terms.deadline', { last: formatLocalDate(c.lastDayToCancel), effective: formatLocalDate(c.effectiveOn) }) : null;
    case 'free_from': return c.freeOn ? t('contract_terms.free_from', { date: formatLocalDate(c.freeOn) }) : null;
    case 'free_now': return t('contract_terms.free_now');
    default: return null;
  }
}

/** Spec §5: the contract terms under the coverage, shown only when at least one is set. */
export function ContractSection({ detail }: { detail: Detail }) {
  const { t } = useTranslation();
  const terms = detail.contractTerms;
  if (!terms) return null;
  const rows = [
    terms.anniversaryOn ? { label: t('contract_terms.anniversary'), value: <LocalDate value={terms.anniversaryOn} /> } : null,
    terms.notice ? { label: t('contract_terms.notice'), value: durationText(t, terms.notice) } : null,
    terms.commitmentEndsOn ? { label: t('contract_terms.commitment_ends'), value: <LocalDate value={terms.commitmentEndsOn} /> } : null,
    terms.cancellableFrom ? { label: t('contract_terms.cancellable_from'), value: <LocalDate value={terms.cancellableFrom} /> } : null,
  ].filter((r) => r !== null);
  const text = sentence(t, detail.cancellation);
  return (
    <Section title={t('contract_terms.title')}>
      <dl className="grid gap-x-6 gap-y-1 sm:grid-cols-[max-content_1fr]">
        {rows.map((r) => (
          <div key={r.label} className="contents">
            <dt className="text-muted-foreground">{r.label}</dt>
            <dd>{r.value}</dd>
          </div>
        ))}
      </dl>
      {text ? <p>{text}</p> : null}
    </Section>
  );
}
