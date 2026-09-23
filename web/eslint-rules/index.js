import noAmountArithmetic from './no-amount-arithmetic.js';
import noLiteralJsxText from './no-literal-jsx-text.js';

export default {
  meta: { name: 'eslint-plugin-nid' },
  rules: { 'no-amount-arithmetic': noAmountArithmetic, 'no-literal-jsx-text': noLiteralJsxText },
};
