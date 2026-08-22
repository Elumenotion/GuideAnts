#!/usr/bin/env python3
"""
md_transcript_to_pdf.py

Convert a "Code Executor" markdown conversation transcript into a well-formatted PDF.

Designed for transcripts produced by this system, whose structure is:

    # Title
    **Created:** ...          <- optional "Key: value" metadata lines
    **Last Activity:** ...
    **Assistant:** ...
    ---
    **User**
    <message>
    ---
    **Assistant** (Code Executor)
    <message / reasoning>
    **Tool Call:** `run_bash`
    ```json
    { "script": "cat > file <<'EOF' ... EOF" }
    ```
    ---
    **Tool** (run_bash)
    {"StandardOutput":"...","ExitCode":0,...}
    ---
    ...

It renders:
  * a cover page (title, stats, metadata, user-request list, inferred deliverable)
  * every message as a role-badged card (User / Assistant / Tool)
  * tool-call INPUTS: `**Tool Call:**` blocks -> their JSON `script` is unescaped and
    rendered with heredoc awareness (bash header + the written-file body highlighted
    with the right lexer), or as highlighted JSON for non-shell tools
  * tool OUTPUTS: the `**Tool**` result JSON in an amber panel
  * markdown: headings, bold/italic, inline code, bullet/numbered lists, pipe tables
  * the 3 known "truncated code block" quirks are auto-closed so fences balance

Usage:
    python3 md_transcript_to_pdf.py INPUT.md [-o OUTPUT.pdf] [--no-cover] [--quiet]

Dependencies (already installed in this environment): weasyprint, pygments, pypdf
"""
import argparse
import bisect
import html as _html
import json
import os
import re
import sys
from collections import Counter

from pygments import highlight
from pygments.formatters import HtmlFormatter
from pygments.lexers import (BashLexer, HtmlLexer, JavascriptLexer, PythonLexer,
                             TextLexer)

# --------------------------------------------------------------------------- #
# Regexes describing the transcript "system"
# --------------------------------------------------------------------------- #
ROLE_RE      = re.compile(r'^\*\*(User|Assistant)\*\*(?:\s*\([^)]*\))?\s*$')
TOOL_OUT_RE  = re.compile(r'^\*\*Tool\*\*\s*\(([^)]*)\)\s*$')
TOOL_CALL_RE = re.compile(r'^\*\*Tool Call:\*\*\s*`([^`]+)`\s*$')
SEPARATOR_RE = re.compile(r'^-{3,}$')
META_RE      = re.compile(r'^\*\*(.+?):\*\*\s*(.*)$')

EXT_LANG = {
    '.js': 'javascript', '.mjs': 'javascript', '.jsx': 'javascript',
    '.ts': 'javascript', '.py': 'python',
    '.html': 'html', '.htm': 'html',
    '.sh': 'bash', '.bash': 'bash',
    '.json': 'json', '.css': 'text',
}

LEX = {
    'javascript': JavascriptLexer, 'python': PythonLexer,
    'html': HtmlLexer, 'bash': BashLexer, 'text': TextLexer,
}


# --------------------------------------------------------------------------- #
# Parsing
# --------------------------------------------------------------------------- #
def load_lines(path):
    with open(path, encoding='utf-8-sig') as f:
        return f.read().splitlines()


def fix_truncated_fences(lines):
    """Some turns end mid-code-fence (log was cut). Close those so the fence
    count balances. A region between two User/Assistant markers with an odd
    number of fence lines gets a closing ``` inserted before its last ---.
    Returns (new_lines, n_fixes)."""
    ts = [i for i, l in enumerate(lines) if ROLE_RE.match(l.strip())]
    fixes = []
    for k, s in enumerate(ts):
        e = ts[k + 1] if k + 1 < len(ts) else len(lines)
        nf = sum(1 for i in range(s, e) if lines[i].strip().startswith('```'))
        if nf % 2 == 1:
            seps = [i for i in range(s, e) if SEPARATOR_RE.match(lines[i].strip())]
            if seps:
                fixes.append(seps[-1])
    for idx in sorted(fixes, reverse=True):
        lines.insert(idx, '```')
    return lines, len(fixes)


def fence_lines(lines):
    return [i for i, l in enumerate(lines)
            if l.strip().startswith('```') and len(l.strip()) >= 3]


