import { deepEqual, Link, useRouterState } from '@tanstack/react-router';
import { ChevronLeft } from 'lucide-react';
import { Fragment } from 'react';
import { useTranslation } from 'react-i18next';
import { Wordmark } from '@/components/Logo';
import { Breadcrumb, BreadcrumbItem, BreadcrumbLink, BreadcrumbList, BreadcrumbPage, BreadcrumbSeparator } from '@/components/ui/breadcrumb';
import { useTrail, type Crumb } from '@/components/page/trail';
import { moduleOf } from '@/components/nav';

/**
 * The page's trail in the top bar; on a phone, « ‹ Parent » or the wordmark, its mark alone under 400 px where the person menu,
 * the bell and « Foyer / Moi » leave it no room (issue 374). No ellipsis: no page has more than two ancestors.
 */
export function TopBarTrail() {
  const { t } = useTranslation();
  const trail = useTrail();
  const pathname = useRouterState({ select: (s) => s.matches.at(-1)?.pathname ?? s.location.pathname });
  const module = moduleOf(pathname);
  const moduleLabel = module ? t(module.label) : undefined;
  const current = trail ? trail.current : moduleLabel;
  const ancestors: Crumb[] = trail?.crumbs
    ?? (module && moduleLabel !== current ? [{ label: moduleLabel!, to: module.to }] : []);
  const search = useRouterState({ select: (s) => s.location.search });
  // An ancestor that is the page itself (« Réglages » on /settings/appearance, same search) is plain text, not a link to here.
  const isSelf = (c: Crumb) => c.to === pathname && !c.params && (c.search === undefined || deepEqual(c.search, search));
  const last = ancestors.at(-1);
  const parent = last && !isSelf(last) ? last : undefined;
  return (
    <>
      <Breadcrumb className="hidden min-w-0 md:block md:flex-[1_1_auto]">
        <BreadcrumbList className="min-w-0 flex-nowrap">
          {ancestors.map((crumb, i) => (
            <Fragment key={crumb.label}>
              {i > 0 ? <BreadcrumbSeparator /> : null}
              <BreadcrumbItem className="min-w-0 shrink-[0.1]">
                {isSelf(crumb)
                  ? <span className="truncate max-w-[16rem]">{crumb.label}</span>
                  : <BreadcrumbLink asChild><Link to={crumb.to} params={crumb.params} search={crumb.search} activeOptions={{ exact: true }} className="truncate max-w-[16rem]">{crumb.label}</Link></BreadcrumbLink>}
              </BreadcrumbItem>
            </Fragment>
          ))}
          {current ? <>{ancestors.length > 0 ? <BreadcrumbSeparator /> : null}<BreadcrumbItem className="min-w-0"><BreadcrumbPage className="truncate max-w-[24rem]">{current}</BreadcrumbPage></BreadcrumbItem></> : null}
        </BreadcrumbList>
      </Breadcrumb>
      {parent
        ? <Link to={parent.to} params={parent.params} search={parent.search} activeOptions={{ exact: true }} aria-label={t('common.back_to', { label: parent.label })} className="flex h-9 min-w-0 items-center gap-0.5 rounded-md px-1 text-sm font-medium outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50 pointer-coarse:h-11 md:hidden"><ChevronLeft aria-hidden="true" className="size-4 shrink-0" /><span aria-hidden="true" className="truncate">{parent.label}</span></Link>
        : <Wordmark className="h-6 md:hidden max-[25rem]:[&_[data-wordmark]]:hidden" />}
    </>
  );
}
