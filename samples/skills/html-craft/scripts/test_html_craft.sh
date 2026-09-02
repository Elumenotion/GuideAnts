#!/usr/bin/env bash
# End-to-end test for html-craft: preflight + probe + text + shot + compare
# against the packaged fixture page. Exit 0 only if every assertion passes.
set -u
cd "$(dirname "$0")"
pass=0; fail=0
ok() { echo "PASS  $1"; pass=$((pass+1)); }
no() { echo "FAIL  $1"; fail=$((fail+1)); }

# 1) preflight is open
pre=$(python3 preflight.py 2>/dev/null)
if python3 -c "import json,sys; sys.exit(0 if json.loads(sys.argv[1])['open'] else 1)" "$pre"; then
  ok "preflight open"
else
  no "preflight: $pre"; echo "$pre"; exit 1
fi

# 2) probe: no pageerrors
probe=$(python3 html_craft.py probe fixture.html 2>/dev/null)
if python3 -c "import json,sys; d=json.loads(sys.argv[1]); sys.exit(0 if not d['pageerrors'] else 1)" "$probe"; then
  ok "probe clean"
else
  no "probe: $probe"
fi

# 3) text idle
t0=$(python3 html_craft.py text fixture.html --sel "#kick" 2>/dev/null)
if python3 -c "import json,sys; sys.exit(0 if json.loads(sys.argv[1])['texts']['#kick']=='READY' else 1)" "$t0"; then
  ok "text idle READY"
else
  no "text idle: $t0"
fi

# 4) text after keypress
t1=$(python3 html_craft.py text fixture.html --sel "#kick" --action "press:Space" 2>/dev/null)
if python3 -c "import json,sys; sys.exit(0 if json.loads(sys.argv[1])['texts']['#kick']=='TURN 1' else 1)" "$t1"; then
  ok "text after press TURN 1"
else
  no "text after press: $t1"
fi

# 5) eval returns a value
ev=$(python3 html_craft.py eval fixture.html "document.getElementById('big').textContent" --action "press:Space" --action "wait:200" 2>/dev/null)
if python3 -c "import json,sys; sys.exit(0 if json.loads(sys.argv[1])['value']=='n=1' else 1)" "$ev"; then
  ok "eval n=1"
else
  no "eval: $ev"
fi

# 6) shot is a real PNG
shot=$(python3 html_craft.py shot fixture.html -o /tmp/html_craft_test.png --action "press:Space" --action "wait:300" 2>/dev/null)
if python3 -c "
import json,sys
d=json.loads(sys.argv[1])
b=open(d['shot'],'rb').read(8)
sys.exit(0 if b==b'\x89PNG\r\n\x1a\n' else 1)" "$shot"; then
  ok "shot is PNG"
else
  no "shot: $shot"
fi

# 7) compare builds a canvas
cmp=$(python3 html_craft.py compare /tmp/html_craft_test.png /tmp/html_craft_test.png -o /tmp/html_craft_cmp.png 2>/dev/null)
if python3 -c "import json,sys; sys.exit(0 if json.loads(sys.argv[1])['size'][0] > 100 else 1)" "$cmp"; then
  ok "compare"
else
  no "compare: $cmp"
fi

echo "----"
echo "passed=$pass failed=$fail"
[ "$fail" -eq 0 ]