def find_tool_call_ranges(lines):
    """Anchor every ``**Tool Call:**`` marker to its input block: the next two
    fence lines after the marker, validated that the body starts with '{'.
    Anchoring (rather than fence parity) keeps this correct even when the
    surrounding prose quotes stray fence snippets. Returns [(marker, o, c)]."""
    fence = fence_lines(lines)
    ranges = []
    for m, l in enumerate(lines):
        if not TOOL_CALL_RE.match(l.strip()):
            continue
        pos = bisect.bisect_right(fence, m)
        if pos >= len(fence):
            continue
        o = fence[pos]
        pos2 = bisect.bisect_right(fence, o)
        if pos2 >= len(fence):
            continue
        c = fence[pos2]
        if '\n'.join(lines[o + 1:c]).strip().startswith('{'):
            ranges.append((m, o, c))
    return ranges


def build_elements(lines, bounds):
    """Split the document into prose/code elements.

    Two independent tracks so they can't corrupt each other:
      1. tool-call input blocks are anchored on their ``**Tool Call:**``
         marker (the next two fence lines, body must look like JSON);
      2. everything else uses the standard markdown fence rule: a fence opens
         a block only if preceded by a blank line; the next fence closes it.
         Fences quoted inline/indented inside prose stay prose. Tool-call
         fence lines are consumed by track 1 and never seen by track 2.
    """
    n = len(lines)
    tc_start = {}
    for m, o, c in find_tool_call_ranges(lines):
        tc_start[o] = c

    bset = set(bi for _, bi in bounds)
    elements, cur, cur_start = [], [], 0
    infence = False
    open_i = lang = None

    def flush():
        nonlocal cur
        if cur:
            elements.append({'type': 'prose', 'lines': cur,
                             'start': cur_start,
                             'end': cur_start + len(cur) - 1})
        cur = []

    i = 0
    while i < n:
        if i in bset:
            flush()
            cur_start = i
        if i in tc_start:  # track 1: tool-call input
            flush()
            c = tc_start[i]
            elements.append({'type': 'code', 'body': lines[i + 1:c],
                             'lang': lines[i].strip()[3:].strip(),
                             'start': i, 'end': c, 'is_toolcall': True})
            i = c + 1
            continue
        s = lines[i].strip()
        is_fence = s.startswith('```') and len(s) >= 3
        if infence:
            if is_fence:  # closing fence
                elements.append({'type': 'code',
                                 'body': lines[open_i + 1:i], 'lang': lang,
                                 'start': open_i, 'end': i})
                infence = False
            i += 1
            continue
        if is_fence and (i == 0 or not lines[i - 1].strip()):
            flush()
            infence = True
            open_i = i
            lang = s[3:].strip()
            i += 1
            continue
        if not cur:
            cur_start = i
        cur.append(lines[i])
        i += 1
    if infence and open_i is not None:  # unclosed block at EOF
        elements.append({'type': 'code', 'body': lines[open_i + 1:n],
                         'lang': lang, 'start': open_i, 'end': n - 1})
    flush()
    return elements


def find_boundaries(lines):
    """Return [(kind, line_index)] where a new 'turn' starts.
    kind is 'role' (User/Assistant) or 'toolout' (**Tool**)."""
    bounds = []
    for i, l in enumerate(lines):
        s = l.strip()
        if ROLE_RE.match(s):
            bounds.append(('role', i))
        elif TOOL_OUT_RE.match(s):
            bounds.append(('toolout', i))
    return bounds


def split_heredoc(script):
    """Split a shell script containing a heredoc into header/command/body/footer.
    Returns dict with kind='heredoc' or 'plain'."""
    ls = script.split('\n')
    start = marker = None
    for i, l in enumerate(ls):
        m = re.search(r'<<-?\s*[\'"]?([A-Za-z_]\w*)[\'"]?', l)
        if m and any(x.strip() == m.group(1) for x in ls[i + 1:]):
            start, marker = i, m.group(1)
            break
    if start is None:
        return {'kind': 'plain'}
    cmd = ls[start]
    end = None
    for i in range(start + 1, len(ls)):
        if ls[i].strip() == marker:
            end = i
            break
    if end is None:
        return {'kind': 'plain'}
    header = [l for l in ls[:start] if l.strip()]
    code = ls[start + 1:end]
    footer = [l for l in ls[end + 1:] if l.strip()]
    target = lang = None
    mt = re.search(r'cat\s*>\s*(\S+)', cmd)
    if mt:
        target = mt.group(1)
        me = re.search(r'\.([a-z0-9]+)$', target, re.I)
        if me:
            lang = EXT_LANG.get('.' + me.group(1).lower())
    if re.search(r'python3?\b', cmd):
        lang = 'python'
    elif re.search(r'\bnode\b', cmd):
        lang = 'javascript'
    return {'kind': 'heredoc', 'header': header, 'cmd': cmd, 'code': code,
            'footer': footer, 'target': target, 'lang': lang, 'marker': marker}


