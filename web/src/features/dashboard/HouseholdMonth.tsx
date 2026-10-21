import { Link } from '@tanstack/react-router';
import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { useSession } from '@/app/Providers';
import { formatMoney } from '@/components/Money';
import { NEUTRAL_COLOR } from '@/components/ProviderIcon';
import { SectionCard } from '@/components/page/SectionCard';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableRow } from '@/components/ui/table';
import { OVERVIEW } from '../income/api';
import { toPlot } from '../investments/chart/plot';
import type { DashboardView } from './api';
import { band, spread, stack, type Span } from './flow';
import { percent } from './format';

const SOURCES = [
  { key: 'subscriptions', color: 'var(--chart-1)' },
  { key: 'loans', color: 'var(--chart-2)' },
  { key: 'property_costs', color: 'var(--chart-3)' },
] as const;
const INCOME_COLOR = 'var(--muted-foreground)';

/** A mark of the flow: its texts hold the server's amount and share, `size` only sizes the mark. */
type Mark = {
  key: string; name: string; money: string; share: string | null; label: string; size: number; color: string;
  link?: (children: ReactNode) => ReactNode;
};

/**
 * Spec §4.1: where the month's income comes from and where it goes. A Sankey on a wide screen, two bars on a phone (and in
 * the narrow margin panel: `compact`); a negative margin cannot balance a flow, so it keeps the bars everywhere.
 */
export function HouseholdMonth({ d, scope, compact = false }: { d: DashboardView; scope: 'me' | 'household'; compact?: boolean }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { capabilities } = useSession();
  const [asTable, setAsTable] = useState(false);
  // Same query as the income panel's: one request for both.
  const overview = $api.useQuery('get', OVERVIEW, { params: { query: { scope } } }, { enabled: capabilities.modules.includes('income') });

  const mark = (key: string, name: string, money: string, pct: number | null | undefined, size: number, color: string, link?: Mark['link']): Mark => {
    const share = pct == null ? null : t('dashboard.month.pct', { value: percent(pct) });
    return { key, name, money, share, label: share ? `${name} ${money} · ${share}` : `${name} ${money}`, size, color, link };
  };
  const total = d.income.total;
  // Today's split of the home's income, largest first; « other » gathers the smallest sources.
  const now = overview.data?.nowSplit ?? [];
  const from = now.length > 0
    ? now.map((s) => {
      const { item, module } = s;
      const link: Mark['link'] = item == null ? undefined : (children) => module === 'realestate'
        ? <Link to="/properties/$id" params={{ id: item }} search={{ tab: 'rental' }}>{children}</Link>
        : <Link to="/income/$id" params={{ id: item }}>{children}</Link>;
      return item == null
        ? mark('other', t('dashboard.month.other'), formatMoney(s.perMonth), s.percent, toPlot(s.perMonth.amount), NEUTRAL_COLOR)
        : mark(item, s.name, formatMoney(s.perMonth), s.percent, toPlot(s.perMonth.amount), INCOME_COLOR, link);
    })
    // Without the income module (rents only) or its split: one block, the home's own income.
    : total ? [mark('income', t('dashboard.month.income'), formatMoney(total), null, toPlot(total.amount), INCOME_COLOR)] : [];
  const to = [
    ...d.commitmentSources.flatMap((s) => {
      const source = SOURCES.find((x) => x.key === s.source);
      return source && s.amortized
        ? [mark(source.key, t(`dashboard.commitment_name.${source.key}`), formatMoney(s.amortized), s.pct, toPlot(s.amortized.amount), source.color)]
        : [];
    }),
    ...(d.margin ? [mark('rest', t('dashboard.month.rest_name'), formatMoney(d.margin), d.marginPct, toPlot(d.margin.amount), 'var(--chart-6)')] : []),
  ];
  // The flow is the income: without it the marks have no whole to be a share of.
  const known = total != null && d.margin != null && to.length > 0;
  const flows = known && toPlot(d.margin!.amount) >= 0 && toPlot(total.amount) > 0;
  const missing = d.income.missingCurrency ?? d.commitments.missingCurrency;
  // Income unknown, else margin unknown (a commitment misses a rate): a drawing without its « Reste » would look complete.
  const reason = total == null
    ? (d.income.missingCurrency ? t('dashboard.unknown_rate', { code: d.income.missingCurrency }) : t('dashboard.income_unknown'))
    : (missing ? t('dashboard.margin_unknown_rate', { code: missing }) : t('dashboard.margin_unknown'));

  const bars = <Bars from={from} to={to} />;
  return (
    <SectionCard title={t('dashboard.month.title')} guide="dashboard.month" className="space-y-3"
      note={d.cashThisMonth.total ? t('dashboard.cash_this_month', { amount: formatMoney(d.cashThisMonth.total) }) : null}
      actions={known ? (
        <Button type="button" variant="outline" size="sm" onClick={() => setAsTable((v) => !v)}>
          {asTable ? t('common.show_chart') : t('common.show_table')}
        </Button>
      ) : null}>
      {!known ? <p className="text-sm text-muted-foreground">{reason}</p>
        : asTable ? <FlowTable from={from} to={to} />
        : flows && !compact ? (
          <>
            <div className="hidden md:block"><Sankey from={from} to={to} total={formatMoney(total)} /></div>
            <div className="md:hidden">{bars}</div>
          </>
        )
        : bars}
    </SectionCard>
  );
}

