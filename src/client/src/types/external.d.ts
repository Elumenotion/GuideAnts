declare module 'react-markdown';
declare module 'remark-gfm';
declare module 'mermaid'; 
declare module '*.md?raw' {
  const content: string;
  export default content;
}