def tag_tool_calls(elements):
    """Fill in tool_name/script/hd for tool-call input elements. Track 1 of
    build_elements already sets is_toolcall; this also picks up any json code
    element whose preceding prose ends in a **Tool Call:** marker (fallback)."""
    for ei, e in enumerate(elements):
        if e.get('is_toolcall'):
            e.setdefault('tool_name', None)
            e.setdefault('input_obj', None)
            e.setdefault('script', None)
            e.setdefault('hd', None)
            if e.get('tool_name') is None:
                # recover name from preceding prose marker line
                for pj in range(ei - 1, -1, -1):
                    pe = elements[pj]
                    if pe['type'] != 'prose':
                        break
                    lb = None
                    for pl in reversed(pe['lines']):
                        if pl.strip():
                            lb = pl.strip()
                            break
                    m = TOOL_CALL_RE.match(lb) if lb else None
                    if m:
                        e['tool_name'] = m.group(1)
                        break
            try:
                obj = json.loads('\n'.join(e['body']))
                e['input_obj'] = obj
                if isinstance(obj, dict) and 'script' in obj:
                    e['script'] = obj['script']
                    e['hd'] = split_heredoc(e['script'])
            except Exception:
                pass
            continue
        e['is_toolcall'] = False
        if e['type'] != 'code' or (e.get('lang') or '').lower() != 'json':
            continue
        for pj in range(ei - 1, -1, -1):
            pe = elements[pj]
            if pe['type'] != 'prose':
                break
            lb = None
            for pl in reversed(pe['lines']):
                if pl.strip():
                    lb = pl.strip()
                    break
            if not lb:
                continue
            m = TOOL_CALL_RE.match(lb)
            if m:
                e['is_toolcall'] = True
                e['tool_name'] = m.group(1)
                try:
                    obj = json.loads('\n'.join(e['body']))
                    e['input_obj'] = obj
                    if isinstance(obj, dict) and 'script' in obj:
                        e['script'] = obj['script']
                        e['hd'] = split_heredoc(e['script'])
                except Exception:
                    pass
                break
            else:
                break


def build_turns(lines, elements, bounds):
    n = len(lines)
    b_idx = [bi for _, bi in bounds]
    turns = []
    for k in range(len(bounds)):
        bkind, bi = bounds[k]
        nxt = b_idx[k + 1] if k + 1 < len(bounds) else n
        if bkind == 'role':
            role = ROLE_RE.match(lines[bi].strip()).group(1)
            mm = re.match(r'^\*\*(User|Assistant)\*\*\s*\(([^)]*)\)\s*$',
                          lines[bi].strip())
            meta = mm.group(2) if mm else None
        else:
            role, meta = 'Tool', TOOL_OUT_RE.match(lines[bi].strip()).group(1)
        tel = [e for e in elements if e['start'] >= bi and e['start'] < nxt]
        turns.append({'role': role, 'meta': meta, 'elements': tel, 'start': bi})
    return turns


# --------------------------------------------------------------------------- #
# Rendering helpers
# --------------------------------------------------------------------------- #
esc = lambda s: _html.escape(s, quote=False)


def hl(src, lang):
    L = LEX.get(lang, TextLexer)
    try:
        return highlight(src, L(), HtmlFormatter(nowrap=True))
    except Exception:
        return esc(src)


def inline(s):
    s = esc(s)
    parts = []

    def _code(m):
        parts.append(m.group(1))
        return '\x00%d\x00' % (len(parts) - 1)

    s = re.sub(r'`([^`]+)`', _code, s)
    s = re.sub(r'\*\*([^*]+)\*\*', r'<strong>\1</strong>', s)
    s = re.sub(r'(?<!\*)\*([^*\n]+)\*(?!\*)', r'<em>\1</em>', s)
    for i, p in enumerate(parts):
        s = s.replace('\x00%d\x00' % i, '<code>%s</code>' % p)
    return s


def _is_tool_json(s):
    s = s.strip()
    return s.startswith('{"StandardOutput"') or s.startswith('{"exitCode"')


PURE_ITALIC = re.compile(r'^\*([^*]+)\*$')


def _split_row(l):
    l = l.strip()
    if l.startswith('|'):
        l = l[1:]
    if l.endswith('|'):
        l = l[:-1]
    return [c.strip() for c in l.split('|')]


def _is_sep_row(cells):
    return (all(re.fullmatch(r':?-{2,}:?', c) for c in cells if c != '')
            and any(c for c in cells))


