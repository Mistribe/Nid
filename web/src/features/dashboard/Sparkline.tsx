import { useApi } from '@/api/ApiProvider';
import { HeroCurve } from '@/components/page/Hero';
import { toPlot } from '@/lib/plot';
import { NET_WORTH } from '@/api/dashboard';

/** The net worth over 12 months beside the hero figure; an unknown month is skipped. */
export function Sparkline({ scope }: { scope: 'me' | 'household' }) {
  const { $api } = useApi();
  const answer = $api.useQuery('get', NET_WORTH, { params: { query: { scope, period: '12m' } } });
  return <HeroCurve values={(answer.data?.points ?? []).flatMap((p) => (p.value ? [toPlot(p.value.amount)] : []))} />;
}
