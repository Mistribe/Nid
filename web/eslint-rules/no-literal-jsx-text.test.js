import { RuleTester } from 'eslint';
import tseslint from 'typescript-eslint';
import { describe, it } from 'vitest';
import rule from './no-literal-jsx-text.js';

RuleTester.describe = describe;
RuleTester.it = it;
RuleTester.itOnly = it.only;

const tester = new RuleTester({
  languageOptions: { parser: tseslint.parser, ecmaVersion: 'latest', sourceType: 'module', parserOptions: { ecmaFeatures: { jsx: true } } },
});

tester.run('no-literal-jsx-text', rule, {
  valid: [
    'const a = <p>{t("app.name")}</p>;',
    'const a = <div className="flex gap-2" />;',
    'const a = <span> · </span>;',
    'const a = <input aria-label={t("scope.label")} />;',
    'const a = <p>{count}</p>;',
  ],
  invalid: [
    { code: 'const a = <p>Bonjour</p>;', errors: [{ messageId: 'literal' }] },
    { code: 'const a = <p>{"Bonjour"}</p>;', errors: [{ messageId: 'literal' }] },
    { code: 'const a = <input placeholder="Nom" />;', errors: [{ messageId: 'literal' }] },
    { code: 'const a = <button aria-label="Fermer" />;', errors: [{ messageId: 'literal' }] },
    { code: 'const a = <img alt="Logo" />;', errors: [{ messageId: 'literal' }] },
  ],
});