def _render_table(rows):
    out = '<table class="md"><thead><tr>'
    out += ''.join('<th>%s</th>' % inline(c) for c in rows[0])
    out += '</tr></thead><tbody>'
    for r in rows[2:]:
        out += '<tr>' + ''.join('<td>%s</td>' % inline(c) for c in r) + '</tr>'
    return out + '</tbody></table>'


def render_prose(par_lines):
    items, para, lst, tab = [], [], [], []

    def fp():
        nonlocal para
        if not para:
            return
        if len(para) >= 2 and all(PURE_ITALIC.match(x.strip()) for x in para):
            inner = [PURE_ITALIC.match(x.strip()).group(1) for x in para]
            if inner and inner[0].rstrip().endswith(':'):
                items.append('<p>%s</p>' % inline(inner[0]))
                items.append('<ul>' + ''.join('<li>%s</li>' % inline(x)
                                              for x in inner[1:]) + '</ul>')
            else:
                items.append('<ul>' + ''.join('<li>%s</li>' % inline(x)
                                              for x in inner) + '</ul>')
            return
        txt = ' '.join(x.strip() for x in para).strip()
        if txt:
            if _is_tool_json(txt):
                items.append('<div class="toolout"><div class="toolout-label">'
                             'TOOL OUTPUT</div><pre>%s</pre></div>' % esc(txt))
            else:
                items.append('<p>%s</p>' % inline(txt))

    def fl():
        nonlocal lst
        if lst:
            items.append('<ul>' + ''.join('<li>%s</li>' % inline(x)
                                          for x in lst) + '</ul>')
        lst = []

    def ft():
        nonlocal tab
        if tab:
            sep = next((ix for ix, r in enumerate(tab) if _is_sep_row(r)), None)
            if sep is not None and len(tab) >= 2:
                items.append(_render_table(tab))
            else:
                for r in tab:
                    items.append('<p>%s</p>' % esc(' | '.join(r)))
        tab = []

    for raw in par_lines:
        l = raw.strip()
        if not l or SEPARATOR_RE.match(l):
            fp(); para = []; fl(); lst = []; ft(); tab = []; continue
        if l.startswith('|'):
            fp(); para = []; fl(); lst = []
            tab.append(_split_row(l))
            continue
        hm = re.match(r'^(#{1,6})\s+(.*)$', l)
        bm = re.match(r'^[-*]\s+(.*)$', l)
        nm = re.match(r'^\d+[.)]\s+(.*)$', l)
        if hm:
            fp(); para = []; fl(); lst = []; ft(); tab = []
            items.append('<h4>%s</h4>' % inline(hm.group(2)))
        elif bm or nm:
            fp(); para = []; ft(); tab = []
            lst.append((bm or nm).group(1))
        else:
            fl(); lst = []; ft(); tab = []
            para.append(l)
    fp(); fl(); ft()
    return items


def _js_like(bl):
    ne = [l for l in bl if l.strip()]
    if not ne:
        return False
    sc = 0
    for l in ne[:20]:
        if (re.search(r'[;{}]\s*$', l)
                or re.search(r'\b(const|let|var|function|return|if|for|while|new|class)\b', l)
                or '=>' in l or '//' in l
                or re.search(r'[a-zA-Z_$]+\(', l)):
            sc += 1
    return sc >= max(2, int(len(ne[:20]) * 0.3))


def render_assist_code(body, lang):
    lang = (lang or '').lower()
    if lang in ('js', 'javascript'):
        use = 'javascript'
    elif lang in ('py', 'python'):
        use = 'python'
    elif lang == 'html':
        use = 'html'
    elif lang in ('bash', 'sh'):
        use = 'bash'
    elif lang == 'json':
        use = 'text'
    else:
        use = 'javascript' if _js_like(body) else 'text'
    src = '\n'.join(body).rstrip('\n')
    inner = esc(src) if use == 'text' else hl(src, use)
    cls = 'code' if use == 'text' else 'code hl'
    return '<div class="%s">%s</div>' % (cls, inner)


