import type { TFunction } from 'i18next';
import type { JSX } from 'react';
import { useTranslation } from 'react-i18next';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney } from '@/components/Money';
import { trimAmount } from '../investments/entries/entries';
import type { ProposalView } from './api';

type Money = { amount: string; currency: string };
type Dated = { asOf: string; amount: Money };
type ValuationPayload = Dated & { note?: string | null };
export type ValuationForm = { asOf: string; amount: string; note: string };

export type KindUi = {
  /** An i18n key. */
  label: 'proposals.kind.valuation';
  summary: (t: TFunction, p: ProposalView) => string;
  Preview: (props: { preview: unknown }) => JSX.Element;
  toForm: (payload: unknown) => ValuationForm;
  /** The payload to send as `editedPayload`, or null when nothing really changed (Review Focus 3). */
  toPayload: (values: ValuationForm, original: unknown) => unknown;
  link: (p: ProposalView) => { to: '/investments/$id'; params: { id: string } } | null;
};

const asValuation = (payload: unknown): ValuationPayload => payload as ValuationPayload;

function ValuationPreview({ preview }: { preview: unknown }) {
  const { t } = useTranslation();
  const p = preview as { current?: Dated | null; previous?: Dated | null; proposed: Dated };
  const row = (label: string, v: Dated | null | undefined) => (
    <tr className="border-b">
      <th scope="row" className="py-2 pr-4 text-left font-normal">{label}</th>
      <td className="py-2 pr-4">{v ? formatLocalDate(v.asOf) : t('proposal.preview.none')}</td>
      <td className="py-2 text-right">{v ? formatMoney(v.amount) : ''}</td>
    </tr>
  );
  return (
    <table className="w-full">
      <caption className="sr-only">{t('proposal.preview.title')}</caption>
      <tbody>
        {row(t('proposal.preview.current'), p.current)}
        {row(t('proposal.preview.previous'), p.previous)}
        {row(t('proposal.preview.proposed'), p.proposed)}
      </tbody>
    </table>
  );
}

const valuation: KindUi = {
  label: 'proposals.kind.valuation',
  summary: (t, p) => {
    const v = asValuation(p.editedPayload ?? p.payload);
    return t('proposals.summary.valuation', {
      name: p.targetName ?? t('proposals.unknown_target'), date: formatLocalDate(v.asOf, 'short'), amount: formatMoney(v.amount),
    });
  },
  Preview: ValuationPreview,
  toForm: (payload) => {
    const v = asValuation(payload);
    return { asOf: v.asOf, amount: trimAmount(v.amount.amount), note: v.note ?? '' };
  },
  toPayload: (values, original) => {
    const o = asValuation(original);
    const amount = values.amount.replace(',', '.');
    const same = values.asOf === o.asOf && trimAmount(amount) === trimAmount(o.amount.amount) && values.note.trim() === (o.note ?? '').trim();
    if (same) return null;
    return { asOf: values.asOf, amount: { amount, currency: o.amount.currency }, ...(values.note.trim() ? { note: values.note.trim() } : {}) };
  },
  link: (p) => ({ to: '/investments/$id', params: { id: p.targetItemId } }),
};

const KINDS: Record<string, KindUi> = { 'investments.record_valuation': valuation };

export const kindOf = (kind: string): KindUi | undefined => KINDS[kind];

export function originText(t: TFunction, via: string): string {
  return via === 'ui' || via === 'api' || via === 'mcp' ? t(`proposals.origin.${via}`) : via;
}

/** The list's groups: one per statement label, « Sans relevé » last, each newest first (the server's order is kept). */
export function groupBySource(items: ProposalView[]): { label: string | null; items: ProposalView[] }[] {
  const groups = new Map<string | null, ProposalView[]>();
  for (const p of items) groups.set(p.sourceLabel ?? null, [...(groups.get(p.sourceLabel ?? null) ?? []), p]);
  return [...groups.entries()]
    .sort(([a], [b]) => (a === null ? 1 : b === null ? -1 : 0))
    .map(([label, list]) => ({ label, items: list }));
}
