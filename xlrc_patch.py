"""XL Strength patcher — ship-ready consolidator for end users.

Adds `uniform float xlrc_strength` (slider 0-1, default 1.0 = stock look) +
a `lerp(original, effected, xlrc_strength)` final composite to ReShade
effect files, so the animator can fade effects instead of toggling them.
Presence of the name IS the marker (no stock shader ships it).

Two transforms (both strict, assert-or-skip):
  value-style: finals of form `float3/4 F(float2 uv, ...)` with exactly one
    `return expr;` — orig tap + rewritten return.
  void-style: finals of form `void F(..., out float3/4 outvar, ...)` with
    zero returns and a top-level full write of the out-var — orig tap +
    end-insert before the closing brace.

Usage (Windows; run with the GAME CLOSED):
  python xlrc_patch.py                     # dry-run report, no writes
  python xlrc_patch.py --apply             # backup (.xlrcbak) + patch
  python xlrc_patch.py --restore           # revert every .xlrcbak, delete baks
  python xlrc_patch.py --apply --filter qUINT   # only paths containing this
  python xlrc_patch.py --shaders PATH      # override shaders dir
  python xlrc_patch.py --apply --force --filter MyFx.fx  # single-file live fix

Shader dir auto-detect (no hardcoded paths):
  1. --shaders PATH, 2. reshade-shaders\\Shaders next to this script
  (i.e. drop the script in the game dir), 3. ./reshade-shaders/Shaders.

Safety: .xlrcbak written once (never overwritten); balance invariant
(braces identical, parens grow by balanced pairs only) refuses bad edits;
already-patched / stock-infra / debug files are skipped
with reasons. --apply refuses while ffxiv_dx11.exe is running.

No redistribution involved: this patches the user's own installed files.
"""

from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys

DECL = ("// XLRC master strength (added for transitions; default 1 = stock look).\n"
        "uniform float xlrc_strength <\n"
        '\tui_type = "slider";\n'
        "\tui_min = 0.0; ui_max = 1.0;\n"
        '\tui_label = "XL Strength";\n'
        '\tui_tooltip = "XL master fade: 0 = bypass, 1 = full effect. For animated transitions.";\n'
        "> = 1.0;\n")

SKIP_FILES = {'KeepUI.fx', 'KeepUI_FFXIV.fx', 'MartysMods_LAUNCHPAD.fx'}
SKIP_DEBUG = {'DisplayDepth.fx'}  # debug views are never faded

# Hand-authored recipes for manual-tail files worth covering. The human
# adding an entry verifies two things the machine can't: the tap reads the
# effect's true input (BackBuffer pristine at the final — every earlier
# pass must render to an explicit target), and the selected returns are
# the composite exits (debug constants/guards stay raw). All other guards
# still apply, including the backbuffer-token check which overrides waive.
OVERRIDES = {
    # Wrap returns whose expression contains any listed substring.
    'AmbientLight.fx': {'match': ['baseSampleMix']},
    # Wrap the final's last return (earlier ones are debug/guards). 'tap'
    # names the sampler to read the input through (default BackBuffer;
    # MagicHDR never imports the ReShade namespace, so it uses its own
    # :COLOR-bound Color sampler instead).
    'MagicHDR.fx': {'last': True, 'tap': 'Color'},
    'Clarity2.fx': {'last': True},
}
# NOTE: native 0-1 bypass masters (Sepia, MagicHDR, Clarity2, ...) used to
# be skipped so the animator would drive native instead. Dropped: at
# xlrc_strength = 1.0 the wrap is identity, so the slider is purely
# additive and existing presets (XL unticked = 1.0) keep their look.


# ---------------------------------------------------------------------------
# Shared scanning infra
# ---------------------------------------------------------------------------

