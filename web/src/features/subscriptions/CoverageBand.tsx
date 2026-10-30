import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { formatLocalDate, LocalDate } from '@/components/LocalDate';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import type { Coverage } from './api';
import { lastDay, sourceText } from './format';
import '@/locales/subscriptions';

const WIDTH = 1000;
const HEIGHT = 40;

/** Each source has its own tone of the fixed chart palette (a source the screen does not know takes the last one). */
const TONES: Record<string, string> = { paid: 'var(--chart-1)', bonus: 'var(--chart-2)', included: 'var(--chart-3)', trial: 'var(--chart-4)', gift: 'var(--chart-5)' };
const toneOf = (source: string) => TONES[source] ?? 'var(--chart-8)';

/** Geometry as well as colour tells the sources apart (dataviz.md §5): every pattern is drawn in currentColor, the source's tone. */
function Pattern({ id, source }: { id: string; source: string }) {
  const common = { id, width: 8, height: 8, patternUnits: 'userSpaceOnUse', style: { color: toneOf(source) } } as const;
  switch (source) {
    case 'bonus':
      return <pattern {...common}><path d="M-2,2 l4,-4 M0,8 l8,-8 M6,10 l4,-4" stroke="currentColor" strokeWidth={1.5} /></pattern>;
    case 'included':
      return <pattern {...common}><circle cx={4} cy={4} r={1.6} fill="currentColor" /></pattern>;
    case 'trial':
      return <pattern {...common}><path d="M0,0 L8,8 M8,0 L0,8" stroke="currentColor" strokeWidth={1} /></pattern>;
    case 'gift':
      return <pattern {...common}><path d="M0,2 H8 M0,6 H8" stroke="currentColor" strokeWidth={1.5} /></pattern>;
    default:
      return <pattern {...common}><rect width={8} height={8} fill="currentColor" /></pattern>;
  }
}

const day = (iso: string) => Date.UTC(Number(iso.slice(0, 4)), Number(iso.slice(5, 7)) - 1, Number(iso.slice(8, 10)));

/** 1E2a design §4: the band in custom SVG, and the same periods as a table. */
export function CoverageBand({ coverage }: { coverage: Coverage[] }) {
  const { t } = useTranslation();
  const captionId = useId();
  if (coverage.length === 0) return <p>{t('coverage.none')}</p>;

  const start = Math.min(...coverage.map((c) => day(c.start)));
  const end = Math.max(...coverage.map((c) => day(c.endBefore)));
  const x = (iso: string) => ((day(iso) - start) / Math.max(1, end - start)) * WIDTH;
  const sources = [...new Set(coverage.map((c) => c.source))];
  const label = (c: Coverage) =>
    t('coverage.segment', { source: sourceText(t, c.source), start: formatLocalDate(c.start), end: formatLocalDate(lastDay(c.endBefore)) });

  return (
    <div className="space-y-2">
      <svg role="img" aria-labelledby={captionId} viewBox={`0 0 ${WIDTH} ${HEIGHT}`} className="h-10 w-full" preserveAspectRatio="none">
        <title id={captionId}>{t('subscription.coverage')}</title>
        <defs>{sources.map((s) => <Pattern key={s} id={`coverage-${s}`} source={s} />)}</defs>
        {coverage.map((c) => (
          <rect key={`${c.start}-${c.source}`} data-source={c.source} x={x(c.start)} y={4} width={Math.max(1, x(c.endBefore) - x(c.start))}
            height={HEIGHT - 8} fill={`url(#coverage-${c.source})`} stroke={toneOf(c.source)}
            {...(c.isProjected ? { strokeDasharray: '4 2', fillOpacity: 0.5 } : {})}>
            <title>{label(c)}</title>
          </rect>
        ))}
      </svg>
      <ul aria-label={t('coverage.legend')} className="flex flex-wrap gap-3 text-sm">
        {sources.map((s) => (
          <li key={s} className="inline-flex items-center gap-1">
            <svg aria-hidden="true" width={16} height={16}>
              <defs><Pattern id={`legend-${s}`} source={s} /></defs>
              <rect width={16} height={16} fill={`url(#legend-${s})`} stroke={toneOf(s)} />
            </svg>
            {sourceText(t, s)}
          </li>
        ))}
      </ul>
      <Table aria-label={t('coverage.table')}>
        <TableHeader>
          <TableRow>
            <TableHead>{t('subscription.col_start')}</TableHead>
            <TableHead>{t('subscription.col_end')}</TableHead>
            <TableHead>{t('coverage.col_source')}</TableHead>
            <TableHead>{t('subscription.col_state')}</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {coverage.map((c) => (
            <TableRow key={`${c.start}-${c.source}`}>
              <TableCell><LocalDate value={c.start} /></TableCell>
              <TableCell><LocalDate value={lastDay(c.endBefore)} /></TableCell>
              <TableCell>{sourceText(t, c.source)}</TableCell>
              <TableCell>{c.isProjected ? t('subscription.projected') : t('subscription.past')}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}