def render_toolcall(e):
    tool = e.get('tool_name') or 'tool'
    parts = []
    hd = e.get('hd') or {'kind': 'plain'}
    if hd['kind'] == 'heredoc' and hd.get('code'):
        target = hd.get('target')
        tname = target.split('/')[-1] if target else None
        lang = hd.get('lang') or 'text'
        head = '\n'.join(hd['header'] + [hd['cmd']])
        parts.append('<div class="code hl">%s</div>' % hl(head, 'bash'))
        code_src = '\n'.join(hd['code']).rstrip('\n')
        langname = {'javascript': 'JavaScript', 'python': 'Python',
                    'html': 'HTML', 'bash': 'Bash', 'text': 'Text'}.get(lang, 'Text')
        tlabel = (' &middot; writes <b>%s</b> (%s)' % (esc(tname), langname)
                  if tname else (' &middot; %s script' % langname))
        parts.append('<div class="tc-sub">script body%s</div>' % tlabel)
        parts.append('<div class="code hl">%s</div>' % hl(code_src, lang))
        if hd['footer']:
            foot = '\n'.join(hd['footer'])
            parts.append('<div class="code hl">%s</div>' % hl(foot, 'bash'))
    elif e.get('script') is not None:
        # direct script tool with no heredoc -> highlight as the tool's language
        lang = 'python' if 'python' in tool.lower() else 'bash'
        parts.append('<div class="code hl">%s</div>' % hl(e['script'], lang))
    else:
        # non-shell tool (or unparseable): show the raw JSON input
        obj = e.get('input_obj')
        src = json.dumps(obj, indent=2) if obj is not None else '\n'.join(e['body'])
        parts.append('<div class="code hl">%s</div>' % hl(src, 'text'))
    return ('<div class="toolcall"><div class="tc-label">TOOL INPUT &nbsp;'
            '<span class="tc-name">%s</span></div>%s</div>'
            % (esc(tool), ''.join(parts)))


def assemble_turns(turns):
    out = []
    for t in turns:
        parts = []
        n_el = len(t['elements'])
        for ei, e in enumerate(t['elements']):
            if e['type'] == 'prose':
                plines = list(e['lines'])
                if ei == 0 and plines:
                    l0 = plines[0].strip()
                    if (l0.startswith('**User**') or l0.startswith('**Assistant**')
                            or l0.startswith('**Tool**')):
                        plines = plines[1:]
                # drop a trailing **Tool Call:** marker line (redundant label)
                if (ei + 1 < n_el and t['elements'][ei + 1]['type'] == 'code'
                        and t['elements'][ei + 1].get('is_toolcall')):
                    for j in range(len(plines) - 1, -1, -1):
                        if plines[j].strip():
                            if re.match(r'^\*\*Tool Call:\*\*\s*`[^`]+`\s*$',
                                        plines[j].strip()):
                                plines = plines[:j]
                            break
                parts.extend(render_prose(plines))
            else:
                if e.get('is_toolcall'):
                    parts.append(render_toolcall(e))
                else:
                    parts.append(render_assist_code(e['body'], e.get('lang', '')))
        role = t['role']
        badge = '<span class="badge %s">%s</span>' % (role.lower(), role)
        if t['meta']:
            badge += '<span class="turn-meta">%s</span>' % esc(t['meta'])
        out.append('<section class="turn %s"><div class="th">%s</div>%s</section>'
                   % (role.lower(), badge, ''.join(parts)))
    return out


# --------------------------------------------------------------------------- #
# Document model
# --------------------------------------------------------------------------- #
def parse_transcript(path):
    lines = load_lines(path)
    lines, nfix = fix_truncated_fences(lines)
    bounds = find_boundaries(lines)
    elements = build_elements(lines, bounds)
    tag_tool_calls(elements)
    turns = build_turns(lines, elements, bounds)

    # cover metadata from the header (before the first boundary)
    head_end = bounds[0][1] if bounds else 0
    title = next((l[2:].strip() for l in lines[:head_end]
                  if l.startswith('# ')),
                 os.path.splitext(os.path.basename(path))[0])
    meta_pairs = []
    for m in [x.strip() for x in lines[:head_end]
              if x.strip() and not x.strip().startswith('# ')
              and not SEPARATOR_RE.match(x.strip())]:
        mm = META_RE.match(m)
        meta_pairs.append((mm.group(1).strip(), mm.group(2).strip())
                          if mm else ('', m))

    def meta_val(key, dflt=''):
        for lab, val in meta_pairs:
            if lab.lower() == key.lower():
                return val
        return dflt

    counts = dict(Counter(t['role'] for t in turns))
    toolcalls = [e for e in elements if e['is_toolcall']]
    heredocs = [e for e in toolcalls if (e.get('hd') or {}).get('kind') == 'heredoc']
    assist_code = [e for e in elements if e['type'] == 'code'
                   and not e['is_toolcall']]

    # infer the primary deliverable: most-written non-/tmp file target
    targets = Counter((e['hd']['target'] for e in heredocs
                       if e['hd'].get('target')
                       and not e['hd']['target'].startswith('/tmp')))
    deliverable = targets.most_common(1)[0][0] if targets else None

    # user-request snippets for the cover
    user_snippets = []
    for t in turns:
        if t['role'] == 'User':
            txt = ''.join(
                (''.join(e['lines']) if e['type'] == 'prose' else ' ')
                for e in t['elements'])
            txt = re.sub(r'\s+', ' ', txt).strip()
            txt = re.sub(r'^\*\*User\*\*\s*', '', txt)
            if txt:
                user_snippets.append(txt[:140])

    return {
        'lines': lines, 'elements': elements, 'turns': turns,
        'title': title, 'meta_pairs': meta_pairs, 'meta_val': meta_val,
        'counts': counts, 'n_lines': len(lines), 'n_fixes': nfix,
        'n_toolcalls': len(toolcalls),
        'n_heredocs': len(heredocs), 'n_assist_code': len(assist_code),
        'deliverable': deliverable, 'user_snippets': user_snippets,
    }