def strip_all(t: str) -> str:
    # Analysis space (positions need not map): line comments blanked first
    # so stray /* inside them can't unbalance block stripping, then blocks,
    # then the branch-selection directive family.
    masked = mask_regions(t, line_spans(t))
    if len(re.findall(r'/\*', masked)) == len(re.findall(r'\*/', masked)):
        masked = re.sub(r'/\*.*?\*/', '', masked, flags=re.S)
    masked = re.sub(r'^\s*#(if|ifdef|ifndef|elif|else|endif|error|warning|pragma|line|undef)\b.*$', '', masked, flags=re.M)
    return masked


def brace_body(t: str, i: int):
    depth = 0; p = i; ins = False
    while p < len(t):
        ch = t[p]
        if ins:
            if ch == '\\': p += 1
            elif ch == '"': ins = False
        elif ch == '"': ins = True
        elif ch == '{': depth += 1
        elif ch == '}':
            depth -= 1
            if depth == 0:
                return t[i:p+1]
        p += 1
    return None


def line_spans(raw: str):
    """Quote-aware // spans for the whole file."""
    spans = []
    off = 0
    for line in raw.split('\n'):
        ins = False; i = 0
        while i + 1 < len(line):
            ch = line[i]
            if ins:
                if ch == '\\': i += 1
                elif ch == '"': ins = False
            elif ch == '"': ins = True
            elif ch == '/' and line[i+1] == '/':
                spans.append((off+i, off+len(line)))
                break
            i += 1
        off += len(line) + 1
    return spans


def mask_regions(raw: str, spans) -> str:
    """Blank out spans (newlines kept) so later scans see no phantom code."""
    chars = list(raw)
    for a, b in spans:
        for i in range(a, min(b, len(chars))):
            if chars[i] != '\n':
                chars[i] = ' '
    return ''.join(chars)


def norm_ws(s: str) -> str:
    return re.sub(r'\s+', ' ', s).strip()


def in_preproc_region(raw: str, pos: int) -> bool:
    """True when pos sits inside an #if...#endif region (naive nesting
    scan; branch selection is NOT evaluated, so overrides refuse to touch
    conditional code — Clarity2 class)."""
    depth = 0
    off = 0
    for line in raw[:pos].split('\n'):
        s = line.strip()
        if re.match(r'#\s*(if|ifdef|ifndef)\b', s):
            depth += 1
        elif re.match(r'#\s*endif\b', s):
            depth = max(0, depth - 1)
    return depth > 0


def comment_spans(raw: str):
    # Line comments FIRST: stray /* inside // lines (BSD "//*" license
    # rows) must not count toward block balance, or comment text parses
    # as code (ColorfulPoster class: a "return" inside a comment).
    line = line_spans(raw)
    masked = mask_regions(raw, line)
    spans = list(line)
    if len(re.findall(r'/\*', masked)) == len(re.findall(r'\*/', masked)):
        for m in re.finditer(r'/\*.*?\*/', masked, flags=re.S):
            spans.append((m.start(), m.end()))
    return spans


def in_span(spans, idx):
    return any(a <= idx < b for a, b in spans)


def decl_pos(raw: str):
    """Offset after BOM + leading header. None if real code starts first."""
    pos = 0
    if raw.startswith('\ufeff'):
        pos = 1
    n = len(raw)
    while pos < n:
        m = re.match(r'[ \t]*\r?\n', raw[pos:])
        if m:
            pos += m.end()
            continue
        if raw.startswith('//', pos):
            e = raw.find('\n', pos)
            pos = n if e < 0 else e + 1
            continue
        if raw.startswith('/*', pos):
            e = raw.find('*/', pos + 2)
            if e < 0:
                return None
            pos = e + 2
            continue
        m = re.match(r'#\s*(include|define|pragma)\b[^\n]*\n?', raw[pos:])
        if m:
            pos += m.end()
            # swallow backslash continuations (multi-line macros). DECL
            # must never split one (Dehaze/Pong/pkd_LayerCake failures).
            while pos >= 2 and raw[pos - 2] == '\\' and raw[pos - 1] == '\n':
                e = raw.find('\n', pos)
                pos = n if e < 0 else e + 1
            continue
        break
    prefix = re.sub(r'/\*.*?\*/', '', raw[:pos], flags=re.S)
    if re.search(r'\b(uniform|technique|namespace|texture|sampler|float[234]?|void|static|struct)\b', prefix):
        return None
    return pos


