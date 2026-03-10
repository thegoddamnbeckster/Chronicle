module.exports = {
  root: true,
  env: { browser: true, es2020: true },
  extends: [
    'eslint:recommended',
    'plugin:@typescript-eslint/recommended',
    'plugin:react-hooks/recommended',
  ],
  ignorePatterns: ['dist', '.eslintrc.cjs'],
  parser: '@typescript-eslint/parser',
  plugins: ['react-refresh'],
  rules: {
    // Context files legitimately export both components and hooks together in
    // this project — suppress the "only export components" HMR hint.
    'react-refresh/only-export-components': 'off',

    // useEffect dependency arrays are intentionally incomplete in several pages
    // (load-once-on-mount patterns). Disable rather than leave stray warnings.
    'react-hooks/exhaustive-deps': 'off',

    // Allow underscore-prefixed variables to be intentionally unused.
    '@typescript-eslint/no-unused-vars': [
      'warn',
      { argsIgnorePattern: '^_', varsIgnorePattern: '^_' },
    ],
  },
}
