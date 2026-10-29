import { useNavigate, useSearch } from '@tanstack/react-router';
import type { ReactNode } from 'react';
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
    void navigate({ to: '.', replace: true, resetScroll: false, search: ((prev: Record<string, unknown>) => ({ ...prev, tab: next === ids[0] ? undefined : next })) as never });
  };
  return [current, set];
}

type DetailTab<T extends string> = {
  id: T; label: string; content: ReactNode; guide?: string; action?: ReactNode;
  /** False: no trigger in the row, the tab is opened from elsewhere (a button of the page header); its label titles it. */ trigger?: boolean;
};

export function DetailTabs<T extends string>({ value, onChange, tabs, guide }: {
  value: T; onChange: (t: T) => void; tabs: DetailTab<T>[]; guide?: string;
}) {
  // The open tab's own action (a settings form's submit) sits at the right of the list, not at the bottom of a long tab.
  const action = tabs.find((tab) => tab.id === value)?.action;
  return (
    <Tabs value={value} onValueChange={(next) => onChange(next as T)}>
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2 print:hidden">
        <TabsList variant="line" data-guide={guide} className="h-auto min-w-0 max-w-full justify-start overflow-x-auto overflow-y-hidden pb-1.5">
          {tabs.filter((tab) => tab.trigger !== false).map((tab) => <TabsTrigger key={tab.id} value={tab.id} data-guide={tab.guide} className="flex-none">{tab.label}</TabsTrigger>)}
        </TabsList>
        {action ? <div className="ml-auto flex-none">{action}</div> : null}
      </div>
      {tabs.map((tab) => (
        <TabsContent key={tab.id} value={tab.id} aria-label={tab.trigger === false ? tab.label : undefined} className="animate-enter space-y-6 pt-4">
          {tab.trigger === false ? <h2 className="text-xl font-semibold">{tab.label}</h2> : null}
          {tab.content}
        </TabsContent>
      ))}
    </Tabs>
  );
}
