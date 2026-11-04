// Validates the static intro site in site/ before it is deployed to Cloudflare Pages.
import fs from "node:fs";
import path from "node:path";

const root = path.resolve("site");
const requiredFiles = [
  "index.html",
  "assets/styles.css",
  "assets/theme.js",
  "assets/site.js",
  "assets/glightbox.min.css",
  "assets/glightbox.min.js",
  "assets/i18n-en.js",
  "assets/img/icon-256.png",
  "_headers",
  "_redirects",
  "robots.txt",
  "sitemap.xml",
];
const errors = [];

for (const file of requiredFiles) {
  if (!fs.existsSync(path.join(root, file))) errors.push(`missing ${file}`);
}

const html = fs.readFileSync(path.join(root, "index.html"), "utf8");
// The CSP allows only same-origin scripts, so inline scripts and handlers would silently fail.
if (/<script(?![^>]*\bsrc=)[^>]*>/i.test(html)) errors.push("index.html: inline <script> is blocked by the CSP");
if (/\son[a-z]+\s*=/i.test(html)) errors.push("index.html: inline event handlers are blocked by the CSP");

// Paths served by _redirects, such as /privacy, have no file of their own.
const redirected = new Set(fs.readFileSync(path.join(root, "_redirects"), "utf8")
  .split(/\r?\n/).map((line) => line.trim().split(/\s+/)[0]).filter((from) => from && !from.startsWith("#")));

for (const match of html.matchAll(/\s(?:src|href|data-en-src|data-en-href)="([^"]+)"/g)) {
  const reference = match[1];
  // ?lang=en and similar links stay on this page.
  if (/^(https?:|mailto:|#|data:|\?)/.test(reference)) continue;
  let pathname = decodeURIComponent(reference.split(/[?#]/)[0]);
  if (pathname === "" || pathname.endsWith("/")) pathname += "index.html";
  if (redirected.has(pathname)) continue;
  const target = path.resolve(root, pathname.replace(/^\/+/, ""));
  if (!target.startsWith(root + path.sep)) errors.push(`index.html: reference escapes site/: ${reference}`);
  else if (!fs.existsSync(target)) errors.push(`index.html: missing local file ${reference}`);
}

// Scripts and stylesheets must be served from this site.
for (const match of html.matchAll(/<(script|link)\b[^>]*\s(?:src|href)="(https?:[^"]+)"/gi)) {
  if (match[1].toLowerCase() === "script" || /rel="stylesheet"/.test(match[0]))
    errors.push(`index.html: external ${match[1]} is blocked by the CSP: ${match[2]}`);
}

for (const anchor of html.matchAll(/<img\b[^>]*>/gi)) {
  if (!/\salt="/.test(anchor[0])) errors.push(`index.html: image without alt text: ${anchor[0]}`);
}

if (errors.length > 0) {
  console.error(errors.join("\n"));
  process.exit(1);
}
console.log(`site/ is valid (${requiredFiles.length} required files).`);
