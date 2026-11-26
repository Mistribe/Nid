import type { TFunction } from 'i18next';
import { ASSET_ENERGIES, ASSET_KINDS } from '@/api/assets';
import '@/locales/assets';

const isKind = (kind: string): kind is (typeof ASSET_KINDS)[number] => (ASSET_KINDS as readonly string[]).includes(kind);

/** The kind in words; a kind this version does not know reads as « Autre ». */
export const kindText = (t: TFunction, kind: string) => t(`assets_kind.${isKind(kind) ? kind : 'other'}`);

/** The energy in words; one this version does not know reads as « Autre ». */
export const energyText = (t: TFunction, energy: string) =>
  t(`assets_energy.${(ASSET_ENERGIES as readonly string[]).includes(energy) ? energy : 'other'}` as 'assets_energy.other');
