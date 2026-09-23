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
    'const a = <p>{t(cond ? "a.b" : "c.d")}</p>;',
    'const a = <div className={cond ? "flex" : "grid"} />;',
    'const a = <p>{cond ? t("a.b") : null}</p>;',
    'const a = <p>{`${count}`}</p>;',
    'const a = <EmptyState title={t("a.b")} body={t("c.d")} />;',
    'const a = <label htmlFor="x">{t("a.b")}</label>;',
  ],
  invalid: [
    { code: 'const a = <p>Bonjour</p>;', errors: [{ messageId: 'literal' }] },
    { code: 'const a = <p>{"Bonjour"}</p>;', errors: [{ messageId: 'literal' }] },
    { code: 'const a = <input placeholder="Nom" />;', errors: [{ messageId: 'literal' }] },
    { code: 'const a = <button aria-label="Fermer" />;', errors: [{ messageId: 'literal' }] },
    { code: 'const a = <img alt="Logo" />;', errors: [{ messageId: 'literal' }] },
    { code: 'const a = <p>{isActive ? "Actif" : "Inactif"}</p>;', errors: [{ messageId: 'literal' }] },
    { code: 'const a = <p>{cond && "Texte"}</p>;', errors: [{ messageId: 'literal' }] },
    { code: 'const a = <p>{`Bonjour ${name}`}</p>;', errors: [{ messageId: 'literal' }] },
    { code: 'const a = <input aria-label={"Fermer"} />;', errors: [{ messageId: 'literal' }] },
    { code: 'const a = <img alt={cond ? "A" : "B"} />;', errors: [{ messageId: 'literal' }] },
    { code: 'const a = <EmptyState body="Rien pour l\u2019instant" />;', errors: [{ messageId: 'literal' }] },
    { code: 'const a = <option label="Mensuel" />;', errors: [{ messageId: 'literal' }] },
    { code: 'const a = <Field description={"Aide"} />;', errors: [{ messageId: 'literal' }] },
  ],
});
