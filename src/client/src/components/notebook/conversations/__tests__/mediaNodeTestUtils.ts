import { createEditor, LexicalEditor } from 'lexical';

export function runWithEditor<T>(
  nodes: Array<import('lexical').Klass<import('lexical').LexicalNode>>,
  fn: (editor: LexicalEditor) => T
): T {
  const editor = createEditor({ nodes, onError: () => {} });
  let result!: T;
  editor.update(() => {
    result = fn(editor);
  });
  return result;
}
