// DevTokenMeter - one dashboard for Claude Code, Codex and GitHub activity.
// Target: .NET Framework 4.x (C# 5), compiled with the in-box csc.exe.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace DevTokenMeter
{
    // ---------------------------------------------------------------- models

    class Day
    {
        public string D;
        public long Input, Output, CacheWrite, CacheRead, Think, Total;
        public int Msgs;
        public HashSet<string> Sessions = new HashSet<string>();
    }

    class Sess
    {
        public string Id, Proj, Model;
        public int Msgs;
        public long Total, Output;
        public DateTime First, Last;
    }

    class Bucket
    {
        public string Name;
        public long Total;
        public int Msgs;
    }

    class Agg
    {
        public string Key, Label;
        public bool Available;
        public int Files, Lines;
        public Dictionary<string, Day> Days = new Dictionary<string, Day>();
        public long[] HourTok = new long[24];
        public int[] HourMsgs = new int[24];
        public Dictionary<string, Bucket> Models = new Dictionary<string, Bucket>();
        public Dictionary<string, Bucket> Projects = new Dictionary<string, Bucket>();
        public Dictionary<string, Sess> Sessions = new Dictionary<string, Sess>();
        public long Input, Output, CacheWrite, CacheRead, Think, Total;
        public int Msgs;

        public Day DayFor(string key)
        {
            Day d;
            if (!Days.TryGetValue(key, out d)) { d = new Day(); d.D = key; Days[key] = d; }
            return d;
        }

        public void Bump(Dictionary<string, Bucket> map, string name, long tok)
        {
            if (string.IsNullOrEmpty(name)) return;
            Bucket b;
            if (!map.TryGetValue(name, out b)) { b = new Bucket(); b.Name = name; map[name] = b; }
            b.Total += tok; b.Msgs++;
        }

        public void Record(DateTime local, string sessionId, string project, string model,
                           long input, long output, long cacheWrite, long cacheRead, long think)
        {
            long total = input + output + cacheWrite + cacheRead;
            if (total <= 0) return;

            var d = DayFor(local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            d.Input += input; d.Output += output; d.CacheWrite += cacheWrite;
            d.CacheRead += cacheRead; d.Think += think; d.Total += total; d.Msgs++;
            if (!string.IsNullOrEmpty(sessionId)) d.Sessions.Add(sessionId);

            HourTok[local.Hour] += total; HourMsgs[local.Hour]++;
            Bump(Models, model, total);
            Bump(Projects, project, total);

            if (!string.IsNullOrEmpty(sessionId))
            {
                Sess s;
                if (!Sessions.TryGetValue(sessionId, out s))
                {
                    s = new Sess();
                    s.Id = sessionId; s.Proj = project; s.Model = model;
                    s.First = local; s.Last = local;
                    Sessions[sessionId] = s;
                }
                s.Msgs++; s.Total += total; s.Output += output;
                if (local < s.First) s.First = local;
                if (local > s.Last) s.Last = local;
                if (string.IsNullOrEmpty(s.Proj)) s.Proj = project;
                if (string.IsNullOrEmpty(s.Model)) s.Model = model;
            }

            Input += input; Output += output; CacheWrite += cacheWrite;
            CacheRead += cacheRead; Think += think; Total += total; Msgs++;
        }
    }

    class GhDay { public string D; public int Count; public int Level; }

    class GitHubData
    {
        public bool Available;
        public string User, Error;
        public Dictionary<string, GhDay> Days = new Dictionary<string, GhDay>();
        public int Total;
        public DateTime Fetched;
    }

    // ---------------------------------------------------------------- scanners

    static class Scan
    {
        static readonly Regex RxTs = new Regex("\"timestamp\":\"([^\"]+)\"", RegexOptions.Compiled);
        static readonly Regex RxModel = new Regex("\"model\":\"([^\"]+)\"", RegexOptions.Compiled);
        static readonly Regex RxCwd = new Regex("\"cwd\":\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);
        static readonly Regex RxUuid = new Regex("\"uuid\":\"([^\"]+)\"", RegexOptions.Compiled);
        static readonly Regex RxIn = new Regex("(?<![a-z_])\"input_tokens\":(\\d+)", RegexOptions.Compiled);
        static readonly Regex RxOut = new Regex("(?<![a-z_])\"output_tokens\":(\\d+)", RegexOptions.Compiled);
        static readonly Regex RxCw = new Regex("\"cache_creation_input_tokens\":(\\d+)", RegexOptions.Compiled);
        static readonly Regex RxCr = new Regex("\"cache_read_input_tokens\":(\\d+)", RegexOptions.Compiled);
        static readonly Regex RxThink = new Regex("\"thinking_tokens\":(\\d+)", RegexOptions.Compiled);

        // codex
        static readonly Regex RxCxUsage = new Regex(
            "\"usage\":\\{\"input_tokens\":(\\d+),\"cached_input_tokens\":(\\d+),\"cache_write_input_tokens\":(\\d+),\"output_tokens\":(\\d+),\"reasoning_output_tokens\":(\\d+),\"total_tokens\":(\\d+)\\}",
            RegexOptions.Compiled);
        static readonly Regex RxCxResp = new Regex("\"response_id\":\"([^\"]+)\"", RegexOptions.Compiled);
        static readonly Regex RxCxThread = new Regex("\"thread_id\":\"([^\"]+)\"", RegexOptions.Compiled);
        // older codex builds only emit event_msg/token_count with a cumulative total
        static readonly Regex RxCxCumulative = new Regex(
            "\"total_token_usage\":\\{\"input_tokens\":(\\d+),\"cached_input_tokens\":(\\d+),\"cache_write_input_tokens\":(\\d+),\"output_tokens\":(\\d+),\"reasoning_output_tokens\":(\\d+),\"total_tokens\":(\\d+)\\}",
            RegexOptions.Compiled);

        static long G(Match m, int g) { return m.Success ? long.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture) : 0L; }

        static bool TryTime(string iso, out DateTime local)
        {
            local = DateTime.MinValue;
            DateTimeOffset dto;
            if (!DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal, out dto)) return false;
            local = dto.ToLocalTime().DateTime;
            return true;
        }

        static string Leaf(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var p = path.Replace("\\\\", "\\").TrimEnd('\\', '/');
            int i = p.LastIndexOfAny(new[] { '\\', '/' });
            return i >= 0 && i < p.Length - 1 ? p.Substring(i + 1) : p;
        }

        static IEnumerable<string> Files(string root)
        {
            if (!Directory.Exists(root)) yield break;
            IEnumerable<string> all;
            try { all = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories); }
            catch { yield break; }
            foreach (var f in all) yield return f;
        }

        // -------- Claude Code: ~/.claude/projects/**/<session>.jsonl
        public static Agg Claude(DateTime cutoff)
        {
            var a = new Agg(); a.Key = "claude"; a.Label = "Claude Code";
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var roots = new[]
            {
                Path.Combine(home, ".claude\\projects"),
                Path.Combine(appdata, "Claude\\claude-code-sessions"),
                Path.Combine(appdata, "Claude\\local-agent-mode-sessions")
            };
            var seen = new HashSet<string>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in roots) foreach (var f in Files(r)) paths.Add(f);
            a.Available = paths.Count > 0;

            foreach (var file in paths)
            {
                a.Files++;
                string sid = Path.GetFileNameWithoutExtension(file);
                string proj = null;
                IEnumerable<string> lines;
                try { lines = File.ReadLines(file); } catch { continue; }

                foreach (var line in lines)
                {
                    a.Lines++;
                    if (line.Length < 60) continue;
                    if (line.IndexOf("\"usage\"", StringComparison.Ordinal) < 0) continue;
                    if (line.IndexOf("\"type\":\"assistant\"", StringComparison.Ordinal) < 0) continue;

                    var mu = RxUuid.Match(line);
                    if (mu.Success && !seen.Add("c:" + mu.Groups[1].Value)) continue;

                    var mt = RxTs.Match(line);
                    DateTime when;
                    if (!mt.Success || !TryTime(mt.Groups[1].Value, out when)) continue;
                    if (when < cutoff) continue;

                    if (proj == null) { var mc = RxCwd.Match(line); if (mc.Success) proj = Leaf(mc.Groups[1].Value); }
                    var mm = RxModel.Match(line);

                    a.Record(when, sid, proj ?? "(unknown)",
                             mm.Success ? mm.Groups[1].Value : "unknown",
                             G(RxIn.Match(line), 1), G(RxOut.Match(line), 1),
                             G(RxCw.Match(line), 1), G(RxCr.Match(line), 1),
                             G(RxThink.Match(line), 1));
                }
            }
            return a;
        }

        // -------- Codex: ~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl
        public static Agg Codex(DateTime cutoff)
        {
            var a = new Agg(); a.Key = "codex"; a.Label = "Codex";
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var roots = new[]
            {
                Path.Combine(home, ".codex\\sessions"),
                Path.Combine(home, ".codex\\archived_sessions")
            };
            var seen = new HashSet<string>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in roots) foreach (var f in Files(r)) paths.Add(f);
            a.Available = paths.Count > 0;

            foreach (var file in paths)
            {
                a.Files++;
                string sid = null, proj = null, model = null;
                int exactRecords = 0;
                var fallback = new List<object[]>();   // when, model, fresh, out, cw, cached, reason
                var prev = new long[5];                // cumulative: in, cached, cw, out, reason
                bool havePrev = false;
                IEnumerable<string> lines;
                try { lines = File.ReadLines(file); } catch { continue; }

                foreach (var line in lines)
                {
                    a.Lines++;
                    if (line.Length < 40) continue;

                    if (proj == null && line.IndexOf("\"type\":\"session_meta\"", StringComparison.Ordinal) >= 0)
                    {
                        var mc = RxCwd.Match(line); if (mc.Success) proj = Leaf(mc.Groups[1].Value);
                        var ms = Regex.Match(line, "\"session_id\":\"([^\"]+)\""); if (ms.Success) sid = ms.Groups[1].Value;
                        var mo = Regex.Match(line, "\"provenance\":\\{\"type\":\"model\",\"model\":\"([^\"]+)\"");
                        if (mo.Success) model = mo.Groups[1].Value;
                        continue;
                    }
                    if (line.IndexOf("\"type\":\"turn_context\"", StringComparison.Ordinal) >= 0)
                    {
                        var mo = RxModel.Match(line); if (mo.Success) model = mo.Groups[1].Value;
                        if (proj == null) { var mc = RxCwd.Match(line); if (mc.Success) proj = Leaf(mc.Groups[1].Value); }
                        continue;
                    }
                    if (line.IndexOf("\"type\":\"token_usage_record\"", StringComparison.Ordinal) < 0)
                    {
                        // legacy path: derive per-turn deltas from the cumulative total
                        if (line.IndexOf("\"type\":\"token_count\"", StringComparison.Ordinal) < 0) continue;
                        var mc2 = RxCxCumulative.Match(line);
                        if (!mc2.Success) continue;
                        var mt2 = RxTs.Match(line);
                        DateTime w2;
                        if (!mt2.Success || !TryTime(mt2.Groups[1].Value, out w2)) continue;

                        var cur = new long[5];
                        for (int gi = 0; gi < 5; gi++) cur[gi] = long.Parse(mc2.Groups[gi + 1].Value);
                        long curTotal = long.Parse(mc2.Groups[6].Value);
                        long prevTotal = havePrev ? prev[0] + prev[3] : 0;

                        var dl = new long[5];
                        bool reset = !havePrev || curTotal < prevTotal;
                        for (int gi = 0; gi < 5; gi++)
                            dl[gi] = reset ? cur[gi] : Math.Max(0, cur[gi] - prev[gi]);
                        prev = cur; havePrev = true;

                        if (dl[0] + dl[3] <= 0) continue;
                        fallback.Add(new object[] { w2, model, Math.Max(0, dl[0] - dl[1]), dl[3], dl[2], dl[1], dl[4] });
                        continue;
                    }

                    var mUse = RxCxUsage.Match(line);
                    if (!mUse.Success) continue;

                    var mr = RxCxResp.Match(line);
                    if (mr.Success && !seen.Add("x:" + mr.Groups[1].Value)) continue;

                    var mt = RxTs.Match(line);
                    DateTime when;
                    if (!mt.Success || !TryTime(mt.Groups[1].Value, out when)) continue;
                    if (when < cutoff) continue;

                    long inTot = long.Parse(mUse.Groups[1].Value);
                    long cached = long.Parse(mUse.Groups[2].Value);
                    long cw = long.Parse(mUse.Groups[3].Value);
                    long outTok = long.Parse(mUse.Groups[4].Value);
                    long reason = long.Parse(mUse.Groups[5].Value);
                    long fresh = Math.Max(0, inTot - cached);

                    if (sid == null)
                    {
                        var mth = RxCxThread.Match(line);
                        sid = mth.Success ? mth.Groups[1].Value : Path.GetFileNameWithoutExtension(file);
                    }

                    a.Record(when, sid, proj ?? "(unknown)", model ?? "unknown",
                             fresh, outTok, cw, cached, reason);
                    exactRecords++;
                }

                // only trust the legacy stream when the file has no exact records at all
                if (exactRecords == 0 && fallback.Count > 0)
                {
                    if (sid == null) sid = Path.GetFileNameWithoutExtension(file);
                    foreach (var r in fallback)
                    {
                        var when = (DateTime)r[0];
                        if (when < cutoff) continue;
                        a.Record(when, sid, proj ?? "(unknown)",
                                 (string)(r[1] ?? model) ?? "unknown",
                                 (long)r[2], (long)r[3], (long)r[4], (long)r[5], (long)r[6]);
                    }
                }
            }
            return a;
        }

        // -------- GitHub contribution calendar (public data, no token needed)
        // attribute order on the <td> is not stable, so grab the whole tag then pull each attribute
        static readonly Regex RxTdTag = new Regex(
            "<td\\b[^>]*data-date=\"\\d{4}-\\d{2}-\\d{2}\"[^>]*>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex RxAttrDate = new Regex("data-date=\"(\\d{4}-\\d{2}-\\d{2})\"", RegexOptions.Compiled);
        static readonly Regex RxAttrLevel = new Regex("data-level=\"(\\d+)\"", RegexOptions.Compiled);
        static readonly Regex RxAttrId = new Regex("id=\"(contribution-day-component-[^\"]+)\"", RegexOptions.Compiled);
        static readonly Regex RxTip = new Regex(
            "<tool-tip[^>]*for=\"(contribution-day-component-[^\"]+)\"[^>]*>([^<]*)</tool-tip>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex RxCount = new Regex("^\\s*(No|[\\d,]+)\\s+contribution", RegexOptions.IgnoreCase);

        public static GitHubData GitHub(string user, string cacheFile, bool force)
        {
            var g = new GitHubData();
            g.User = user;
            if (string.IsNullOrWhiteSpace(user))
            {
                g.Error = "No GitHub username set. Run:  DevTokenMeter.exe --github YOUR_USERNAME";
                return g;
            }

            if (!force && File.Exists(cacheFile))
            {
                try
                {
                    var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile);
                    var txt = File.ReadAllText(cacheFile);
                    if (age.TotalHours < 3 && txt.IndexOf("\"user\":\"" + user + "\"", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        foreach (Match m in Regex.Matches(txt, "\"(\\d{4}-\\d{2}-\\d{2})\":\\[(\\d+),(\\d+)\\]"))
                        {
                            var d = new GhDay();
                            d.D = m.Groups[1].Value;
                            d.Count = int.Parse(m.Groups[2].Value);
                            d.Level = int.Parse(m.Groups[3].Value);
                            g.Days[d.D] = d; g.Total += d.Count;
                        }
                        if (g.Days.Count > 0) { g.Available = true; g.Fetched = File.GetLastWriteTime(cacheFile); return g; }
                    }
                }
                catch { }
            }

            string html;
            try
            {
                ServicePointManager.SecurityProtocol =
                    (SecurityProtocolType)3072 | (SecurityProtocolType)768; // Tls12 | Tls11
                using (var wc = new WebClient())
                {
                    wc.Encoding = Encoding.UTF8;
                    wc.Headers.Add("User-Agent", "DevTokenMeter/1.0");
                    wc.Headers.Add("Accept", "text/html");
                    html = wc.DownloadString("https://github.com/users/" +
                        Uri.EscapeDataString(user) + "/contributions");
                }
            }
            catch (Exception ex)
            {
                g.Error = "Could not reach github.com for '" + user + "': " + ex.Message;
                return g;
            }

            var counts = new Dictionary<string, int>();
            foreach (Match m in RxTip.Matches(html))
            {
                var c = RxCount.Match(m.Groups[2].Value);
                if (!c.Success) continue;
                var raw = c.Groups[1].Value;
                counts[m.Groups[1].Value] =
                    raw.Equals("No", StringComparison.OrdinalIgnoreCase) ? 0 : int.Parse(raw.Replace(",", ""));
            }

            foreach (Match m in RxTdTag.Matches(html))
            {
                var tag = m.Value;
                var md = RxAttrDate.Match(tag);
                if (!md.Success) continue;

                var d = new GhDay();
                d.D = md.Groups[1].Value;
                var ml = RxAttrLevel.Match(tag);
                d.Level = ml.Success ? int.Parse(ml.Groups[1].Value) : 0;

                var mi = RxAttrId.Match(tag);
                int c;
                if (mi.Success && counts.TryGetValue(mi.Groups[1].Value, out c)) d.Count = c;
                g.Days[d.D] = d;
            }

            if (g.Days.Count == 0)
            {
                g.Error = "GitHub returned no calendar for '" + user + "' (wrong username, or the page layout changed).";
                return g;
            }

            foreach (var d in g.Days.Values) g.Total += d.Count;
            g.Available = true;
            g.Fetched = DateTime.Now;

            try
            {
                var sb = new StringBuilder();
                sb.Append("{\"user\":\"").Append(user).Append("\",\"days\":{");
                bool first = true;
                foreach (var d in g.Days.Values.OrderBy(x => x.D))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(d.D).Append("\":[").Append(d.Count).Append(',').Append(d.Level).Append(']');
                }
                sb.Append("}}");
                Directory.CreateDirectory(Path.GetDirectoryName(cacheFile));
                File.WriteAllText(cacheFile, sb.ToString());
            }
            catch { }

            return g;
        }
    }

    // ---------------------------------------------------------------- config

    static class Config
    {
        public static string Dir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DevTokenMeter");
            }
        }

        public static string ReadGhUser()
        {
            var f = Path.Combine(Dir, "config.json");
            if (!File.Exists(f)) return null;
            try
            {
                var m = Regex.Match(File.ReadAllText(f), "\"githubUser\"\\s*:\\s*\"([^\"]*)\"");
                return m.Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : null;
            }
            catch { return null; }
        }

        public static void WriteGhUser(string user)
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, "config.json"),
                "{\"githubUser\":\"" + (user ?? "").Replace("\"", "") + "\"}");
        }
    }
}
