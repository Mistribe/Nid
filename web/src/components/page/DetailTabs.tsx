import { useNavigate, useSearch } from '@tanstack/react-router';
import type { ReactNode } from 'react';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';

/** The `tab` of the URL, validated against the screen's own ids: unknown or absent is the first, which stays out of the URL. */
export function useDetailTab<T extends string>(ids: readonly T[]): [T, (next: T) => void] {
  const { tab } = useSearch({ strict: false });
  const navigate = useNavigate();
  const current = ids.find((id) => id === tab) ?? ids[0]!;
  const set = (next: T) => {
    void navigate({ to: '.', replace: true, search: ((prev: Record<string, unknown>) => ({ ...prev, tab: next === ids[0] ? undefined : next })) as never });
  };
  return [current, set];
}

export function DetailTabs<T extends string>({ value, onChange, tabs, guide }: {
  value: T; onChange: (t: T) => void; tabs: { id: T; label: string; content: ReactNode; guide?: string }[]; guide?: string;
}) {
  return (
    <Tabs value={value} onValueChange={(next) => onChange(next as T)}>
      <TabsList variant="line" data-guide={guide} className="h-auto max-w-full justify-start print:hidden overflow-x-auto overflow-y-hidden pb-1.5">
        {tabs.map((tab) => <TabsTrigger key={tab.id} value={tab.id} data-guide={tab.guide} className="flex-none">{tab.label}</TabsTrigger>)}
      </TabsList>
      {tabs.map((tab) => <TabsContent key={tab.id} value={tab.id} className="animate-enter space-y-6 pt-4">{tab.content}</TabsContent>)}
    </Tabs>
  );
}
