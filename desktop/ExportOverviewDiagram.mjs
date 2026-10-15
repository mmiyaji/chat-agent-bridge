import { chromium } from 'playwright';
import { readFile, mkdir } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// Build-time export only. The desktop application has no browser dependency.
const desktop = path.dirname(fileURLToPath(import.meta.url));
const sourcePath = path.join(desktop, '..', 'docs', 'assets', 'overview', 'OverviewArchitecture.svg');
const outputPath = path.join(desktop, 'ChatAgentBridge.Desktop', 'Assets', 'OverviewArchitecture.png');
const source = await readFile(sourcePath, 'utf8');
const browser = await chromium.launch({ headless: true, channel: 'msedge' });
try {
  const page = await browser.newPage({ viewport: { width: 1200, height: 1000 }, deviceScaleFactor: 2 });
  await page.route('**/*', route => route.abort());
  await page.setContent(`<style>html,body{margin:0;padding:0;background:white}svg{display:block}</style>${source}`);
  await page.evaluate(() => document.fonts.ready);
  await mkdir(path.dirname(outputPath), { recursive: true });
  await page.locator('svg').screenshot({ path: outputPath, animations: 'disabled' });
  console.log(`Exported overview diagram: ${outputPath}`);
} finally {
  await browser.close();
}