def check_balance(raw: str, out: str) -> bool:
    def counts(x):
        x = strip_all(x)
        return (x.count('{'), x.count('}'), x.count('('), x.count(')'))
    a, b = counts(raw), counts(out)
    return a[0] == b[0] and a[1] == b[1] and (b[2] - a[2]) == (b[3] - a[3])


def collect_finals(t: str):
    """Distinct non-RenderTarget final pixel-shader fns per technique set."""
    techs = []
    for tm in re.finditer(r'^\s*technique\s+(\w+)', t, re.M):
        bi = t.find('{', tm.end())
        if bi < 0:
            continue
        body = brace_body(t, bi)
        if not body:
            continue
        passes = []
        for pm in re.finditer(r'\bpass\b[^{]*\{', body):
            pbi = body.find('{', pm.start())
            pbody = brace_body(body, pbi)
            if not pbody:
                continue
            psm = re.search(r'PixelShader\s*=\s*([\w:]+)', pbody)
            rtm = re.search(r'RenderTarget\s*=', pbody)
            ps = psm.group(1).split(':')[-1] if psm else None
            passes.append({'ps': ps, 'rt': bool(rtm)})
        techs.append({'name': tm.group(1), 'passes': passes})
    if not techs:
        return None, 'no-techniques'
    finals = {}
    for tech in techs:
        fout = None
        for p in reversed(tech['passes']):
            if not p['rt'] and p['ps']:
                fout = p['ps']
                break
        if fout:
            finals.setdefault(fout, []).append(tech['name'])
    if not finals:
        return None, 'no-bb-final-any-tech'
    for fout in finals:
        for tech in techs:
            for p in tech['passes']:
                if p['ps'] == fout and p['rt']:
                    return None, f'{fout}-shared-midchain'
    return (techs, finals), ''


def backbuffer_ok(t: str, raw: str) -> bool:
    return 'ReShade::BackBuffer' in t or re.search(r'^\s*#include\s+"([^"]*/)?ReShade\.fxh"', raw, re.M) is not None


# ---------------------------------------------------------------------------
# Value-style transform: float3/4 F(float2 uv, ...) with one return expr
# ---------------------------------------------------------------------------

