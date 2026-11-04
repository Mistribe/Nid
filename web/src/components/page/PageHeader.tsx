import { Link, type LinkProps } from '@tanstack/react-router';
import { Fragment, type ReactNode, type Ref } from 'react';
import { Breadcrumb, BreadcrumbItem, BreadcrumbLink, BreadcrumbList, BreadcrumbSeparator } from '@/components/ui/breadcrumb';

/** The top of a screen: breadcrumb, title with its badges, actions (under the title on a phone), then a stats line. */
export function PageHeader({ crumbs, title, titleRef, meta, actions, children }: {
  /** A title the screen moves focus to (after a decision) is focusable, never in the tab order. */
  titleRef?: Ref<HTMLHeadingElement>; crumbs?: { label: string; to: LinkProps['to']; params?: LinkProps['params']; search?: LinkProps['search'] }[]; title: ReactNode; meta?: ReactNode; actions?: ReactNode; children?: ReactNode;
}) {
  return (
    <header data-page-header className="space-y-3">
      {crumbs?.length ? (
        <Breadcrumb>
          <BreadcrumbList>
            {crumbs.map((crumb, i) => (
              <Fragment key={crumb.label}>
                {i > 0 ? <BreadcrumbSeparator /> : null}
                <BreadcrumbItem><BreadcrumbLink asChild><Link to={crumb.to} params={crumb.params} search={crumb.search}>{crumb.label}</Link></BreadcrumbLink></BreadcrumbItem>
              </Fragment>
            ))}
          </BreadcrumbList>
        </Breadcrumb>
      ) : null}
      <div className="flex flex-wrap items-center justify-between gap-x-4 gap-y-2">
        <div className="flex min-w-0 flex-wrap items-center gap-2">
          <h1 ref={titleRef} tabIndex={titleRef ? -1 : undefined} className="text-large-title break-words outline-none">{title}</h1>
          {meta}
        </div>
        {actions ? <div data-page-actions className="flex flex-wrap items-center gap-2">{actions}</div> : null}
      </div>
      {children}
    </header>
  );
}