// Drawing units; the viewBox scales them, capped so the labels stay at their reading size.
const W = 820;
const LEFT = { x0: 196, x1: 206 };
const MID = { x0: 404, x1: 416 };
const RIGHT = { x0: 614, x1: 624 };
const TOP = 32;
const LINE = 18;

type Hover = { mark: Mark; x: number; y: number };

function Sankey({ from, to, total }: { from: Mark[]; to: Mark[]; total: string }) {
  const { t } = useTranslation();
  const [hover, setHover] = useState<Hover | null>(null);
  const h = Math.max(200, LINE * Math.max(from.length, to.length) + 24);
  const height = h + TOP + 12;
  const at = (s: Span) => ({ y0: s.y0 + TOP, y1: s.y1 + TOP });
  // Each side's nodes with gaps, and the same bands against the middle node without them, in the same order.
  const side = (marks: Mark[]) => {
    const nodes = stack(marks, h, 8);
    const inner = stack(marks, h, 0);
    const labels = spread(nodes.map((n) => (n.y0 + n.y1) / 2), h, LINE);
    return nodes.map((n, i) => ({ ...n, node: at(n), inner: at(inner[i]!), labelAt: labels[i]! + TOP + 4 }));
  };
  const left = side(from);
  const right = side(to);
  const rect = (x: { x0: number; x1: number }, s: Span, color: string) =>
    <rect x={x.x0} y={s.y0} width={x.x1 - x.x0} height={s.y1 - s.y0} rx={2} fill={color} />;
  // A mark's band, node and label make one hover target, bigger than the band alone; a link's focus shows it too.
  const target = (m: Mark, x: number, s: Span, children: ReactNode) => {
    const show = () => setHover({ mark: m, x, y: (s.y0 + s.y1) / 2 });
    const hide = () => setHover((v) => (v?.mark.key === m.key ? null : v));
    return <g key={m.key} onPointerEnter={show} onPointerLeave={hide} onFocus={show} onBlur={hide}>{children}</g>;
  };
  const label = (m: Mark, x: number, y: number, anchor: 'start' | 'end', share: boolean) => {
    const text = (
      <text x={x} y={y} textAnchor={anchor} fontSize={13} className={m.key === 'rest' ? 'fill-foreground font-semibold' : 'fill-foreground'}>
        {m.name} <tspan className="fill-muted-foreground font-normal">{share && m.share ? `${m.money} · ${m.share}` : m.money}</tspan>
      </text>
    );
    return m.link ? m.link(text) : text;
  };

  return (
    <div className="relative mx-auto w-full max-w-[52rem]">
      <svg viewBox={`0 0 ${W} ${height}`} className="block w-full" role="group" aria-label={t('dashboard.month.chart')}>
        <text x={(MID.x0 + MID.x1) / 2} y={18} textAnchor="middle" fontSize={13} className="fill-foreground font-semibold">
          {t('dashboard.month.per_month', { amount: total })}
        </text>
        {rect(MID, at({ y0: 0, y1: h }), 'var(--foreground)')}
        {/* The left labels have no room for the share: the tooltip and the table give it. */}
        {left.map((m) => target(m, LEFT.x1, m.node, (
          <>
            <path data-band={m.key} d={band(LEFT.x1, m.node, MID.x0, m.inner)} fill={m.color} fillOpacity={hover?.mark.key === m.key ? 0.4 : 0.22} />
            {rect(LEFT, m.node, m.color)}
            {label(m, LEFT.x0 - 8, m.labelAt, 'end', false)}
          </>
        )))}
        {right.map((m) => target(m, RIGHT.x0, m.node, (
          <>
            <path data-band={m.key} d={band(MID.x1, m.inner, RIGHT.x0, m.node)} fill={m.color} fillOpacity={hover?.mark.key === m.key ? 0.55 : 0.35} />
            {rect(RIGHT, m.node, m.color)}
            {label(m, RIGHT.x1 + 8, m.labelAt, 'start', true)}
          </>
        )))}
      </svg>
      {hover ? (
        <div role="tooltip" className="pointer-events-none absolute z-10 grid min-w-44 -translate-x-1/2 -translate-y-[calc(100%+8px)] gap-1 rounded-lg border bg-background px-2.5 py-1.5 text-xs shadow-xl"
          style={{ left: `${(hover.x / W) * 100}%`, top: `${(hover.y / height) * 100}%` }}>
          <div className="flex items-center gap-2 font-medium">
            <span aria-hidden="true" className="size-2.5 rounded-sm" style={{ background: hover.mark.color }} />
            {hover.mark.name}
          </div>
          <div className="flex justify-between gap-4"><span>{t('dashboard.month.col_per_month')}</span><span className="tabular-nums">{hover.mark.money}</span></div>
          {hover.mark.share
            ? <div className="flex justify-between gap-4"><span>{t('dashboard.month.col_share')}</span><span className="tabular-nums">{hover.mark.share}</span></div>
            : null}
        </div>
      ) : null}
    </div>
  );
}