def try_value(raw: str, t: str, spans, finals, ov=None):
    edits = []
    for fout in finals:
        m = re.search(r'\b(float[34])\s+' + re.escape(fout) + r'\s*\(([^)]*)\)[^{]*\{', t)
        if not m:
            return None, f'{fout}-fn-parse'
        ret, params = m.group(1), m.group(2)
        uvm = re.search(r'float2\s+(\w+)', params)
        if not uvm:
            return None, f'{fout}-no-uv-param'
        uv = uvm.group(1)
        bi = t.find('{', m.start())
        body = brace_body(t, bi)
        if not body:
            return None, f'{fout}-no-body'
        if re.search(r'toOutputColorspace|getBackBufferLinear|toLinearColorspace', body):
            return None, f'{fout}-colorspace'
        rets = re.findall(r'\breturn\b([^;]*);', body)
        if ov is not None and 'match' in ov:
            # Wrap every return whose expression contains any listed
            # substring, each with its own tap (scope-safe).
            targets = [(j, r) for j, r in enumerate(rets) if any(s in r for s in ov['match'])]
            if not targets:
                return None, f'{fout}-override-nomatch'
            if re.search(r'\bxlrc_%s(_o\d+)?_orig\b' % re.escape(fout), body):
                return None, f'{fout}-name-clash'
            last_mode = False
        elif ov is not None and ov.get('last'):
            # Wrap the final's last return (earlier ones are debug/guards).
            if not rets:
                return None, f'{fout}-override-nomatch'
            targets = [(len(rets) - 1, rets[-1])]
            if re.search(r'\bxlrc_%s(_o\d+)?_orig\b' % re.escape(fout), body):
                return None, f'{fout}-name-clash'
            last_mode = True
        else:
            if len(rets) != 1:
                return None, f'{fout}-returns-{len(rets)}'
            targets = [(0, rets[0])]
            last_mode = False
        # Locate the exact returns via masked RAW (same offsets): a
        # comment-text "return" would otherwise swallow the real one's
        # semicolon and shadow it (ColorfulPoster class).
        masked_raw = mask_regions(raw, spans)
        for j, r in targets:
            expr = r.strip()
            if not expr:
                return None, f'{fout}-empty-return'
            suf = f'_o{j}' if ov is not None else ''
            cands = [mm for mm in re.finditer(r'\breturn\b([^;]*);', masked_raw)]
            same = [mm for mm in cands if norm_ws(mm.group(1)) == norm_ws(expr)]
            if last_mode:
                # Earliest candidates are the debug/guards; the composite
                # exit is the last one standing. It must not sit in
                # conditional code (truncated-body class: Clarity2).
                match = same[-1:] if same else []
                if match and in_preproc_region(raw, match[0].start()):
                    return None, f'{fout}-conditional-return'
            else:
                match = same
            if len(match) != 1:
                return None, f'{fout}-raw-return-ambiguous-{len(match)}'
            rstmt = match[0]
            if ov is not None and not last_mode and in_preproc_region(raw, rstmt.start()):
                continue  # conditional target: leave raw, keep others
            line_start = raw.rfind('\n', 0, rstmt.start()) + 1
            # Braceless-if hazard: inserting between `if (c)` and its return
            # would unhook the return. Bail to manual.
            prev_code = None
            for pl in raw[:line_start].split('\n')[::-1]:
                if pl.strip() == '' or pl.strip().startswith('//'):
                    continue
                prev_code = pl.strip()
                break
            if prev_code is not None and re.search(r'\)\s*$', prev_code) and not prev_code.endswith('{') and not prev_code.endswith(';'):
                return None, f'{fout}-braceless-if'
            indent = re.match(r'[ \t]*', raw[line_start:]).group(0)
            tap_src = ov.get('tap', 'ReShade::BackBuffer') if isinstance(ov, dict) else 'ReShade::BackBuffer'
            origline = f'{indent}float4 xlrc_{fout}{suf}_orig = tex2D({tap_src}, {uv});'
            if ret == 'float4':
                newret = f'{indent}return lerp(xlrc_{fout}{suf}_orig, ({expr}), xlrc_strength);'
            else:
                newret = f'{indent}return lerp(xlrc_{fout}{suf}_orig.rgb, ({expr}), xlrc_strength);'
            edits.append((rstmt.start(), rstmt.end(), origline + '\n' + newret, f'{fout}-return{suf}'))
        if ov is not None and not edits:
            return None, f'{fout}-override-nomatch'
    return edits, ''


# ---------------------------------------------------------------------------
# Void-style transform: void F(..., out float3/4 o, ...), end-insert
# ---------------------------------------------------------------------------

def strip_map(raw: str):
    n = len(raw)
    line = line_spans(raw)
    masked = mask_regions(raw, line)
    spans = list(line)
    if len(re.findall(r'/\*', masked)) == len(re.findall(r'\*/', masked)):
        for m in re.finditer(r'/\*.*?\*/', masked, flags=re.S):
            spans.append((m.start(), m.end()))
    dire = []
    for m in re.finditer(r'^[ \t]*#(if|ifdef|ifndef|elif|else|endif|error|warning|pragma|line|undef)\b[^\n]*', masked, flags=re.M):
        dire.append((m.start(), m.end()))
    skip = sorted(spans + dire)
    out = []
    mp = []
    i = 0
    k = 0
    while i < n:
        if k < len(skip) and skip[k][0] <= i < skip[k][1]:
            i = skip[k][1]
            if k + 1 < len(skip) and skip[k + 1][0] < i:
                k += 1
                continue
            k += 1
            continue
        if k < len(skip) and i >= skip[k][1]:
            k += 1
            continue
        out.append(raw[i])
        mp.append(i)
        i += 1
    return ''.join(out), mp