# --------------------------------------------------------------------------- #
# HTML / PDF
# --------------------------------------------------------------------------- #
CSS_BODY = """
@page { size: A4; margin: 16mm 14mm 18mm 14mm;
  @bottom-left { content:"__FOOTER__"; font-size:7.5pt; color:#8a97a5; font-family:"DejaVu Sans",sans-serif; }
  @bottom-right { content:"Page " counter(page) " of " counter(pages); font-size:7.5pt; color:#8a97a5; font-family:"DejaVu Sans",sans-serif; } }
@page:first { @bottom-left{content:none} @bottom-right{content:none} }
*{box-sizing:border-box}
body{font-family:"DejaVu Sans",sans-serif;font-size:9.2pt;line-height:1.5;color:#22303c;margin:0}
.cover{page-break-after:always;height:247mm;display:flex;flex-direction:column;overflow:hidden}
.cover-accent{height:6mm;background:linear-gradient(90deg,#1b6fb8,#35a3d8 55%,#7fd4e8)}
.cover-body{flex:1;min-height:0;display:flex;flex-direction:column;padding:26mm 12mm 0 12mm}
.cover-kicker{font-size:10pt;letter-spacing:2.5px;text-transform:uppercase;color:#1b6fb8;font-weight:bold;margin-bottom:6mm}
.cover h1{font-size:25pt;line-height:1.22;color:#12283a;margin:0 0 6mm 0}
.cover-sub{font-size:10.5pt;color:#48586a;margin-bottom:9mm;line-height:1.55}
.cover-meta{width:100%;border-collapse:collapse;margin-bottom:9mm}
.cover-meta td{border-bottom:0.3pt solid #d7e1ea;padding:2.2mm 2mm;font-size:9.5pt;vertical-align:top}
.cover-meta td.k{width:33mm;color:#7a8a9a;text-transform:uppercase;letter-spacing:1px;font-size:7.8pt;font-weight:bold;padding-top:3mm}
.cover-contents{flex:1 1 0;min-height:0;overflow:hidden}
.cc-h{font-size:11pt;color:#12283a;border-bottom:1pt solid #1b6fb8;padding-bottom:2mm;margin:0 0 3.5mm 0;letter-spacing:1px;text-transform:uppercase}
.cc ol{margin:0;padding-left:5mm}
.cc li{margin-bottom:3mm;font-size:9.5pt;color:#33475a;break-inside:avoid}
.cc .t{color:#1b6fb8;font-weight:bold;font-size:8pt;margin-right:2mm}
.cc .s{color:#6b7c8d;font-size:8.6pt;display:block;margin-top:0.8mm}
.stat-row{display:flex;gap:3.5mm;margin-bottom:4mm}
.stat{flex:1;background:#eef5fa;border:0.3pt solid #cfe2ef;border-radius:1.5mm;padding:3mm 3mm;text-align:center}
.stat .n{font-size:16pt;font-weight:bold;color:#1b6fb8;line-height:1.1}
.stat .l{font-size:7.2pt;color:#5b6c7c;text-transform:uppercase;letter-spacing:0.7px;margin-top:1mm}
.cover-foot{font-size:8.5pt;color:#7a8a9a;border-top:0.3pt solid #d7e1ea;padding-top:3mm;margin-top:5mm}
.doc{page-break-before:always}
h3.part{font-size:13pt;color:#12283a;margin:0 0 5mm 0;padding:2.5mm 3mm;background:#eef5fa;border-left:2.2mm solid #1b6fb8;border-radius:1mm}
section.turn{margin-bottom:4mm;break-inside:auto}
.th{display:flex;align-items:baseline;margin-bottom:1.8mm}
.badge{font-size:7.6pt;font-weight:bold;letter-spacing:1.2px;text-transform:uppercase;padding:0.8mm 2.6mm;border-radius:1mm;color:#fff}
.badge.user{background:#1b6fb8}.badge.assistant{background:#2e7d4f}.badge.tool{background:#8a97a5}
.turn-meta{font-size:8pt;color:#6b7c8d;margin-left:2mm;font-style:italic}
section.turn p{margin:0 0 2.2mm 0}
section.turn ul{margin:0 0 2.2mm 0;padding-left:5mm}
section.turn li{margin-bottom:1mm}
h4{font-size:10pt;color:#12283a;margin:3mm 0 1.5mm 0}
code{font-family:"DejaVu Sans Mono",monospace;font-size:8.2pt;background:#eef2f6;padding:0 1mm;border-radius:0.6mm;color:#0d5b8a}
.code{background:#f6f8fa;border:0.3pt solid #d9e2ea;border-left:1.6mm solid #9fc3d9;border-radius:1mm;padding:2.2mm 2.8mm;margin:0 0 2.6mm 0;overflow-wrap:anywhere;white-space:pre-wrap;font-family:"DejaVu Sans Mono",monospace;font-size:7.4pt;line-height:1.45;color:#24292e}
.toolout{background:#faf7f0;border:0.3pt solid #e4dcc8;border-left:1.6mm solid #c9b98a;border-radius:1mm;padding:1.8mm 2.5mm;margin:0 0 2.4mm 0;break-inside:auto}
.toolout-label{font-size:6.8pt;letter-spacing:1.2px;color:#9a8c5f;font-weight:bold;margin-bottom:1mm}
.toolout pre{white-space:pre-wrap;overflow-wrap:anywhere;font-size:7.2pt;color:#4a4234;line-height:1.4;font-family:"DejaVu Sans Mono",monospace}
.toolcall{background:#f0f7f4;border:0.3pt solid #cfe6da;border-left:1.6mm solid #4f9d7a;border-radius:1mm;padding:1.8mm 2.5mm 2.4mm 2.5mm;margin:0 0 2.8mm 0;break-inside:auto}
.toolcall .code{margin-bottom:1.8mm}
.tc-label{font-size:7pt;letter-spacing:1.3px;color:#2e7d4f;font-weight:bold;margin-bottom:1.4mm}
.tc-name{font-family:"DejaVu Sans Mono",monospace;font-size:8pt;background:#e2f0e8;color:#2e7d4f;padding:0.4mm 1.8mm;border-radius:0.8mm;letter-spacing:0}
.tc-sub{font-size:7.4pt;color:#5b7d6d;margin:0.4mm 0 1.2mm 0;font-style:italic}
table.md{width:100%;border-collapse:collapse;margin:0 0 3mm 0;font-size:8.5pt}
table.md th{background:#eef5fa;color:#12283a;text-align:left;padding:1.8mm 2.2mm;border:0.3pt solid #cfe2ef;font-size:8.1pt}
table.md td{padding:1.8mm 2.2mm;border:0.3pt solid #d7e1ea;vertical-align:top}
table.md tbody tr:nth-child(even) td{background:#f7fafc}
__PYG__
"""


