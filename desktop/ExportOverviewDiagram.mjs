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
  const layout = await page.evaluate(() => {
    const svg = document.querySelector('svg');
    const canvas = svg.viewBox.baseVal;
    const labels = [...svg.querySelectorAll('text')].map(element => {
      const box = element.getBBox();
      return { text: element.textContent, x: box.x, y: box.y, width: box.width, height: box.height };
    });
    const errors = [];
    for (let i = 0; i < labels.length; i++) {
      const a = labels[i];
      if (a.x < canvas.x || a.y < canvas.y || a.x + a.width > canvas.x + canvas.width || a.y + a.height > canvas.y + canvas.height) {
        errors.push(`Text outside diagram: ${a.text}`);
      }
      for (let j = i + 1; j < labels.length; j++) {
        const b = labels[j];
        if (Math.min(a.x + a.width, b.x + b.width) > Math.max(a.x, b.x) &&
            Math.min(a.y + a.height, b.y + b.height) > Math.max(a.y, b.y)) {
          errors.push(`Overlapping text: ${a.text} / ${b.text}`);
        }
      }
    }
    return { labelCount: labels.length, errors };
  });
  if (layout.errors.length) throw new Error(layout.errors.join('\n'));
  await mkdir(path.dirname(outputPath), { recursive: true });
  await page.locator('svg').screenshot({ path: outputPath, animations: 'disabled' });
  console.log(`Verified ${layout.labelCount} labels: no text overlaps or canvas overflow.`);
  console.log(`Exported overview diagram: ${outputPath}`);
} finally {
  await browser.close();
}