def try_void(raw: str, t: str, finals):
    edits = []
    for fout in sorted(finals):
        m = re.search(r'\bvoid\s+(?:\w+::)?' + re.escape(fout) + r'\s*\(([^)]*)\)', t)
        if not m:
            return None, f'{fout}-fn-parse'
        params = m.group(1)
        outs = re.findall(r'\bout\s+(float[34]?)\s+(\w+)', params)
        if len(outs) != 1 or outs[0][0] not in ('float4', 'float3'):
            return None, f'{fout}-out-sig'
        otype, oname = outs[0]
        uvm = re.search(r'float2\s+(\w+)\s*:\s*TEXCOORD', params)
        if uvm:
            uv = uvm.group(1)
        else:
            allf2 = re.findall(r'(?<![\w:])float2\s+(\w+)', params)
            if len(allf2) != 1:
                return None, f'{fout}-no-uv-param'
            uv = allf2[0]
        if uv == oname:
            return None, f'{fout}-uv-is-outparam'
        bi = t.find('{', m.start())
        body = brace_body(t, bi)
        if not body:
            return None, f'{fout}-no-body'
        if re.search(r'toOutputColorspace|getBackBufferLinear|toLinearColorspace', body):
            return None, f'{fout}-colorspace'
        if re.search(r'\breturn\b', body):
            return None, f'{fout}-has-returns'
        # The fade READS the out-var: require a top-level full write, or a
        # conditional-only read becomes a hard x4000 (FluoroDuoTone class).
        toplevel = body
        while True:
            nub = re.sub(r'\{[^{}]*\}', '', toplevel)
            if nub == toplevel:
                break
            toplevel = nub
        if not re.search(r'\b' + re.escape(oname) + r'\s*=(?!=)', toplevel):
            return None, f'{fout}-out-cond-write'
        if re.search(r'\bxlrc_%s_orig\b' % re.escape(fout), body):
            return None, f'{fout}-name-clash'
        # NOTE: void path re-strips with offset mapping (match start !=
        # brace start); the caller passes map-capable raw/t here.
        edits.append((fout, otype, oname, uv, bi, body))
    return edits, ''


def apply_void_edits(raw: str, t: str, mp, void_edits):
    out_edits = []
    for fout, otype, oname, uv, bi, body in void_edits:
        stripend = bi + len(body) - 1  # stripped offset of '}'
        if stripend >= len(mp):
            return None, f'{fout}-map-range'
        rawbrace = mp[stripend]
        line_start = raw.rfind('\n', 0, rawbrace) + 1
        bprefix = re.match(r'[ \t]*', raw[line_start:]).group(0)
        step = '\t' if '\n\t' in raw else '    '
        indent = bprefix + step
        if otype == 'float4':
            fade = f'{indent}{oname} = lerp(xlrc_{fout}_orig, {oname}, xlrc_strength);'
        else:
            fade = f'{indent}{oname} = lerp(xlrc_{fout}_orig.rgb, {oname}, xlrc_strength);'
        origline = f'{indent}float4 xlrc_{fout}_orig = tex2D(ReShade::BackBuffer, {uv});'
        out_edits.append((rawbrace, rawbrace + 1,
                          origline + '\n' + fade + '\n' + raw[line_start:line_start + len(bprefix)] + '}',
                          f'{fout}-tail'))
    return out_edits, ''


