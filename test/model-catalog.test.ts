import test from 'node:test';
import assert from 'node:assert/strict';
import { parseCursorModels } from '../src/admin/model-catalog.js';

test('Cursor model parsing excludes headers and strips ANSI text', () => {
  assert.deepEqual(parseCursorModels('Available models:\n\u001b[32mauto - Auto\u001b[0m\nsonnet  Sonnet\nauto - Auto\n'), [
    { id: 'auto', label: 'Auto' }, { id: 'sonnet', label: 'Sonnet' },
  ]);
  assert.deepEqual(parseCursorModels('No models available for this account.'), []);
});
