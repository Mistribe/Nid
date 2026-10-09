export const percent = (value: number, sign = false) =>
  new Intl.NumberFormat('fr-FR', { maximumFractionDigits: 1, signDisplay: sign ? 'exceptZero' : 'auto' }).format(value);
