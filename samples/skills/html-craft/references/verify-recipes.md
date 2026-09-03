# Verify recipes - in-page assertions

Copy-paste expressions for `html_craft.py eval PAGE "<expression>"`. All
expressions must return JSON-serializable values.

## Screen-pixel geometry of an SVG element

Source units lie: CSS sizes the SVG, and `fitText`/scale transforms change
what the user sees. Project SVG-local bounds into screen pixels:

```js
(() => {
  const el = document.getElementById("page");   // your element
  const b = el.getBBox();
  const m = el.getCTM();                        // full local->screen matrix
  return {
    top:    (m.d * b.y + m.f).toFixed(0),
    bottom: (m.d * (b.y + b.height) + m.f).toFixed(0),
    left:   (m.a * b.x + m.e).toFixed(0),
    right:  (m.a * (b.x + b.width) + m.e).toFixed(0),
  };
})()
```

For plain HTML elements use `getBoundingClientRect()` instead.

## Overlap / bounds check against a known landmark

```js
(() => {
  const el = document.getElementById("page");
  const m = el.getCTM();
  const b = el.getBBox();
  const rimY = 134;                                // landmark in SVG units
  const bot = m.d * (b.y + b.height) + m.f;
  return { overlapsRim: bot > rimY, bottom: bot.toFixed(0) };
})()
```

## Font-size audit (catch invisible shrink-to-fit)

A `fitText`-style scaler shrinks text to fit a width budget; source font
sizes are then meaningless. Audit the *final* rendered sizes:

```js
(() => {
  const counts = {};
  document.querySelectorAll("#sheet text").forEach(t => {
    const fs = parseFloat(t.getAttribute("font-size"));
    counts[fs] = (counts[fs] || 0) + 1;
  });
  return counts;   // e.g. {16:4, 23:2, 13:2} - any surprise = a shrink happened
})()
```

If a size is smaller than intended, the wrap/width budget is too tight:
raise `MAX_CHARS`-style limits or widen the element, then re-audit.

## State sweep (N turns/steps)

Drive each state with its own `shot` call, and after each, capture a compact
record. Wait budgets come from the page's own phase constants:

```bash
# phases show:3.0 fold:3.2 crank:4.6 output:4.5 -> intake ~1.6s after press
for i in 1 2 3 4 5; do
  python3 Skills/html-craft/scripts/html_craft.py shot page.html -o t${i}.png \
    --action "press:Space" --action "wait:1600"
done
```

(Each call is a fresh load; for stateful pages, pass the accumulated action
list, e.g. `--action press:Space --action wait:15600 --action press:Space`.)

## Visible copy check

```bash
python3 Skills/html-craft/scripts/html_craft.py text page.html \
  --sel "#capKick" --sel "#capText" \
  --action "press:Space" --action "wait:12500"
```

## Before/after evidence

```bash
python3 Skills/html-craft/scripts/html_craft.py compare before.png after.png -o cmp.png
```

## Timing discipline

- Read the page's `TIMING` object (or equivalent) before writing waits.
- Capture *inside* the phase you want: phase start = sum of prior durations.
- If a capture lands in the wrong phase, you will see it in the kicker/label
  text - print it with the same call (`--sel "#capKick"` alongside the shot
  in a second command, or `eval` the label first).
