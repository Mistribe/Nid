import js from '@eslint/js';
import { defineConfig } from 'eslint/config';
import reactHooks from 'eslint-plugin-react-hooks';
import globals from 'globals';
import tseslint from 'typescript-eslint';
import nid from './eslint-rules/index.js';

/** A `no-restricted-imports` pattern refusing the given src/ folders, by alias or by relative path. */
const layer = (folders, from) => ({
  group: folders.flatMap((f) => [`@/${f}`, `@/${f}/**`, `**/${f}/**`]),
  message: `${from} below ${folders.join(', ')}: move what both need down to components/ or lib/.`,
});

export default defineConfig(
  { ignores: ['dist', 'src/api/schema.d.ts'] },
  js.configs.recommended,
  tseslint.configs.recommendedTypeChecked,
  {
    languageOptions: {
      globals: globals.browser,
      parserOptions: {
        projectService: {
          // e2e/ has its own tsconfig.json: its files would soon pass the default project's limit of 8.
          allowDefaultProject: ['*.js', 'eslint-rules/*.js', 'vite.config.ts', 'playwright.config.ts', 'playwright.local.config.ts'],
          defaultProject: './tsconfig.node.json',
        },
        tsconfigRootDir: import.meta.dirname,
      },
    },
    plugins: { 'react-hooks': reactHooks },
    rules: {
      'react-hooks/rules-of-hooks': 'error',
      'react-hooks/exhaustive-deps': 'error',
      'no-restricted-syntax': ['error', {
        selector: "JSXAttribute[name.name='dangerouslySetInnerHTML']",
        message: 'Imported labels, notes and model output are shown as plain text (frontend/architecture.md §9).',
      }],
    },
  },
  {
    files: ['src/**/*.{ts,tsx}'],
    plugins: { nid },
    rules: { 'nid/no-amount-arithmetic': 'error', 'nid/no-literal-jsx-text': 'error' },
  },
  // Layers (frontend/architecture.md §2): app → features → components → lib / api. A lower layer never imports a higher
  // one: what two features share lives in components/ or lib/, never in one of them. Tests may cross (renderApp mounts it all).
  {
    files: ['src/components/**/*.{ts,tsx}'],
    ignores: ['src/**/*.test.{ts,tsx}'],
    rules: { 'no-restricted-imports': ['error', { patterns: [layer(['features', 'app'], 'components/ sits')] }] },
  },
  {
    files: ['src/lib/**/*.{ts,tsx}', 'src/api/**/*.{ts,tsx}'],
    ignores: ['src/**/*.test.{ts,tsx}'],
    rules: { 'no-restricted-imports': ['error', { patterns: [layer(['components', 'features', 'app'], 'lib/ and api/ sit')] }] },
  },
  {
    files: ['src/**/*.test.{ts,tsx}', 'src/test/**'],
    rules: { 'nid/no-literal-jsx-text': 'off' },
  },
  {
    files: ['**/*.js', '**/*.mjs', 'public/**'],
    extends: [tseslint.configs.disableTypeChecked],
    languageOptions: { globals: { ...globals.node, ...globals.browser } },
  },
);