/** The phone's flow: where the income comes from above where it goes, each bar with the server's amounts in its legend. */
function Bars({ from, to }: { from: Mark[]; to: Mark[] }) {
  const { t } = useTranslation();
  return (
    <div className="space-y-3">
      <Bar title={t('dashboard.month.from')} marks={from} attribute="data-source" />
      <Bar title={t('dashboard.month.to')} marks={to} attribute="data-segment" />
    </div>
  );
}

function Bar({ title, marks, attribute }: { title: string; marks: Mark[]; attribute: string }) {
  return (
    <div className="space-y-1.5">
      <p className="text-xs text-muted-foreground">{title}</p>
      {/* Widths are the amounts' plotting values as flex-grow: the browser shares the bar, we do no arithmetic. */}
      <div aria-hidden="true" className="flex h-3 w-full gap-0.5 overflow-hidden rounded-full bg-muted">
        {marks.filter((m) => m.size > 0).map((m) => (
          <div key={m.key} {...{ [attribute]: m.key }} style={{ flexGrow: m.size, flexBasis: 0, background: m.color }} />
        ))}
      </div>
      <ul aria-label={title} className="flex flex-wrap gap-x-5 gap-y-1 text-sm">
        {marks.map((m) => (
          <li key={m.key} className="flex items-center gap-2">
            <span aria-hidden="true" className="size-2.5 rounded-sm" style={{ background: m.color }} />
            <span className="tabular-nums">{m.link ? m.link(m.label) : m.label}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}

/** Exactly the drawn figures. */
function FlowTable({ from, to }: { from: Mark[]; to: Mark[] }) {
  const { t } = useTranslation();
  const rows = (title: string, marks: Mark[]) => (
    <>
      <TableRow>
        <TableHead scope="colgroup">{title}</TableHead>
        <TableHead className="text-right">{t('dashboard.month.col_per_month')}</TableHead>
        <TableHead className="text-right">{t('dashboard.month.col_share')}</TableHead>
      </TableRow>
      {marks.map((m) => (
        <TableRow key={m.key}>
          <TableHead scope="row" className="font-normal">{m.link ? m.link(m.name) : m.name}</TableHead>
          <TableCell className="text-right tabular-nums">{m.money}</TableCell>
          <TableCell className="text-right tabular-nums">{m.share ?? '—'}</TableCell>
        </TableRow>
      ))}
    </>
  );
  return (
    <Table aria-label={t('dashboard.month.table')}>
      <TableBody>
        {rows(t('dashboard.month.from'), from)}
        {rows(t('dashboard.month.to'), to)}
      </TableBody>
    </Table>
  );
}
