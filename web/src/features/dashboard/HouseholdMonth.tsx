import { Link } from '@tanstack/react-router';
import { useId, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { useSession } from '@/app/Providers';
import { formatMoney } from '@/components/Money';
import { NEUTRAL_COLOR } from '@/components/ProviderIcon';
import { SectionCard } from '@/components/page/SectionCard';
import { Table, TableBody, TableCell, TableHead, TableRow } from '@/components/ui/table';
import { cn } from '@/lib/utils';
import { OVERVIEW } from '../income/api';
import { toPlot } from '../investments/chart/plot';
import type { DashboardView } from './api';
import { band, spread, stack, type Span } from './flow';
import { percent } from './format';
import { TableToggle } from '@/components/page/TableToggle';

const SOURCES = [
  { key: 'subscriptions', color: 'var(--chart-1)' },
  { key: 'loans', color: 'var(--chart-2)' },
  { key: 'property_costs', color: 'var(--chart-3)' },
  { key: 'tax', color: 'var(--chart-4)' },
  { key: 'childcare', color: 'var(--chart-5)' },
] as const;
// The income in the theme's accent: what comes in reads apart from where it goes (the commitments' fixed chart colours).
const INCOME_COLOR = 'var(--primary)';

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
        <TableToggle asTable={asTable} onToggle={() => setAsTable((v) => !v)} />
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

// Drawing units across; the drawing stretches to its column (`preserveAspectRatio="none"`), one unit high is one pixel.
// The labels are HTML beside it: they truncate instead of running off the edge, and keep their reading size.
const W = 400;
const LEFT = { x0: 0, x1: 8 };
const MID = { x0: 196, x1: 204 };
const RIGHT = { x0: 392, x1: 400 };
const LINE = 24;
const MID_COLOR = 'var(--muted-foreground)';

type Hover = { mark: Mark; x: number; y: number };
type Placed = Mark & { node: Span; inner: Span; labelAt: number };

function Sankey({ from, to, total }: { from: Mark[]; to: Mark[]; total: string }) {
  const { t } = useTranslation();
  const gradient = useId().replace(/:/g, '');
  const [hover, setHover] = useState<Hover | null>(null);
  const h = Math.max(200, LINE * Math.max(from.length, to.length) + 24);
  // Each side's nodes with gaps, and the same bands against the middle node without them, in the same order.
  const side = (marks: Mark[]): Placed[] => {
    const nodes = stack(marks, h, 8);
    const inner = stack(marks, h, 0);
    const labels = spread(nodes.map((n) => (n.y0 + n.y1) / 2), h, LINE);
    return nodes.map((n, i) => ({ ...n, node: n, inner: inner[i]!, labelAt: labels[i]! }));
  };
  const left = side(from);
  const right = side(to);
  const rect = (x: { x0: number; x1: number }, s: Span, color: string) =>
    <rect x={x.x0} y={s.y0} width={x.x1 - x.x0} height={s.y1 - s.y0} rx={2} fill={color} />;
  // A mark's band and its label are each a hover target; a link's focus shows it too.
  const handlers = (m: Placed, x: number) => {
    const show = () => setHover({ mark: m, x, y: (m.node.y0 + m.node.y1) / 2 });
    const hide = () => setHover((v) => (v?.mark.key === m.key ? null : v));
    return { onPointerEnter: show, onPointerLeave: hide, onFocus: show, onBlur: hide };
  };
  const labels = (marks: Placed[], x: number, align: 'start' | 'end') => (
    <div className="relative">
      {marks.map((m) => (
        <p key={m.key} {...handlers(m, x)} style={{ top: m.labelAt }}
          className={cn('absolute inset-x-0 flex -translate-y-1/2 items-baseline gap-1.5 text-sm whitespace-nowrap [&_a:hover]:underline',
            align === 'end' ? 'justify-end pr-3 text-right' : 'pl-3', m.key === 'rest' && 'font-semibold')}>
          <span className="truncate">{m.link ? m.link(m.name) : m.name}</span>{' '}
          <span className="shrink-0 font-normal text-muted-foreground tabular-nums">{m.share ? `${m.money} · ${m.share}` : m.money}</span>
        </p>
      ))}
    </div>
  );

  return (
    <div role="group" aria-label={t('dashboard.month.chart')}>
      <p className="mb-2 text-center text-sm font-semibold">{t('dashboard.month.per_month', { amount: total })}</p>
      <div className="grid grid-cols-[minmax(0,1fr)_minmax(12rem,1.4fr)_minmax(0,1fr)]" style={{ height: h }}>
        {labels(left, LEFT.x1, 'end')}
        <div className="relative">
          <svg viewBox={`0 0 ${W} ${h}`} preserveAspectRatio="none" aria-hidden="true" className="block w-full animate-reveal" style={{ height: h }}>
            <defs>
              {/* Each band fades from where it starts to where it goes. */}
              {left.map((m) => (
                <linearGradient key={m.key} id={`${gradient}-${m.key}`} gradientUnits="userSpaceOnUse" x1={LEFT.x1} x2={MID.x0} y1={0} y2={0}>
                  <stop offset="0" stopColor={m.color} /><stop offset="1" stopColor={MID_COLOR} />
                </linearGradient>
              ))}
              {right.map((m) => (
                <linearGradient key={m.key} id={`${gradient}-${m.key}`} gradientUnits="userSpaceOnUse" x1={MID.x1} x2={RIGHT.x0} y1={0} y2={0}>
                  <stop offset="0" stopColor={MID_COLOR} /><stop offset="1" stopColor={m.color} />
                </linearGradient>
              ))}
            </defs>
            {rect(MID, { y0: 0, y1: h }, MID_COLOR)}
            {left.map((m) => (
              <g key={m.key} {...handlers(m, LEFT.x1)}>
                <path data-band={m.key} d={band(LEFT.x1, m.node, MID.x0, m.inner)} fill={`url(#${gradient}-${m.key})`}
                  fillOpacity={hover?.mark.key === m.key ? 0.5 : 0.25} className="transition-[fill-opacity] duration-200" />
                {rect(LEFT, m.node, m.color)}
              </g>
            ))}
            {right.map((m) => (
              <g key={m.key} {...handlers(m, RIGHT.x0)}>
                <path data-band={m.key} d={band(MID.x1, m.inner, RIGHT.x0, m.node)} fill={`url(#${gradient}-${m.key})`}
                  fillOpacity={hover?.mark.key === m.key ? 0.7 : 0.45} className="transition-[fill-opacity] duration-200" />
                {rect(RIGHT, m.node, m.color)}
              </g>
            ))}
          </svg>
          {hover ? (
            <div role="tooltip" className="pointer-events-none absolute z-10 grid min-w-44 -translate-x-1/2 -translate-y-[calc(100%+8px)] gap-1 rounded-lg border bg-popover/95 px-3 py-2 text-xs shadow-raised backdrop-blur-sm"
              style={{ left: `${(hover.x / W) * 100}%`, top: hover.y }}>
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
        {labels(right, RIGHT.x0, 'start')}
      </div>
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
