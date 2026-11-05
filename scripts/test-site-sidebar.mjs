// Focused regression checks for the static site's scroll selection controller.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

const listeners = {}, timers = new Map();
let timerId = 0, frame;
const positions = { top: 0, how: 800, features: 1600, faq: 2400 };
const links = Object.keys(positions).map(id => ({
  id, events: {}, attrs: {}, selected: false,
  getAttribute: name => name === 'href' ? '#' + id : null,
  classList: { toggle(name, value) { links.find(link => link.id === id).selected = value; } },
  setAttribute(name, value) { this.attrs[name] = value; },
  removeAttribute(name) { delete this.attrs[name]; },
  addEventListener(name, fn) { this.events[name] = fn; }
}));
const root = { classList: { remove() {} }, scrollHeight: 3000 };
const context = {
  URLSearchParams, location: { search: '', hash: '' },
  document: { documentElement: root,
    getElementById: id => id in positions ? { id, getBoundingClientRect: () => ({ top: positions[id] - context.scrollY }) } : null,
    querySelectorAll: () => links },
  innerHeight: 600, scrollY: 0,
  addEventListener: (name, fn) => { listeners[name] = fn; },
  setTimeout: fn => { timers.set(++timerId, fn); return timerId; },
  clearTimeout: id => timers.delete(id),
  requestAnimationFrame: fn => { frame = fn; }
};
context.window = context;
vm.runInNewContext(fs.readFileSync('site/assets/site.js', 'utf8'), context);
const selected = () => links.filter(link => link.selected).map(link => link.id);
const flush = () => { const fn = frame; frame = null; fn?.(); };
const click = (id, overrides = {}) => links.find(link => link.id === id).events.click({ button: 0, ...overrides });
assert.deepEqual(selected(), ['top']);
click('features');
assert.deepEqual(selected(), ['features']);
context.scrollY = 800; listeners.scroll(); flush();
assert.deepEqual(selected(), ['features'], 'smooth scroll keeps the clicked destination selected');
context.scrollY = 1600; listeners.scrollend();
assert.deepEqual(selected(), ['features']);
context.scrollY = 800; listeners.scroll(); flush();
assert.deepEqual(selected(), ['how'], 'ordinary scrolling follows the visible section');
click('faq', { ctrlKey: true });
assert.deepEqual(selected(), ['how'], 'opening a new tab must not change this tab selection');
click('faq'); context.scrollY = 2400; listeners.scrollend();
assert.deepEqual(selected(), ['faq'], 'short final section is selected at page bottom');
click('top'); context.scrollY = 800; listeners.wheel(); listeners.scroll(); flush();
assert.deepEqual(selected(), ['how'], 'manual scrolling interrupts a click hold');
context.scrollY = 1600; listeners.resize(); flush();
assert.deepEqual(selected(), ['features']);
click('top');
for (const fn of [...timers.values()]) fn();
assert.deepEqual(selected(), ['features'], 'fallback reconciles selection when no scroll occurs');
assert.equal(links.filter(link => link.attrs['aria-current'] === 'location').length, 1);
console.log('Sidebar selection regression checks passed.');
