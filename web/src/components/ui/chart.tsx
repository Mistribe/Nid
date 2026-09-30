import * as React from 'react';
import * as RechartsPrimitive from 'recharts';
import { cn } from '@/lib/utils';

// Format: { THEME_NAME: CSS_SELECTOR }
const THEMES = { light: '', dark: '.dark' } as const;

export type ChartConfig = {
  [k in string]: {
    label?: React.ReactNode;
    icon?: React.ComponentType;
  } & (
    | { color?: string; theme?: never }
    | { color?: never; theme: Record<keyof typeof THEMES, string> }
  );
};

type ChartContextProps = { config: ChartConfig };

const ChartContext = React.createContext<ChartContextProps | null>(null);

function useChart() {
  const context = React.useContext(ChartContext);
  if (!context) throw new Error('useChart must be used within a <ChartContainer />');
  return context;
}

function ChartContainer({ id, className, children, config, ...props }: React.ComponentProps<'div'> & {
  config: ChartConfig;
  children: React.ComponentProps<typeof RechartsPrimitive.ResponsiveContainer>['children'];
}) {
  const uniqueId = React.useId();
  const chartId = `chart-${id ?? uniqueId.replace(/:/g, '')}`;

  return (
    <ChartContext.Provider value={{ config }}>
      <div
        data-slot="chart"
        data-chart={chartId}
        className={cn(
          "flex aspect-video justify-center text-xs [&_.recharts-cartesian-axis-tick_text]:fill-muted-foreground [&_.recharts-cartesian-grid_line[stroke='#ccc']]:stroke-border/50 [&_.recharts-curve.recharts-tooltip-cursor]:stroke-border [&_.recharts-dot[stroke='#fff']]:stroke-transparent [&_.recharts-layer]:outline-hidden [&_.recharts-polar-grid_[stroke='#ccc']]:stroke-border [&_.recharts-radial-bar-background-sector]:fill-muted [&_.recharts-rectangle.recharts-tooltip-cursor]:fill-muted [&_.recharts-reference-line_[stroke='#ccc']]:stroke-border [&_.recharts-sector]:outline-hidden [&_.recharts-sector[stroke='#fff']]:stroke-transparent [&_.recharts-surface]:outline-hidden",
          className,
        )}
        {...props}
      >
        <ChartStyle id={chartId} config={config} />
        <RechartsPrimitive.ResponsiveContainer>{children}</RechartsPrimitive.ResponsiveContainer>
      </div>
    </ChartContext.Provider>
  );
}

const ChartStyle = ({ id, config }: { id: string; config: ChartConfig }) => {
  const colorConfig = Object.entries(config).filter(([, entry]) => entry.theme ?? entry.color);
  if (!colorConfig.length) return null;

  return (
    <style
      // eslint-disable-next-line no-restricted-syntax -- CSS custom properties built from the chart's own config (colours we pass), never imported text
      dangerouslySetInnerHTML={{
        __html: Object.entries(THEMES)
          .map(([theme, prefix]) => `
${prefix} [data-chart=${id}] {
${colorConfig
  .map(([key, itemConfig]) => {
    const color = itemConfig.theme?.[theme as keyof typeof itemConfig.theme] ?? itemConfig.color;
    return color ? `  --color-${key}: ${color};` : null;
  })
  .join('\n')}
}
`)
          .join('\n'),
      }}
    />
  );
};

const ChartTooltip = RechartsPrimitive.Tooltip;

const ChartLegend = RechartsPrimitive.Legend;

function ChartLegendContent({
  className, hideIcon = false, payload, verticalAlign = 'bottom', nameKey,
}: React.ComponentProps<'div'> & {
  hideIcon?: boolean;
  nameKey?: string;
  payload?: ReadonlyArray<{ value?: unknown; dataKey?: unknown; color?: string; type?: string }>;
  verticalAlign?: 'top' | 'bottom' | 'middle';
}) {
  const { config } = useChart();
  if (!payload?.length) return null;

  return (
    <div className={cn('flex items-center justify-center gap-4', verticalAlign === 'top' ? 'pb-3' : 'pt-3', className)}>
      {payload
        .filter((item) => item.type !== 'none')
        .map((item) => {
          const key = `${nameKey ?? (typeof item.dataKey === 'string' ? item.dataKey : undefined) ?? 'value'}`;
          const itemConfig = getPayloadConfigFromPayload(config, item, key);

          return (
            <div
              key={String(item.value)}
              className="flex items-center gap-1.5 [&>svg]:h-3 [&>svg]:w-3 [&>svg]:text-muted-foreground"
            >
              {itemConfig?.icon && !hideIcon ? (
                <itemConfig.icon />
              ) : (
                <div className="h-2 w-2 shrink-0 rounded-[2px]" style={{ backgroundColor: item.color }} />
              )}
              {itemConfig?.label}
            </div>
          );
        })}
    </div>
  );
}

function getPayloadConfigFromPayload(config: ChartConfig, payload: unknown, key: string) {
  if (typeof payload !== 'object' || payload === null) return undefined;

  const inner = 'payload' in payload && typeof payload.payload === 'object' && payload.payload !== null
    ? payload.payload
    : undefined;

  let configLabelKey: string = key;

  if (key in payload && typeof payload[key as keyof typeof payload] === 'string') {
    configLabelKey = payload[key as keyof typeof payload];
  } else if (inner && key in inner && typeof inner[key as keyof typeof inner] === 'string') {
    configLabelKey = inner[key as keyof typeof inner];
  }

  return configLabelKey in config ? config[configLabelKey] : config[key];
}

export { ChartContainer, ChartLegend, ChartLegendContent, ChartStyle, ChartTooltip };
