import { useTranslation } from 'react-i18next';

/** The asset classes a target can name, in the server's order (AllocationClasses.All). */
export const CLASSES = ['cash', 'money_market', 'bond', 'equity', 'mixed', 'capital_guaranteed', 'real_estate', 'other'] as const;
const LIQUIDITY = ['available', 'days', 'locked'] as const;

type ClassKey = (typeof CLASSES)[number];
type LiquidityKey = (typeof LIQUIDITY)[number];

/** A class the web does not know yet reads « Autre », never a raw key. */
export function useClassName() {
  const { t } = useTranslation();
  return (key: string) => t(`dashboard.allocation.class.${(CLASSES as readonly string[]).includes(key) ? (key as ClassKey) : 'other'}`);
}

export function useLiquidityName() {
  const { t } = useTranslation();
  return (key: string) => ((LIQUIDITY as readonly string[]).includes(key) ? t(`dashboard.allocation.liquidity.${key as LiquidityKey}`) : key);
}
