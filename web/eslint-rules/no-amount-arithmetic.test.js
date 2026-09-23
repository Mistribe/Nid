import { RuleTester } from 'eslint';
import tseslint from 'typescript-eslint';
import { describe, it } from 'vitest';
import rule from './no-amount-arithmetic.js';

RuleTester.describe = describe;
RuleTester.it = it;
RuleTester.itOnly = it.only;

const tester = new RuleTester({ languageOptions: { parser: tseslint.parser, ecmaVersion: 'latest', sourceType: 'module' } });

tester.run('no-amount-arithmetic', rule, {
  valid: [
    'const label = `${price.amount} ${price.currency}`;',
    'const count = items.length + 1;',
    'const same = a.amount === b.amount;',
    'format(money.amount);',
    'const total = list.householdCashCost;',
  ],
  invalid: [
    { code: 'const sum = a.amount + b.amount;', errors: [{ messageId: 'arithmetic' }] },
    { code: 'const twice = price.amount * 2;', errors: [{ messageId: 'arithmetic' }] },
    { code: 'const d = detail.nextChargeAmount - 1;', errors: [{ messageId: 'arithmetic' }] },
    { code: 'total += row.amount;', errors: [{ messageId: 'arithmetic' }] },
    { code: 'const n = -price.amount;', errors: [{ messageId: 'arithmetic' }] },
    { code: 'amount++;', errors: [{ messageId: 'arithmetic' }] },
    { code: 'const x = Number(price.amount);', errors: [{ messageId: 'coercion' }] },
    { code: 'const x = parseFloat(allocatedAmount);', errors: [{ messageId: 'coercion' }] },
    { code: 'const x = Math.round(price?.amount);', errors: [{ messageId: 'coercion' }] },
    { code: 'const x = Number.parseFloat(p.amount!);', errors: [{ messageId: 'coercion' }] },
  ],
});
