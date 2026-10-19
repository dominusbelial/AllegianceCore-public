declare module '*.cjs' {
  export function validateResult(value: unknown): boolean;
  export function validateKept(value: unknown): boolean;
}
