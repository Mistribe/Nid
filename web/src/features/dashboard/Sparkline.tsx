import { useId } from 'react';
import { useApi } from '@/api/ApiProvider';
import { toPlot } from '../investments/chart/plot';
import { NET_WORTH } from './api';

const W = 200;
const H = 56;

/**
 * The net worth over 12 months as a bare line beside the hero figure: a shape, not a reading (no axis, no label), so it
 * hides from assistive technology; the curve below and its table carry the figures. An unknown month is skipped.
 */
export function Sparkline({ scope }: { scope: 'me' | 'household' }) {
  const { $api } = useApi();
  const gradient = `spark-${useId().replace(/:/g, '')}`;
  const answer = $api.useQuery('get', NET_WORTH, { params: { query: { scope, period: '12m' } } });
  const values = (answer.data?.points ?? []).flatMap((p) => (p.value ? [toPlot(p.value.amount)] : []));
  if (values.length < 2) return null;
  const min = Math.min(...values);
  const span = Math.max(...values) - min || 1;
  const at = (v: number, i: number) => `${((i / (values.length - 1)) * W).toFixed(1)} ${(H - 4 - ((v - min) / span) * (H - 8)).toFixed(1)}`;
  const line = values.map((v, i) => `${i === 0 ? 'M' : 'L'}${at(v, i)}`).join(' ');
  return (
    <svg aria-hidden="true" viewBox={`0 0 ${W} ${H}`} preserveAspectRatio="none" className="h-14 w-full max-w-56 overflow-visible">
      <defs>
        <linearGradient id={gradient} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor="var(--primary)" stopOpacity={0.3} />
          <stop offset="1" stopColor="var(--primary)" stopOpacity={0} />
        </linearGradient>
      </defs>
      <path d={`${line} L${W} ${H} L0 ${H} Z`} fill={`url(#${gradient})`} className="animate-enter [animation-delay:300ms]" />
      <path d={line} fill="none" stroke="var(--primary)" strokeWidth={2} strokeLinejoin="round" strokeLinecap="round"
        vectorEffect="non-scaling-stroke" pathLength={1} className="animate-draw" />
    </svg>
  );
}