# ---------------------------------------------------------------------------
# Per-file driver
# ---------------------------------------------------------------------------

def process(path: str, apply: bool):
    """Returns (status, detail). Writes only when apply."""
    fn = os.path.basename(path)
    if fn in SKIP_FILES:
        return ('skip-stock-infra', '')
    if fn in SKIP_DEBUG:
        return ('skip-debug-view', '')
    raw = open(path, encoding='utf-8', errors='replace').read()
    if 'xlrc_strength' in raw:
        return ('skip-has-xlrc', '')
    if 'xlrc_' in raw:
        return ('manual', 'has-other-xlrc')
    spans = comment_spans(raw)
    t = strip_all(raw)
    got = collect_finals(t)
    if got[0] is None:
        return ('manual', got[1])
    _, finals = got[0]
    ov = OVERRIDES.get(fn)
    # Overrides waive the token gate: the recipe author verified the tap
    # reads the true input (see OVERRIDES).
    if ov is None and not backbuffer_ok(t, raw):
        return ('manual', 'no-backbuffer-token')

    vedits, vwhy = try_value(raw, t, spans, finals, ov)
    if vedits is not None:
        return finish(path, raw, vedits, 'value', apply, finals, spans)

    # Value form didn't fit — try void form (own offset-mapped strip).
    tv, mp = strip_map(raw)
    got2 = collect_finals(tv)
    if got2[0] is None:
        return ('manual', got2[1] + '|value:' + vwhy)
    _, finals2 = got2[0]
    if not backbuffer_ok(tv, raw):
        return ('manual', 'no-backbuffer-token|value:' + vwhy)
    wedits, wwhy = try_void(raw, tv, finals2)
    if wedits is None:
        return ('manual', wwhy + '|value:' + vwhy)
    redits, rwhy = apply_void_edits(raw, tv, mp, wedits)
    if redits is None:
        return ('manual', rwhy + '|value:' + vwhy)
    return finish(path, raw, redits, 'void', apply, finals2, spans)


def finish(path: str, raw: str, edits, flavor: str, apply: bool, finals, spans) -> tuple:
    dpos = decl_pos(raw)
    if dpos is None:
        return ('manual', 'no-clean-decl-site|' + flavor)
    # Every final fn must be defined after the decl point (raw space,
    # comment occurrences skipped) or HLSL decl-before-use breaks.
    for fout in finals:
        fdefs = [mm for mm in re.finditer(r'\b(?:float[34]|void)\s+(?:\w+::)?' + re.escape(fout) + r'\s*\(', raw)
                 if not in_span(spans, mm.start())]
        if not fdefs or min(mm.start() for mm in fdefs) < dpos:
            return ('manual', f'{fout}-decl-order|' + flavor)
    if apply:
        bak = path + '.xlrcbak'
        if not os.path.exists(bak):
            open(bak, 'w', encoding='utf-8', newline='').write(raw)
        out = raw
        for s, e, rep, _ in sorted(edits, key=lambda x: -x[0]):
            out = out[:s] + rep + out[e:]
        out = out[:dpos] + '\n' + DECL + out[dpos:]
        if not check_balance(raw, out):
            return ('REFUSED-unbalanced', flavor)
        open(path, 'w', encoding='utf-8', newline='').write(out)
        return ('APPLIED', flavor + ':' + '; '.join(d[3] for d in edits))
    return ('GENERATABLE', flavor + ':' + '; '.join(d[3] for d in edits))


# ---------------------------------------------------------------------------
# CLI driver
# ---------------------------------------------------------------------------

def find_shaders_dir(cli: str | None) -> str:
    if cli:
        return cli
    here = os.path.dirname(os.path.abspath(__file__))
    cand = os.path.join(here, 'reshade-shaders', 'Shaders')
    if os.path.isdir(cand):
        return cand
    cand = os.path.join(os.getcwd(), 'reshade-shaders', 'Shaders')
    if os.path.isdir(cand):
        return cand
    return ''


