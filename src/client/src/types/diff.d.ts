declare module 'diff' {
  export interface Change {
    added?: boolean;
    removed?: boolean;
    value: string;
  }

  export function diffWords(oldStr: string, newStr: string): Change[];
} 