import { useNavigate, useSearch } from '@tanstack/react-router';
import { useEffect, useRef, type ReactNode } from 'react';
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
    void navigate({ to: '.', replace: true, resetScroll: false, search: ((prev: Record<string, unknown>) => ({ ...prev, tab: next === ids[0] ? undefined : next, page: undefined })) as never });
  };
  return [current, set];
}

type DetailTab<T extends string> = { id: T; label: string; content: ReactNode; guide?: string; action?: ReactNode };

export function DetailTabs<T extends string>({ value, onChange, tabs, guide }: {
  value: T; onChange: (t: T) => void; tabs: DetailTab<T>[]; guide?: string;
}) {
  // The open tab's own action (a settings form's submit) sits at the right of the list, not at the bottom of a long tab.
  const action = tabs.find((tab) => tab.id === value)?.action;
  // On a phone the list scrolls inside itself: the open tab, from the URL or picked, stays in its view.
  const list = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const l = list.current;
    const tab = l?.querySelector<HTMLElement>('[data-state="active"]');
    if (!l || !tab) return;
    // Sideways only, inside the list: `scrollIntoView` would also scroll the page down to a list below the fold.
    const t = tab.getBoundingClientRect();
    const b = l.getBoundingClientRect();
    if (t.left < b.left || t.right > b.right) l.scrollLeft += t.left - b.left - (b.width - t.width) / 2;
  }, [value]);
  return (
    <Tabs value={value} onValueChange={(next) => onChange(next as T)}>
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2 print:hidden">
        <TabsList ref={list} variant="line" data-guide={guide} className="h-auto min-w-0 max-w-full justify-start overflow-x-auto overflow-y-hidden pb-1.5">
          {tabs.map((tab) => <TabsTrigger key={tab.id} value={tab.id} data-guide={tab.guide} className="flex-none">{tab.label}</TabsTrigger>)}
        </TabsList>
        {action ? <div className="ml-auto max-w-full flex-none">{action}</div> : null}
      </div>
      {tabs.map((tab) => (
        <TabsContent key={tab.id} value={tab.id} className="animate-enter space-y-6 pt-4">
          {tab.content}
        </TabsContent>
      ))}
    </Tabs>
  );
}