def game_running() -> bool:
    try:
        p = subprocess.run(['tasklist', '/FI', 'IMAGENAME eq ffxiv_dx11.exe', '/NH'],
                           capture_output=True, text=True, timeout=15)
        return 'ffxiv_dx11.exe' in p.stdout.lower()
    except OSError:
        return False


def iter_fx(root: str, filtr: str):
    for dirpath, _, fns in os.walk(root):
        for fn in sorted(fns):
            if not fn.endswith('.fx'):
                continue
            full = os.path.join(dirpath, fn)
            if filtr and filtr.lower() not in full.lower():
                continue
            yield full


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="Add xlrc_strength fade support to ReShade shaders.")
    ap.add_argument('--apply', action='store_true', help='Write patches (default: dry-run report).')
    ap.add_argument('--restore', action='store_true', help='Revert every .xlrcbak and delete the baks.')
    ap.add_argument('--shaders', default=None, help='Shaders dir (auto-detected otherwise).')
    ap.add_argument('--filter', default='', help='Only paths containing this (e.g. qUINT).')
    ap.add_argument('--force', action='store_true',
                    help='Apply even with the game running (single-file live fixes only).')
    a = ap.parse_args(argv)

    root = find_shaders_dir(a.shaders)
    if not root or not os.path.isdir(root):
        print('Shaders dir not found. Pass --shaders PATH (the reshade-shaders\\Shaders folder),')
        print('or drop this script in the game dir next to reshade-shaders\\.')
        return 2
    print(f'Shaders: {root}')

    if a.restore:
        n = 0
        for dirpath, _, fns in os.walk(root):
            for fn in sorted(fns):
                if not fn.endswith('.xlrcbak'):
                    continue
                bak = os.path.join(dirpath, fn)
                dst = bak[:-len('.xlrcbak')]
                try:
                    with open(bak, encoding='utf-8', errors='replace') as f:
                        content = f.read()
                    with open(dst, 'w', encoding='utf-8', newline='') as f:
                        f.write(content)
                    os.remove(bak)
                    n += 1
                except OSError as e:
                    print(f'  RESTORE-FAIL {dst}: {e}')
        print(f'restored: {n}')
        return 0

    if a.apply and game_running() and not a.force:
        print('REFUSED: ffxiv_dx11.exe is running. Close the game first')
        print('(file locks + a 600-file recompile storm), then re-run --apply.')
        return 3
    if a.apply and a.force and game_running():
        print('WARNING: game is running; live patch only safe for single files.')

    from collections import Counter
    cats: Counter = Counter()
    manuals = []
    generatable = []
    n_applied = 0
    total = 0
    for full in iter_fx(root, a.filter):
        total += 1
        try:
            st, detail = process(full, a.apply)
        except Exception as e:  # noqa - one bad file must never stop the run
            st, detail = ('error', str(e)[:120])
        cats[st] += 1
        rel = os.path.relpath(full, root)
        if st == 'APPLIED':
            n_applied += 1
        elif st == 'GENERATABLE':
            generatable.append((rel, detail))
        elif st == 'manual' or st == 'error' or st.startswith('REFUSED'):
            manuals.append((rel, st, detail))
    print(f'files: {total}  applied: {n_applied}')
    print(dict(cats))
    if not a.apply and generatable:
        print('--- GENERATABLE (would patch under --apply) ---')
        for rel, detail in generatable:
            print(f'  {rel:60} {detail[:100]}')
    if manuals:
        print('--- MANUAL (left untouched, with reasons) ---')
        for rel, st, detail in manuals:
            print(f'  {rel:60} {st} {detail[:100]}')
    if a.apply:
        print('Done. Start the game — ReShade recompiles patched shaders once.')
        print('If anything looks wrong: re-run with --restore to revert.')
    else:
        print('Dry run — no files written. Re-run with --apply to patch.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
