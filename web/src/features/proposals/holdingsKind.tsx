import type { TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { Badge } from '@/components/ui/badge';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney } from '@/components/Money';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import fr from '@/locales/fr.proposals.json';
import { ValueComparison, type KindUi, type Money } from './kindParts';
import '@/locales/proposals';

type HoldingsPayload = { asOf: string; lines: unknown[]; movements: unknown[] };
type LinePreview = {
  index: number; name: string; isin?: string | null; kind: string; isNew: boolean; units?: string | null; unitPrice?: string | null;
  value: Money; previous?: { asOf: string; value: Money } | null; largeChange: boolean;
};
type MovementPreview = {
  index: number; holding: string; on: string; kind: string; units?: string | null; amount: Money; label?: string | null; recorded: boolean;
};
type HoldingsPreview = { total: Money | null; recordValuation?: boolean; valuation?: unknown; lines: LinePreview[]; movements: MovementPreview[] };

const asHoldings = (payload: unknown) => payload as HoldingsPayload;

/** A decimal string as French prints it: the server's digits, a comma for the point. */
const units = (x?: string | null) => (x ? x.replace('.', ',') : '');

const holdingKind = (t: TFunction, kind: string) =>
  Object.hasOwn(fr.proposal.holdings.kind, kind) ? t(`proposal.holdings.kind.${kind as keyof typeof fr.proposal.holdings.kind}`) : kind;

const movementKind = (t: TFunction, kind: string) =>
  Object.hasOwn(fr.proposal.holdings.movement_kind, kind) ? t(`proposal.holdings.movement_kind.${kind as keyof typeof fr.proposal.holdings.movement_kind}`) : kind;

/** Issue #34 §6: the statement's lines (new holdings and large changes marked), its movements, the envelope's value when it records one. */
function HoldingsPreviewView({ preview }: { preview: unknown }) {
  const { t } = useTranslation();
  const p = preview as HoldingsPreview;
  return (
    <div className="space-y-3">
      {p.valuation ? (
        <section aria-label={t('proposal.holdings.valuation')}>
          <h2 className="mb-2 text-base font-medium">{t('proposal.holdings.valuation')}</h2>
          <ValueComparison preview={p.valuation} />
        </section>
      ) : null}
      <Table>
        <caption className="text-left font-semibold">{t('proposal.holdings.lines')}</caption>
        <TableHeader>
          <TableRow>
            <TableHead scope="col">{t('proposal.holdings.name')}</TableHead>
            <TableHead scope="col">{t('proposal.holdings.kind_column')}</TableHead>
            <TableHead scope="col" className="text-right">{t('proposal.holdings.units')}</TableHead>
            <TableHead scope="col" className="text-right">{t('proposal.holdings.unit_price')}</TableHead>
            <TableHead scope="col" className="text-right">{t('proposal.holdings.value')}</TableHead>
            <TableHead scope="col" className="text-right">{t('proposal.holdings.previous')}</TableHead>
            <TableHead scope="col">{t('proposal.movements.state')}</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {p.lines.map((l) => (
            <TableRow key={l.index}>
              <TableCell>
                <span className="block">{l.name}</span>
                {l.isin ? <span className="block text-sm text-muted-foreground">{l.isin}</span> : null}
              </TableCell>
              <TableCell>{holdingKind(t, l.kind)}</TableCell>
              <TableCell className="text-right">{units(l.units)}</TableCell>
              <TableCell className="text-right">{units(l.unitPrice)}</TableCell>
              <TableCell className="text-right">{formatMoney(l.value)}</TableCell>
              <TableCell className="text-right">
                {l.previous ? t('proposal.holdings.previous_value', { amount: formatMoney(l.previous.value), date: formatLocalDate(l.previous.asOf, 'short') }) : ''}
              </TableCell>
              <TableCell className="space-x-1">
                {l.isNew ? <Badge variant="secondary">{t('proposal.holdings.new')}</Badge> : null}
                {l.largeChange ? <Badge variant="destructive">{t('proposal.holdings.large_change')}</Badge> : null}
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      {p.total ? <p>{t('proposal.holdings.total', { amount: formatMoney(p.total) })}</p> : null}
      {p.movements.length > 0 ? (
        <Table>
          <caption className="text-left font-semibold">{t('proposal.holdings.movements')}</caption>
          <TableHeader>
            <TableRow>
              <TableHead scope="col">{t('proposal.movements.on')}</TableHead>
              <TableHead scope="col">{t('proposal.holdings.name')}</TableHead>
              <TableHead scope="col">{t('proposal.movements.kind')}</TableHead>
              <TableHead scope="col">{t('proposal.movements.label')}</TableHead>
              <TableHead scope="col" className="text-right">{t('proposal.holdings.units')}</TableHead>
              <TableHead scope="col" className="text-right">{t('proposal.movements.amount')}</TableHead>
              <TableHead scope="col">{t('proposal.movements.state')}</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {p.movements.map((m) => (
              <TableRow key={m.index}>
                <TableCell>{formatLocalDate(m.on)}</TableCell>
                <TableCell>{m.holding}</TableCell>
                <TableCell>{movementKind(t, m.kind)}</TableCell>
                <TableCell>{m.label ?? ''}</TableCell>
                <TableCell className="text-right">{units(m.units)}</TableCell>
                <TableCell className="text-right">{formatMoney(m.amount)}</TableCell>
                <TableCell>{m.recorded ? t('proposal.movements.recorded') : ''}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      ) : null}
    </div>
  );
}

/** The lines are the document's: approved as proposed, or rejected. */
function HoldingsFields() {
  const { t } = useTranslation();
  return <p>{t('proposal.holdings.as_proposed')}</p>;
}

export const holdings: KindUi = {
  label: 'proposals.kind.holdings',
  summary: (t, p) => {
    const o = asHoldings(p.editedPayload ?? p.payload);
    return t('proposals.summary.holdings', {
      name: p.targetName ?? t('proposals.unknown_target'), date: formatLocalDate(o.asOf, 'short'), count: o.lines.length,
    });
  },
  Preview: HoldingsPreviewView,
  schema: () => z.object({}),
  toForm: () => ({}),
  toPayload: () => null,
  field: () => undefined,
  Fields: HoldingsFields,
  link: (p) => ({ to: '/investments/$id', params: { id: p.targetItemId ?? '' } }),
};
