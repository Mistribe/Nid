import { useQuery } from '@tanstack/react-query';
import { Link, useLocation } from '@tanstack/react-router';
import { ChevronRightIcon } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { formatLocalDate } from '@/components/LocalDate';
import { PageHeader } from '@/components/page/PageHeader';
import { StatLine } from '@/components/page/StatLine';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from '@/components/ui/collapsible';
import { Skeleton } from '@/components/ui/skeleton';
import { cn } from '@/lib/utils';

type Text = Record<string, string>;
export type Version = { version: string; date: string; changes: Text[] };
type Group = { minor: string; headline: Version; patches: Version[] };

/** The groups shown open; the older ones fold under « Versions précédentes ». */
const OPEN_GROUPS = 3;

/**
 * public/changelog.json, written by scripts/changelog.mjs at image build. A dev server has none: it answers with the SPA,
 * which is no list, so the page says there is nothing to show.
 */
async function fetchChangelog(): Promise<Version[]> {
  const response = await fetch('/changelog.json', { headers: { Accept: 'application/json' } });
  if (!response.ok || !response.headers.get('Content-Type')?.includes('json')) return [];
  const body: unknown = await response.json();
  return Array.isArray(body) ? (body as Version[]) : [];
}

/**
 * The versions (newest first) by minor release, in their order: the `x.y.0` heads its group, its patches follow it; a group
 * without its `x.y.0` (the list starts after it) is headed by its oldest version.
 */
export function groupByMinor(versions: Version[]): Group[] {
  const groups: { minor: string; versions: Version[] }[] = [];
  for (const v of versions) {
    const minor = v.version.split('.').slice(0, 2).join('.');
    const last = groups.at(-1);
    if (last?.minor === minor) last.versions.push(v); else groups.push({ minor, versions: [v] });
  }
  return groups.map(({ minor, versions: list }) => {
    const headline = list.find((v) => v.version.split('.')[2] === '0') ?? list.at(-1)!;
    return { minor, headline, patches: list.filter((v) => v !== headline) };
  });
}

/** screens.md §1, « Nouveautés »: what changed in each version, the running one marked, each version linkable (`#0.12.0`). */
export function ChangelogPage() {
  const { t, i18n } = useTranslation();
  const { $api } = useApi();
  const list = useQuery({ queryKey: ['changelog'], queryFn: fetchChangelog, staleTime: Infinity });
  const api = $api.useQuery('get', '/api/v1/platform/version', {}, { staleTime: Infinity });
  const hash = useLocation({ select: (l) => l.hash });
  const current = import.meta.env.VITE_NID_VERSION ?? 'dev';
  const [olderOpen, setOlderOpen] = useState(false);
  const groups = groupByMinor(list.data ?? []);
  const target = hash ? groups.findIndex((g) => g.headline.version === hash || g.patches.some((p) => p.version === hash)) : -1;
  // A link to a folded version opens the older ones, then brings it into view once, when it is on screen.
  useEffect(() => { if (target >= OPEN_GROUPS) setOlderOpen(true); }, [target]);
  const scrolled = useRef<string | null>(null);
  useEffect(() => {
    const element = hash && scrolled.current !== hash ? document.getElementById(hash) : null;
    if (!element) return;
    scrolled.current = hash;
    element.scrollIntoView({ block: 'start' });
  }, [hash, target, olderOpen]);

  const line = (text: Text) => text[i18n.language] ?? text.fr ?? Object.values(text)[0];
  const date = (v: Version) => <time dateTime={v.date}>{formatLocalDate(v.date, 'long', i18n.language)}</time>;
  const mark = (v: Version) => (v.version === current ? <Badge variant="secondary">{t('app.changelog.current')}</Badge> : null);
  // The version's own link, to share it: the anchor is its heading's id.
  const anchor = (v: Version) => <Link to="/changelog" hash={v.version} className="hover:underline">{t('app.version', { version: v.version })}</Link>;
  const changes = (v: Version, small: boolean) => (
    <ul className={cn('space-y-1.5', small && 'text-sm')}>
      {v.changes.map((change, i) => <li key={i}>{line(change)}</li>)}
    </ul>
  );
  const group = (g: Group) => (
    <section key={g.minor} aria-labelledby={g.headline.version} className="space-y-4 border-t pt-5 first:border-t-0 first:pt-0">
      <div className="space-y-2">
        <h2 id={g.headline.version} className="flex flex-wrap items-baseline gap-x-3 gap-y-1 scroll-mt-20">
          <span className="text-title">{anchor(g.headline)}</span>
          <span className="text-sm text-muted-foreground">{date(g.headline)}</span>
          {mark(g.headline)}
        </h2>
        {changes(g.headline, false)}
      </div>
      {g.patches.length > 0 ? (
        <ol aria-label={t('app.changelog.patches', { minor: g.minor })} className="space-y-3 border-l-2 pl-4">
          {g.patches.map((p) => (
            <li key={p.version} className="space-y-1">
              <h3 id={p.version} className="flex flex-wrap items-baseline gap-x-3 gap-y-1 scroll-mt-20 text-sm">
                <span className="font-semibold">{anchor(p)}</span>
                <span className="text-muted-foreground">{date(p)}</span>
                {mark(p)}
              </h3>
              {changes(p, true)}
            </li>
          ))}
        </ol>
      ) : null}
    </section>
  );

  const older = groups.slice(OPEN_GROUPS);
  return (
    <article className="max-w-3xl space-y-6">
      <PageHeader title={t('app.changelog.title')}>
        <StatLine items={[
          { label: t('app.changelog.web'), value: current },
          { label: t('app.changelog.api'), value: api.data?.version ?? (api.isError ? t('app.api_version_unknown') : '…') },
        ]} />
      </PageHeader>
      {list.isPending ? (
        <div role="status" aria-busy="true" aria-label={t('app.changelog.loading')} className="space-y-3">
          <Skeleton className="h-6 w-48" /><Skeleton className="h-4 w-full" /><Skeleton className="h-4 w-2/3" />
        </div>
      ) : list.isError ? <p className="text-sm text-muted-foreground">{t('app.changelog.error')}</p>
        : groups.length === 0 ? <p className="text-sm text-muted-foreground">{t('app.changelog.empty')}</p>
        : (
          <div className="space-y-5">
            {groups.slice(0, OPEN_GROUPS).map(group)}
            {older.length > 0 ? (
              <Collapsible open={olderOpen} onOpenChange={setOlderOpen} className="space-y-5 border-t pt-5">
                <CollapsibleTrigger asChild>
                  <Button type="button" variant="ghost" size="sm" className="group -ml-3">
                    <ChevronRightIcon aria-hidden="true" className="transition-transform group-data-[state=open]:rotate-90" />
                    {t('app.changelog.older', { count: older.length })}
                  </Button>
                </CollapsibleTrigger>
                <CollapsibleContent className="space-y-5">{older.map(group)}</CollapsibleContent>
              </Collapsible>
            ) : null}
          </div>
        )}
    </article>
  );
}
