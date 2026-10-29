import { useNavigate, useSearch } from '@tanstack/react-router';
import { useId, type ReactNode } from 'react';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';

/**
 * The `tab` of the URL, validated against the screen's own ids: unknown or absent is the first, which stays out of the URL.
 * A tab change keeps the scroll: the page does not jump back to its top under the pointer.
 */
export function useDetailTab<T extends string>(ids: readonly T[]): [T, (next: T) => void] {
  const { tab } = useSearch({ strict: false });
  const navigate = useNavigate();
  const current = ids.find((id) => id === tab) ?? ids[0]!;
  const set = (next: T) => {
    void navigate({ to: '.', replace: true, resetScroll: false, search:((prev: Record<string, unknown>) => ({ ...prev, tab: next === ids[0] ? undefined : next })) as never });
  };
  return [current, set];
}

type DetailTab<T extends string> = { id: T; label: string; content: ReactNode; guide?: string; action?: ReactNode; /** The side menu's group heading. */ group?: string };

/**
 * A screen's tabs: a row above the content, or with `side` a menu on its left, the tabs grouped under their `group` headings,
 * and the open tab's label as the content's title (the menu stacks above it on a phone).
 */
export function DetailTabs<T extends string>({ value, onChange, tabs, guide, side }: {
  value: T; onChange: (t: T) => void; tabs: DetailTab<T>[]; guide?: string; side?: boolean;
}) {
  // The open tab's own action (a settings form's submit) sits at the right of the list, not at the bottom of a long tab.
  const open = tabs.find((tab) => tab.id === value);
  const action = open?.action;
  const contents = tabs.map((tab) => <TabsContent key={tab.id} value={tab.id} className="animate-enter space-y-6 pt-4">{tab.content}</TabsContent>);
  if (side) {
    const groups = [...new Set(tabs.map((tab) => tab.group ?? ''))];
    return (
      // At least a screen tall: a short tab opened from low on the page keeps the scroll, so the menu stays under the pointer.
      <Tabs value={value} onValueChange={(next) => onChange(next as T)} orientation="vertical" className="min-h-dvh flex-row flex-wrap items-start gap-x-10 gap-y-6">
        <nav data-guide={guide} className="flex max-w-full flex-[1_1_12rem] flex-col gap-5 sm:max-w-56 print:hidden">
          {groups.map((group) => <SideGroup key={group} title={group} tabs={tabs.filter((tab) => (tab.group ?? '') === group)} />)}
        </nav>
        <div className="min-w-0 flex-[999_1_35rem]">
          <div className="flex flex-wrap items-center gap-x-3 gap-y-2 print:hidden">
            <h2 className="text-xl font-semibold">{open?.label}</h2>
            {action ? <div className="ml-auto flex-none">{action}</div> : null}
          </div>
          {contents}
        </div>
      </Tabs>
    );
  }
  return (
    <Tabs value={value} onValueChange={(next) => onChange(next as T)}>
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2 print:hidden">
        <TabsList variant="line" data-guide={guide} className="h-auto min-w-0 max-w-full justify-start overflow-x-auto overflow-y-hidden pb-1.5">
          {tabs.map((tab) => <TabsTrigger key={tab.id} value={tab.id} data-guide={tab.guide} className="flex-none">{tab.label}</TabsTrigger>)}
        </TabsList>
        {action ? <div className="ml-auto flex-none">{action}</div> : null}
      </div>
      {contents}
    </Tabs>
  );
}

function SideGroup<T extends string>({ title, tabs }: { title: string; tabs: DetailTab<T>[] }) {
  const id = useId();
  return (
    <div className="flex flex-col gap-1">
      {title ? <p id={id} className="px-3 text-xs font-semibold uppercase tracking-wide text-muted-foreground">{title}</p> : null}
      <TabsList variant="line" aria-labelledby={title ? id : undefined} className="w-full items-stretch gap-0.5 p-0">
        {tabs.map((tab) => (
          <TabsTrigger key={tab.id} value={tab.id} data-guide={tab.guide}
            className="h-10 px-3 font-normal after:hidden data-[state=active]:bg-primary/10 data-[state=active]:font-medium group-data-[variant=line]/tabs-list:data-[state=active]:bg-primary/10">
            {tab.label}
          </TabsTrigger>
        ))}
      </TabsList>
    </div>
  );
}