def build_cover(model):
    counts = model['counts']
    title = esc(model['title'])
    mv = model['meta_val']
    deliverable = model['deliverable']
    dline = ('<tr><td class="k">Deliverable</td><td><code>%s</code></td></tr>'
             % esc(deliverable)) if deliverable else ''
    stat1 = (
        '<div class="stat-row">'
        '<div class="stat"><div class="n">%d</div><div class="l">Messages</div></div>'
        '<div class="stat"><div class="n">%d</div><div class="l">User</div></div>'
        '<div class="stat"><div class="n">%d</div><div class="l">Assistant</div></div>'
        '<div class="stat"><div class="n">%d</div><div class="l">Tool calls</div></div>'
        '<div class="stat"><div class="n">%d</div><div class="l">Heredoc scripts</div></div>'
        '</div>'
        % (len(model['turns']), counts.get('User', 0), counts.get('Assistant', 0),
           model['n_toolcalls'], model['n_heredocs']))
    stat2 = (
        '<div class="stat-row">'
        '<div class="stat"><div class="n">%d</div><div class="l">Tool outputs</div></div>'
        '<div class="stat"><div class="n">%d</div><div class="l">Inline code blocks</div></div>'
        '<div class="stat"><div class="n">%d</div><div class="l">Source lines</div></div>'
        '</div>'
        % (counts.get('Tool', 0), model['n_assist_code'], model['n_lines']))
    snips = model['user_snippets'][:4]  # cap to avoid cover overflow
    snip = ''.join(
        '<li><span class="t">User %d</span>%s <span class="s">&hellip;</span></li>'
        % (i + 1, esc(t)) for i, t in enumerate(snips))
    if len(model['user_snippets']) > 4:
        snip += '<li><span class="s">+ %d more requests&hellip;</span></li>' % (len(model['user_snippets']) - 4)
    sub = ('Complete session transcript &mdash; including every tool call. The original '
           'request, the iterative build, all %d tool calls with their full script '
           'inputs (%d heredoc scripts), their outputs, and the dialogue.'
           % (model['n_toolcalls'], model['n_heredocs']))
    meta_rows = ''
    if mv('Created'):
        meta_rows += '<tr><td class="k">Created</td><td>%s</td></tr>' % esc(mv('Created'))
    if mv('Last Activity'):
        meta_rows += '<tr><td class="k">Last activity</td><td>%s</td></tr>' % esc(mv('Last Activity'))
    if mv('Assistant'):
        meta_rows += '<tr><td class="k">Assistant</td><td>%s</td></tr>' % esc(mv('Assistant'))
    meta_rows += dline
    meta_tbl = ('<table class="cover-meta">%s</table>' % meta_rows) if meta_rows else ''
    foot = ('Generated from the markdown conversation log &middot; %d source lines &middot; '
            '%d tool calls rendered with full script bodies'
            % (model['n_lines'], model['n_toolcalls']))
    return (
        '<div class="cover"><div class="cover-accent"></div><div class="cover-body">'
        '<div class="cover-kicker">Conversation Transcript</div>'
        '<h1>%s</h1>'
        '<div class="cover-sub">%s</div>'
        '%s%s%s%s'
        '<div class="cover-contents"><div class="cc-h">User Requests in This Session</div>'
        '<ol>%s</ol></div>'
        '<div class="cover-foot">%s</div>'
        '</div></div>'
        % (title, sub, stat1, stat2, meta_tbl, meta_tbl and '' or '', snip, foot))


