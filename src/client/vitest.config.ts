/// <reference types="vitest" />
// eslint-disable-next-line import/no-extraneous-dependencies
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { resolve } from 'path';

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@': resolve(__dirname, 'src'),
    },
  },
  // @ts-ignore - Vitest injects `test` property
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    environmentOptions: {
      jsdom: {
        resources: 'usable',
      },
    },
    testTimeout: 5000,
    hookTimeout: 5000,
    maxThreads: 1,
    minThreads: 1,
    isolate: true,
    coverage: {
      provider: 'v8',
      reporter: ['text', 'json', 'html'],
      exclude: [
        'node_modules/',
        'src/test/',
        '**/*.d.ts',
        '**/*.config.*',
        '**/electron/**',
        '**/*.css',
        '**/*.json',
        '**/index.ts',
        'src/pages/settings/components/catalog/providers/AnthropicForm.tsx',
        'src/pages/settings/components/catalog/providers/AzureOpenAiChatForm.tsx',
        'src/pages/settings/components/catalog/providers/AzureOpenAiResponsesForm.tsx',
        'src/pages/settings/components/catalog/providers/OpenAiChatForm.tsx',
        'src/pages/settings/components/catalog/providers/OpenAiResponsesForm.tsx',
        'src/pages/settings/components/catalog/providers/GoogleGeminiForm.tsx',
        'src/pages/settings/components/catalog/providers/HuggingFaceInferenceForm.tsx',
        'src/pages/settings/components/catalog/providers/OpenRouterForm.tsx',
      ],
      all: true,
      lines: 85,
      functions: 83,
      branches: 80,
      statements: 85,
    },
  },
}); 