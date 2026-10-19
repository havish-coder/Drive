// WCAG contrast check for the UI's text/background token pairs (light + dark). Fails below 4.5:1.
const fs = require('fs');
const css = fs.readFileSync(__dirname + '/../src/CloudSync.Api/wwwroot/index.html', 'utf8');
const block = re => Object.fromEntries([...css.match(re)[1].matchAll(/--([\w-]+):\s*(#[0-9A-Fa-f]{6})/g)].map(m => [m[1], m[2]]));
const light = block(/:root \{([^}]*)\}/), dark = { ...light, ...block(/prefers-color-scheme: dark\) \{\s*:root \{([^}]*)\}/) };
const lum = h => { const c = [1, 3, 5].map(i => parseInt(h.slice(i, i + 2), 16) / 255).map(v => v <= .03928 ? v / 12.92 : ((v + .055) / 1.055) ** 2.4);
  return .2126 * c[0] + .7152 * c[1] + .0722 * c[2]; };
const ratio = (a, b) => { const [x, y] = [lum(a), lum(b)].sort((p, q) => q - p); return (x + .05) / (y + .05); };
const pairs = [['fg', 'bg'], ['fg', 'surface'], ['fg', 'subtle'], ['muted', 'surface'], ['muted', 'bg'], ['muted', 'subtle'], ['primary', 'surface'],
  ['primary', 'primary-soft'], ['on-primary', 'primary'], ['destructive', 'surface'], ['on-destructive', 'destructive'], ['success', 'surface'], ['bg', 'fg']];
let fail = 0;
for (const [mode, t] of [['light', light], ['dark', dark]])
  for (const [f, b] of pairs) { const r = ratio(t[f], t[b]), ok = r >= 4.5; fail += !ok; console.log(`${ok ? 'ok  ' : 'FAIL'} ${mode.padEnd(5)} ${f} on ${b}: ${r.toFixed(2)}`); }
process.exit(fail ? 1 : 0);
