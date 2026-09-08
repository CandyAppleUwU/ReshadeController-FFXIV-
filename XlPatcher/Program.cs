// XL Strength patcher — zero-dependency Windows console app (net8.0,
// framework-dependent: every FFXIV PC already has the runtime via XIVLauncher).
//
// Adds `uniform float xlrc_strength` (slider 0-1, default 1.0 = stock look) +
// a `lerp(original, effected, xlrc_strength)` final composite to ReShade
// effect files, so the animator can fade effects instead of toggling them.
// Presence of the name IS the marker (no stock shader ships it).
//
// Double-click: dry-run report, then press A to apply / R to restore.
// CLI: --apply | --restore | --shaders PATH | --filter TEXT | --force
// Run with the GAME CLOSED (--force allows live single-file fixes).
// No redistribution involved: this patches the user's own installed files.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

static class Patcher
{
    const string Decl =
        "// XLRC master strength (added for transitions; default 1 = stock look).\n" +
        "uniform float xlrc_strength <\n" +
        "\tui_type = \"slider\";\n" +
        "\tui_min = 0.0; ui_max = 1.0;\n" +
        "\tui_label = \"XL Strength\";\n" +
        "\tui_tooltip = \"XL master fade: 0 = bypass, 1 = full effect. For animated transitions.\";\n" +
        "> = 1.0;\n";

    static readonly HashSet<string> SkipFiles =
        new(StringComparer.OrdinalIgnoreCase) { "KeepUI.fx", "KeepUI_FFXIV.fx", "MartysMods_LAUNCHPAD.fx" };
    static readonly HashSet<string> SkipDebug =
        new(StringComparer.OrdinalIgnoreCase) { "DisplayDepth.fx" };
    // NOTE: native 0-1 bypass masters (Sepia, MagicHDR, Clarity2, ...) used
    // to be skipped so the animator would drive native instead. Dropped:
    // at xlrc_strength = 1.0 the wrap is identity, so the slider is purely
    // additive and existing presets (XL unticked = 1.0) keep their look.