def build_html(model, with_cover=True):
    sections = ''.join(assemble_turns(model['turns']))
    pyg = HtmlFormatter(style='friendly').get_style_defs('.hl')
    css = CSS_BODY.replace('__FOOTER__', esc(model['title'])).replace('__PYG__', pyg)
    cover = build_cover(model) if with_cover else ''
    doc = ('<div class="doc"><h3 class="part">Session Transcript</h3>%s</div>'
           % sections)
    return ('<!DOCTYPE html><html><head><meta charset="utf-8">'
            '<title>%s</title><style>%s</style></head><body>%s%s</body></html>'
            % (esc(model['title']), css, cover, doc))


def to_pdf(html_str, out_path):
    from weasyprint import HTML
    HTML(string=html_str).write_pdf(out_path)


# --------------------------------------------------------------------------- #
# CLI
# --------------------------------------------------------------------------- #
def main(argv=None):
    ap = argparse.ArgumentParser(
        description='Convert a Code-Executor markdown transcript to a formatted PDF.')
    ap.add_argument('input', help='input .md transcript')
    ap.add_argument('-o', '--output', help='output .pdf (default: <input>.pdf)')
    ap.add_argument('--no-cover', action='store_true', help='omit the cover page')
    ap.add_argument('--html', metavar='PATH', help='also write the intermediate HTML')
    ap.add_argument('--quiet', action='store_true')
    args = ap.parse_args(argv)

    out = args.output or (os.path.splitext(args.input)[0] + '.pdf')
    model = parse_transcript(args.input)
    html_str = build_html(model, with_cover=not args.no_cover)
    if args.html:
        with open(args.html, 'w', encoding='utf-8') as f:
            f.write(html_str)
    to_pdf(html_str, out)
    if not args.quiet:
        c = model['counts']
        print('Title      : %s' % model['title'])
        print('Deliverable: %s' % (model['deliverable'] or '-'))
        print('Turns      : %d  (User %d / Assistant %d / Tool %d)'
              % (len(model['turns']), c.get('User', 0), c.get('Assistant', 0),
                 c.get('Tool', 0)))
        print('Tool calls : %d  (heredoc scripts %d)   inline code %d'
              % (model['n_toolcalls'], model['n_heredocs'], model['n_assist_code']))
        print('Fences fixed: %d   source lines %d' % (model['n_fixes'], model['n_lines']))
        print('PDF        : %s' % out)
    return 0


if __name__ == '__main__':
    sys.exit(main())
