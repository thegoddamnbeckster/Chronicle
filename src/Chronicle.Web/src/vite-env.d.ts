/// <reference types="vite/client" />

// CSS Modules — tell TypeScript every imported .module.css is a valid object
// whose keys are class name strings. Vite handles the actual transformation.
declare module '*.module.css' {
  const classes: { readonly [key: string]: string };
  export default classes;
}