    // Hand-authored recipes for manual-tail files (same semantics as the
    // .py OVERRIDES). match = wrap returns whose expression contains any
    // listed substring; last = wrap the final's last return (earlier ones
    // are debug/guards). The recipe author verifies the tap reads the true
    // input, which waives the backbuffer-token gate.
    static readonly Dictionary<string, (string[] match, bool last, string tap)> Overrides =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["AmbientLight.fx"] = (new[] { "baseSampleMix" }, false, null),
            ["MagicHDR.fx"] = (null, true, "Color"),
            ["Clarity2.fx"] = (null, true, null),
        };

    // ---------------- shared scanning infra ----------------

    static string StripAll(string t)
    {
        // Analysis space: line comments blanked first so stray /* inside
        // them can't unbalance block stripping, then blocks, then the
        // branch-selection directive family.
        string masked = MaskRegions(t, LineSpans(t));
        if (Regex.Matches(masked, @"/\*").Count == Regex.Matches(masked, @"\*/").Count)
            masked = Regex.Replace(masked, @"/\*.*?\*/", "", RegexOptions.Singleline);
        masked = Regex.Replace(masked, @"^\s*#(if|ifdef|ifndef|elif|else|endif|error|warning|pragma|line|undef)\b.*$", "",
            RegexOptions.Multiline);
        return masked;
    }

    static string BraceBody(string t, int i)
    {
        int depth = 0, p = i; bool ins = false;
        while (p < t.Length)
        {
            char ch = t[p];
            if (ins)
            {
                if (ch == '\\') p++;
                else if (ch == '"') ins = false;
            }
            else if (ch == '"') ins = true;
            else if (ch == '{') depth++;
            else if (ch == '}')
            {
                depth--;
                if (depth == 0) return t.Substring(i, p - i + 1);
            }
            p++;
        }
        return null;
    }

    static List<(int, int)> LineSpans(string raw)
    {
        var spans = new List<(int, int)>();
        int off = 0;
        foreach (var line in raw.Split('\n'))
        {
            bool ins = false; int i = 0;
            while (i + 1 < line.Length)
            {
                char ch = line[i];
                if (ins)
                {
                    if (ch == '\\') i++;
                    else if (ch == '"') ins = false;
                }
                else if (ch == '"') ins = true;
                else if (ch == '/' && line[i + 1] == '/') { spans.Add((off + i, off + line.Length)); break; }
                i++;
            }
            off += line.Length + 1;
        }
        return spans;
    }

    static string NormWs(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    static bool InPreprocRegion(string raw, int pos)
    {
        // True inside an #if...#endif region (naive nesting scan; branch
        // selection is NOT evaluated, so overrides refuse conditional code).
        int depth = 0;
        foreach (var line in raw.Substring(0, pos).Split('\n'))
        {
            string s = line.Trim();
            if (Regex.IsMatch(s, @"^#\s*(if|ifdef|ifndef)\b")) depth++;
            else if (Regex.IsMatch(s, @"^#\s*endif\b")) depth = Math.Max(0, depth - 1);
        }
        return depth > 0;
    }

    static string MaskRegions(string raw, List<(int, int)> spans)
    {
        var chars = raw.ToCharArray();
        foreach (var s in spans)
            for (int i = s.Item1; i < s.Item2 && i < chars.Length; i++)
                if (chars[i] != '\n') chars[i] = ' ';
        return new string(chars);
    }

    static List<(int, int)> CommentSpans(string raw)
    {
        // Line comments FIRST: stray /* inside // lines (BSD "//*"
        // license rows) must not count toward block balance, or comment
        // text parses as code (ColorfulPoster class).
        var line = LineSpans(raw);
        string masked = MaskRegions(raw, line);
        var spans = new List<(int, int)>(line);
        if (Regex.Matches(masked, @"/\*").Count == Regex.Matches(masked, @"\*/").Count)
            foreach (Match m in Regex.Matches(masked, @"/\*.*?\*/", RegexOptions.Singleline))
                spans.Add((m.Index, m.Index + m.Length));
        return spans;
    }

    static bool InSpan(List<(int, int)> spans, int idx)
    {
        foreach (var s in spans) if (s.Item1 <= idx && idx < s.Item2) return true;
        return false;
    }

    static int? DeclPos(string raw)
    {
        int pos = 0;
        if (raw.Length > 0 && raw[0] == '\ufeff') pos = 1;
        int n = raw.Length;
        while (pos < n)
        {
            var m0 = Regex.Match(raw.Substring(pos), @"\A[ \t]*\r?\n");
            if (m0.Success) { pos += m0.Length; continue; }
            if (pos + 1 < n && raw[pos] == '/' && raw[pos + 1] == '/')
            {
                int e = raw.IndexOf('\n', pos);
                pos = e < 0 ? n : e + 1;
                continue;
            }
            if (pos + 1 < n && raw[pos] == '/' && raw[pos + 1] == '*')
            {
                int e = raw.IndexOf("*/", pos + 2, StringComparison.Ordinal);
                if (e < 0) return null;
                pos = e + 2;
                continue;
            }
            var dm = Regex.Match(raw.Substring(pos), @"\A#\s*(include|define|pragma)\b[^\n]*\n?");
            if (dm.Success)
            {
                pos += dm.Length;
                while (pos >= 2 && raw[pos - 2] == '\\' && raw[pos - 1] == '\n')
                {
                    int e = raw.IndexOf('\n', pos);
                    pos = e < 0 ? n : e + 1;
                }
                continue;
            }
            break;
        }
        string prefix = Regex.Replace(raw.Substring(0, pos), @"/\*.*?\*/", "", RegexOptions.Singleline);
        if (Regex.IsMatch(prefix, @"\b(uniform|technique|namespace|texture|sampler|float[234]?|void|static|struct)\b"))
            return null;
        return pos;
    }

    static bool CheckBalance(string raw, string output)
    {
        (int, int, int, int) Counts(string x)
        {
            x = StripAll(x);
            return (x.Count(c => c == '{'), x.Count(c => c == '}'), x.Count(c => c == '('), x.Count(c => c == ')'));
        }
        var a = Counts(raw); var b = Counts(output);
        return a.Item1 == b.Item1 && a.Item2 == b.Item2 && (b.Item3 - a.Item3) == (b.Item4 - a.Item4);
    }

    // (techs, finals) or (null, reason)
    static (List<(string name, List<(string ps, bool rt)> passes)>, Dictionary<string, List<string>>, string) CollectFinals(string t)
    {
        var techs = new List<(string name, List<(string ps, bool rt)> passes)>();
        foreach (Match tm in Regex.Matches(t, @"^\s*technique\s+(\w+)", RegexOptions.Multiline))
        {
            int bi = t.IndexOf('{', tm.Index + tm.Length);
            if (bi < 0) continue;
            string body = BraceBody(t, bi);
            if (body == null) continue;
            var passes = new List<(string, bool)>();
            foreach (Match pm in Regex.Matches(body, @"\bpass\b[^{]*\{"))
            {
                int pbi = body.IndexOf('{', pm.Index);
                string pbody = BraceBody(body, pbi);
                if (pbody == null) continue;
                var psm = Regex.Match(pbody, @"PixelShader\s*=\s*([\w:]+)");
                bool rt = Regex.IsMatch(pbody, @"RenderTarget\s*=");
                passes.Add((psm.Success ? psm.Groups[1].Value.Split(':').Last() : null, rt));
            }
            techs.Add((tm.Groups[1].Value, passes));
        }
        if (techs.Count == 0) return (null, null, "no-techniques");
        var finals = new Dictionary<string, List<string>>();
        foreach (var tech in techs)
        {
            string fout = null;
            for (int i = tech.passes.Count - 1; i >= 0; i--)
                if (!tech.passes[i].rt && tech.passes[i].ps != null) { fout = tech.passes[i].ps; break; }
            if (fout != null)
            {
                if (!finals.TryGetValue(fout, out var l)) finals[fout] = l = new List<string>();
                l.Add(tech.name);
            }
        }
        if (finals.Count == 0) return (null, null, "no-bb-final-any-tech");
        foreach (var fout in finals.Keys)
            foreach (var tech in techs)
                foreach (var p in tech.passes)
                    if (p.ps == fout && p.rt) return (null, null, fout + "-shared-midchain");
        return (techs, finals, "");
    }

    static bool BackbufferOk(string t, string raw) =>
        t.Contains("ReShade::BackBuffer") || Regex.IsMatch(raw, "^\\s*#include\\s+\"([^\"]*/)?ReShade\\.fxh\"", RegexOptions.Multiline);

    // ---------------- value-style ----------------

    static (List<(int s, int e, string rep, string tag)>, string) TryValue(
        string raw, string t, List<(int, int)> spans, Dictionary<string, List<string>> finals,
        (string[] match, bool last, string tap)? ov = null)
    {
        var edits = new List<(int, int, string, string)>();
        foreach (var fout in finals.Keys)
        {
            var m = Regex.Match(t, @"\b(float[34])\s+" + Regex.Escape(fout) + @"\s*\(([^)]*)\)[^{]*\{");
            if (!m.Success) return (null, fout + "-fn-parse");
            string ret = m.Groups[1].Value, pars = m.Groups[2].Value;
            var uvm = Regex.Match(pars, @"float2\s+(\w+)");
            if (!uvm.Success) return (null, fout + "-no-uv-param");
            string uv = uvm.Groups[1].Value;
            int bi = t.IndexOf('{', m.Index);
            string body = BraceBody(t, bi);
            if (body == null) return (null, fout + "-no-body");
            if (Regex.IsMatch(body, @"toOutputColorspace|getBackBufferLinear|toLinearColorspace"))
                return (null, fout + "-colorspace");
            var rets = Regex.Matches(body, @"\breturn\b([^;]*);");
            List<(int j, string r)> targets;
            bool lastMode = false;
            if (ov.HasValue && ov.Value.match != null)
            {
                targets = new List<(int, string)>();
                for (int jj = 0; jj < rets.Count; jj++)
                    if (ov.Value.match.Any(s => rets[jj].Groups[1].Value.Contains(s)))
                        targets.Add((jj, rets[jj].Groups[1].Value));
                if (targets.Count == 0) return (null, fout + "-override-nomatch");
                if (Regex.IsMatch(body, @"\bxlrc_" + Regex.Escape(fout) + @"(_o\d+)?_orig\b"))
                    return (null, fout + "-name-clash");
            }
            else if (ov.HasValue && ov.Value.last)
            {
                if (rets.Count == 0) return (null, fout + "-override-nomatch");
                targets = new List<(int, string)> { (rets.Count - 1, rets[rets.Count - 1].Groups[1].Value) };
                if (Regex.IsMatch(body, @"\bxlrc_" + Regex.Escape(fout) + @"(_o\d+)?_orig\b"))
                    return (null, fout + "-name-clash");
                lastMode = true;
            }
            else
            {
                if (rets.Count != 1) return (null, fout + "-returns-" + rets.Count);
                targets = new List<(int, string)> { (0, rets[0].Groups[1].Value) };
            }
            // Locate the exact returns via masked RAW (same offsets): a
            // comment-text "return" would otherwise swallow the real one's
            // semicolon and shadow it (ColorfulPoster class).
            string maskedRaw = MaskRegions(raw, spans);
            foreach (var (j, r) in targets)
            {
                string expr = r.Trim();
                if (expr.Length == 0) return (null, fout + "-empty-return");
                string suf = ov.HasValue ? "_o" + j : "";
                var cands = new List<Match>();
                foreach (Match mm in Regex.Matches(maskedRaw, @"\breturn\b([^;]*);"))
                    cands.Add(mm);
                var same = cands.Where(mm => NormWs(mm.Groups[1].Value) == NormWs(expr)).ToList();
                // Last-mode: earliest candidates are debug/guards; the
                // composite exit is the last one standing. It must not sit
                // in conditional code (truncated-body class: Clarity2).
                var match = lastMode ? same.Skip(Math.Max(0, same.Count - 1)).ToList() : same;
                if (lastMode && match.Count == 1 && InPreprocRegion(raw, match[0].Index))
                    return (null, fout + "-conditional-return");
                if (match.Count != 1) return (null, fout + "-raw-return-ambiguous-" + match.Count);
                var rstmt = match[0];
                if (ov.HasValue && !lastMode && InPreprocRegion(raw, rstmt.Index))
                    continue; // conditional target: leave raw, keep others
                int lineStart = raw.LastIndexOf('\n', Math.Max(0, rstmt.Index - 1)) + 1;
                string prevCode = null;
                foreach (var pl in raw.Substring(0, lineStart).Split('\n').Reverse())
                {
                    string s2 = pl.Trim();
                    if (s2 == "" || s2.StartsWith("//")) continue;
                    prevCode = s2; break;
                }
                if (prevCode != null && Regex.IsMatch(prevCode, @"\)\s*$") && !prevCode.EndsWith("{") && !prevCode.EndsWith(";"))
                    return (null, fout + "-braceless-if");
                string indent = Regex.Match(raw.Substring(lineStart), @"\A[ \t]*").Value;
                string tapSrc = (ov.HasValue && ov.Value.tap != null) ? ov.Value.tap : "ReShade::BackBuffer";
                string origline = indent + "float4 xlrc_" + fout + suf + "_orig = tex2D(" + tapSrc + ", " + uv + ");";
                string newret = ret == "float4"
                    ? indent + "return lerp(xlrc_" + fout + suf + "_orig, (" + expr + "), xlrc_strength);"
                    : indent + "return lerp(xlrc_" + fout + suf + "_orig.rgb, (" + expr + "), xlrc_strength);";
                edits.Add((rstmt.Index, rstmt.Index + rstmt.Length, origline + "\n" + newret, fout + "-return" + suf));
            }
        }
        if (ov.HasValue && edits.Count == 0) return (null, "override-nomatch");
        return (edits, "");
    }

    // ---------------- void-style (offset-mapped strip) ----------------

    static (string, List<int>) StripMap(string raw)
    {
        int n = raw.Length;
        var line = LineSpans(raw);
        string masked = MaskRegions(raw, line);
        var spans = new List<(int, int)>(line);
        if (Regex.Matches(masked, @"/\*").Count == Regex.Matches(masked, @"\*/").Count)
            foreach (Match m in Regex.Matches(masked, @"/\*.*?\*/", RegexOptions.Singleline))
                spans.Add((m.Index, m.Index + m.Length));
        var dire = new List<(int, int)>();
        foreach (Match m in Regex.Matches(masked, @"^[ \t]*#(if|ifdef|ifndef|elif|else|endif|error|warning|pragma|line|undef)\b[^\n]*", RegexOptions.Multiline))
            dire.Add((m.Index, m.Index + m.Length));
        var skip = spans.Concat(dire).OrderBy(s => s.Item1).ThenBy(s => s.Item2).ToList();
        var outSb = new StringBuilder(); var mp = new List<int>();
        int ii = 0, k = 0;
        while (ii < n)
        {
            if (k < skip.Count && skip[k].Item1 <= ii && ii < skip[k].Item2)
            {
                ii = skip[k].Item2;
                if (k + 1 < skip.Count && skip[k + 1].Item1 < ii) { k++; continue; }
                k++; continue;
            }
            if (k < skip.Count && ii >= skip[k].Item2) { k++; continue; }
            outSb.Append(raw[ii]); mp.Add(ii); ii++;
        }
        return (outSb.ToString(), mp);
    }

    static (List<(string fout, string otype, string oname, string uv, int bi, string body)>, string) TryVoid(
        string raw, string t, Dictionary<string, List<string>> finals)
    {
        var edits = new List<(string, string, string, string, int, string)>();
        foreach (var fout in finals.Keys.OrderBy(x => x))
        {
            var m = Regex.Match(t, @"\bvoid\s+(?:\w+::)?" + Regex.Escape(fout) + @"\s*\(([^)]*)\)");
            if (!m.Success) return (null, fout + "-fn-parse");
            string pars = m.Groups[1].Value;
            var outs = Regex.Matches(pars, @"\bout\s+(float[34]?)\s+(\w+)");
            if (outs.Count != 1 || (outs[0].Groups[1].Value != "float4" && outs[0].Groups[1].Value != "float3"))
                return (null, fout + "-out-sig");
            string otype = outs[0].Groups[1].Value, oname = outs[0].Groups[2].Value;
            string uv;
            var um = Regex.Match(pars, @"float2\s+(\w+)\s*:\s*TEXCOORD");
            if (um.Success) uv = um.Groups[1].Value;
            else
            {
                var all = Regex.Matches(pars, @"(?<![\w:])float2\s+(\w+)");
                if (all.Count != 1) return (null, fout + "-no-uv-param");
                uv = all[0].Groups[1].Value;
            }
            if (uv == oname) return (null, fout + "-uv-is-outparam");
            int bi = t.IndexOf('{', m.Index);
            string body = BraceBody(t, bi);
            if (body == null) return (null, fout + "-no-body");
            if (Regex.IsMatch(body, @"toOutputColorspace|getBackBufferLinear|toLinearColorspace"))
                return (null, fout + "-colorspace");
            if (Regex.IsMatch(body, @"\breturn\b")) return (null, fout + "-has-returns");
            string toplevel = body;
            while (true)
            {
                string nub = Regex.Replace(toplevel, @"\{[^{}]*\}", "");
                if (nub == toplevel) break;
                toplevel = nub;
            }
            if (!Regex.IsMatch(toplevel, @"\b" + Regex.Escape(oname) + @"\s*=(?!=)"))
                return (null, fout + "-out-cond-write");
            if (Regex.IsMatch(body, @"\bxlrc_" + Regex.Escape(fout) + @"_orig\b"))
                return (null, fout + "-name-clash");
            edits.Add((fout, otype, oname, uv, bi, body));
        }
        return (edits, "");
    }

    static (List<(int s, int e, string rep, string tag)>, string) ApplyVoidEdits(
        string raw, List<int> mp, List<(string fout, string otype, string oname, string uv, int bi, string body)> vedits)
    {
        var out_ = new List<(int, int, string, string)>();
        foreach (var (fout, otype, oname, uv, bi, body) in vedits)
        {
            int stripend = bi + body.Length - 1;
            if (stripend >= mp.Count) return (null, fout + "-map-range");
            int rawbrace = mp[stripend];
            int lineStart = raw.LastIndexOf('\n', Math.Max(0, rawbrace - 1)) + 1;
            string bprefix = Regex.Match(raw.Substring(lineStart), @"\A[ \t]*").Value;
            string step = raw.Contains("\n\t") ? "\t" : "    ";
            string indent = bprefix + step;
            string fade = otype == "float4"
                ? indent + oname + " = lerp(xlrc_" + fout + "_orig, " + oname + ", xlrc_strength);"
                : indent + oname + " = lerp(xlrc_" + fout + "_orig.rgb, " + oname + ", xlrc_strength);";
            string origline = indent + "float4 xlrc_" + fout + "_orig = tex2D(ReShade::BackBuffer, " + uv + ");";
            out_.Add((rawbrace, rawbrace + 1,
                origline + "\n" + fade + "\n" + raw.Substring(lineStart, bprefix.Length) + "}", fout + "-tail"));
        }
        return (out_, "");
    }

    // ---------------- per-file driver ----------------

    static (string, string) Process(string path, bool apply)
    {
        string fn = Path.GetFileName(path);
        if (SkipFiles.Contains(fn)) return ("skip-stock-infra", "");
        if (SkipDebug.Contains(fn)) return ("skip-debug-view", "");
        string raw = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n");
        if (raw.Contains("xlrc_strength")) return ("skip-has-xlrc", "");
        if (raw.Contains("xlrc_")) return ("manual", "has-other-xlrc");
        var spans = CommentSpans(raw);
        string t = StripAll(raw);
        var got = CollectFinals(t);
        if (got.Item1 == null) return ("manual", got.Item3);
        var finals = got.Item2;
        (string[] match, bool last, string tap)? ov = null;
        if (Overrides.TryGetValue(fn, out var found)) ov = found;
        // Overrides waive the token gate: the recipe author verified the
        // tap reads the true input (see Overrides).
        if (!ov.HasValue && !BackbufferOk(t, raw)) return ("manual", "no-backbuffer-token");

        var vt = TryValue(raw, t, spans, finals, ov);
        if (vt.Item1 != null) return Finish(path, raw, vt.Item1, "value", apply, finals, spans);

        string tv; List<int> mp;
        (tv, mp) = StripMap(raw);
        var got2 = CollectFinals(tv);
        if (got2.Item1 == null) return ("manual", got2.Item3 + "|value:" + vt.Item2);
        var finals2 = got2.Item2;
        if (!BackbufferOk(tv, raw)) return ("manual", "no-backbuffer-token|value:" + vt.Item2);
        var wt = TryVoid(raw, tv, finals2);
        if (wt.Item1 == null) return ("manual", wt.Item2 + "|value:" + vt.Item2);
        var rt = ApplyVoidEdits(raw, mp, wt.Item1);
        if (rt.Item1 == null) return ("manual", rt.Item2 + "|value:" + vt.Item2);
        return Finish(path, raw, rt.Item1, "void", apply, finals2, spans);
    }

    static (string, string) Finish(string path, string raw,
        List<(int s, int e, string rep, string tag)> edits, string flavor, bool apply,
        Dictionary<string, List<string>> finals, List<(int, int)> spans)
    {
        int? dpos = DeclPos(raw);
        if (dpos == null) return ("manual", "no-clean-decl-site|" + flavor);
        foreach (var fout in finals.Keys)
        {
            var fdefs = new List<Match>();
            foreach (Match mm in Regex.Matches(raw, @"\b(?:float[34]|void)\s+(?:\w+::)?" + Regex.Escape(fout) + @"\s*\("))
                if (!InSpan(spans, mm.Index)) fdefs.Add(mm);
            if (fdefs.Count == 0 || fdefs.Min(mm => mm.Index) < dpos.Value)
                return ("manual", fout + "-decl-order|" + flavor);
        }
        if (apply)
        {
            string bak = path + ".xlrcbak";
            if (!File.Exists(bak)) File.WriteAllText(bak, raw, new UTF8Encoding(false));
            string output = raw;
            foreach (var (s, e, rep, tag) in edits.OrderByDescending(x => x.s))
                output = output.Substring(0, s) + rep + output.Substring(e);
            output = output.Substring(0, dpos.Value) + "\n" + Decl + output.Substring(dpos.Value);
            if (!CheckBalance(raw, output)) return ("REFUSED-unbalanced", flavor);
            File.WriteAllText(path, output, new UTF8Encoding(false));
            return ("APPLIED", flavor + ":" + string.Join("; ", edits.Select(d => d.tag)));
        }
        return ("GENERATABLE", flavor + ":" + string.Join("; ", edits.Select(d => d.tag)));
    }

    // ---------------- driver ----------------

    static string FindShadersDir(string cli)
    {
        if (!string.IsNullOrEmpty(cli)) return cli;
        string here = AppContext.BaseDirectory;
        string cand = Path.Combine(here, "reshade-shaders", "Shaders");
        if (Directory.Exists(cand)) return cand;
        cand = Path.Combine(Directory.GetCurrentDirectory(), "reshade-shaders", "Shaders");
        if (Directory.Exists(cand)) return cand;
        return "";
    }

    static bool GameRunning()
    {
        try { return System.Diagnostics.Process.GetProcessesByName("ffxiv_dx11").Length > 0; }
        catch { return false; }
    }

    static int Main(string[] argv)
    {
        if (argv.Length == 2 && argv[0] == "--probe")
        {
            string praw = File.ReadAllText(argv[1], Encoding.UTF8).Replace("\r\n", "\n");
            Console.WriteLine("declpos=" + (DeclPos(praw)?.ToString() ?? "null"));
            return 0;
        }
        bool apply = argv.Contains("--apply");
        bool restore = argv.Contains("--restore");
        bool force = argv.Contains("--force");
        string shaders = "", filter = "";
        for (int i = 0; i < argv.Length; i++)
        {
            if (argv[i] == "--shaders" && i + 1 < argv.Length) shaders = argv[++i];
            else if (argv[i] == "--filter" && i + 1 < argv.Length) filter = argv[++i];
        }
        bool interactive = argv.Length == 0;
        string root = FindShadersDir(shaders);
        if (root == "" || !Directory.Exists(root))
        {
            Console.WriteLine("Shaders dir not found. Pass --shaders PATH (the reshade-shaders\\Shaders folder),");
            Console.WriteLine("or drop this program next to reshade-shaders\\ (e.g. in the game dir).");
            if (interactive) Console.ReadKey();
            return 2;
        }
        Console.WriteLine("Shaders: " + root);

        if (restore)
        {
            int n = 0;
            foreach (var bak in Directory.EnumerateFiles(root, "*.xlrcbak", SearchOption.AllDirectories))
            {
                try
                {
                    File.Copy(bak, bak.Substring(0, bak.Length - ".xlrcbak".Length), true);
                    File.Delete(bak);
                    n++;
                }
                catch (Exception e) { Console.WriteLine("  RESTORE-FAIL " + bak + ": " + e.Message); }
            }
            Console.WriteLine("restored: " + n);
            if (interactive) Console.ReadKey();
            return 0;
        }

        var files = Directory.EnumerateFiles(root, "*.fx", SearchOption.AllDirectories)
            .Where(f => filter == "" || f.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(f => f).ToList();

        if (interactive)
        {
            // Double-click: report first, ask after.
            var manuals0 = new List<(string, string, string)>();
            var gen0 = new List<(string, string)>();
            _ = Run(files, false, manuals0, gen0);
            Console.WriteLine("Dry run — no files written.");
            Console.WriteLine("Press A to patch, R to restore backups, any other key to exit.");
            var k = Console.ReadKey(true).Key;
            if (k == ConsoleKey.A) { apply = true; }
            else if (k == ConsoleKey.R)
            {
                int n = 0;
                foreach (var bak in Directory.EnumerateFiles(root, "*.xlrcbak", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.Copy(bak, bak.Substring(0, bak.Length - ".xlrcbak".Length), true);
                        File.Delete(bak);
                        n++;
                    }
                    catch (Exception e) { Console.WriteLine("  RESTORE-FAIL " + bak + ": " + e.Message); }
                }
                Console.WriteLine("restored: " + n);
                Console.ReadKey();
                return 0;
            }
            else return 0;
        }

        if (apply && GameRunning() && !force)
        {
            Console.WriteLine("REFUSED: ffxiv_dx11.exe is running. Close the game first");
            Console.WriteLine("(file locks + a 600-file recompile storm), then re-run with --apply.");
            if (interactive) Console.ReadKey();
            return 3;
        }
        if (apply && force && GameRunning())
            Console.WriteLine("WARNING: game is running; live patch only safe for single files.");

        var manuals = new List<(string, string, string)>();
        var gen = new List<(string, string)>();
        _ = Run(files, apply, manuals, gen);
        if (apply)
        {
            Console.WriteLine("Done. Start the game — ReShade recompiles patched shaders once.");
            Console.WriteLine("If anything looks wrong: re-run with --restore to revert.");
        }
        else Console.WriteLine("Dry run — no files written. Re-run with --apply to patch.");
        if (interactive) Console.ReadKey();
        return 0;
    }

    // Returns (total, applied, cats, manuals, generatable) via out lists.
    static Dictionary<string, int> Run(List<string> files, bool apply,
        List<(string, string, string)> manuals = null, List<(string, string)> generatable = null)
    {
        var cats = new Dictionary<string, int>();
        int applied = 0, total = 0;
        foreach (var full in files)
        {
            total++;
            string st, detail;
            try { (st, detail) = Process(full, apply); }
            catch (Exception e) { st = "error"; detail = e.Message.Length > 120 ? e.Message.Substring(0, 120) : e.Message; }
            cats[st] = cats.TryGetValue(st, out int c) ? c + 1 : 1;
            string rel = full;
            if (st == "APPLIED") applied++;
            else if (st == "GENERATABLE") generatable?.Add((rel, detail));
            else if (st == "manual" || st == "error" || st.StartsWith("REFUSED")) manuals?.Add((rel, st, detail));
        }
        Console.WriteLine("files: " + total + "  applied: " + applied);
        Console.WriteLine("{ " + string.Join(", ", cats.Select(kv => kv.Key + ": " + kv.Value)) + " }");
        if (!apply && generatable != null && generatable.Count > 0)
        {
            Console.WriteLine("--- GENERATABLE (would patch under --apply) ---");
            foreach (var (rel, detail) in generatable) Console.WriteLine("  " + rel + "  " + detail);
        }
        if (manuals != null && manuals.Count > 0)
        {
            Console.WriteLine("--- MANUAL (left untouched, with reasons) ---");
            int shown = 0;
            foreach (var (rel, st, detail) in manuals)
            {
                if (shown++ >= 60) { Console.WriteLine("  ... (" + (manuals.Count - shown + 1) + " more)"); break; }
                Console.WriteLine("  " + rel + "  " + st + " " + (detail.Length > 100 ? detail.Substring(0, 100) : detail));
            }
        }
        return cats;
    }
}
