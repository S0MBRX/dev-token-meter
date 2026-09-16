// Native WinForms shell: three stacked contribution grids, details collapsed by default.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace DevTokenMeter
{
    static class Theme
    {
        public static readonly Color Bg = ColorTranslator.FromHtml("#141413");
        public static readonly Color Panel = ColorTranslator.FromHtml("#1c1b1a");
        public static readonly Color Panel2 = ColorTranslator.FromHtml("#232221");
        public static readonly Color Line = ColorTranslator.FromHtml("#332f2c");
        public static readonly Color Fg = ColorTranslator.FromHtml("#f5f4f1");
        public static readonly Color Dim = ColorTranslator.FromHtml("#a19c94");
        public static readonly Color Faint = ColorTranslator.FromHtml("#6e675f");

        public static readonly Color Claude = ColorTranslator.FromHtml("#d97757");
        public static readonly Color Codex = ColorTranslator.FromHtml("#8b7fd4");
        public static readonly Color GitHub = ColorTranslator.FromHtml("#39d353");

        public static Color[] Ramp(string key)
        {
            if (key == "claude")
                return Hex("#232221", "#3d2a22", "#6b3e2c", "#a85636", "#d97757", "#f0a07d");
            if (key == "codex")
                return Hex("#232221", "#282544", "#3c3670", "#5b4fa6", "#8b7fd4", "#b3aae8");
            // classic github greens
            return Hex("#232221", "#0e4429", "#006d32", "#26a641", "#39d353", "#39d353");
        }
        static Color[] Hex(params string[] h)
        {
            var a = new Color[h.Length];
            for (int i = 0; i < h.Length; i++) a[i] = ColorTranslator.FromHtml(h[i]);
            return a;
        }

        // every font is 2x the original sizes
        public static readonly Font UI = new Font("Segoe UI", 18f);
        public static readonly Font UIBold = new Font("Segoe UI", 18f, FontStyle.Bold);
        public static readonly Font Head = new Font("Segoe UI", 22f, FontStyle.Bold);
        public static readonly Font Big = new Font("Consolas", 30f, FontStyle.Bold);
        public static readonly Font Mono = new Font("Consolas", 18f);
        public static readonly Font MonoSmall = new Font("Consolas", 15f);
    }

    static class Fmt
    {
        public static string Short(long n)
        {
            if (n >= 1000000000L) return (n / 1000000000.0).ToString("0.00", CultureInfo.InvariantCulture) + "B";
            if (n >= 1000000L) return (n / 1000000.0).ToString("0.00", CultureInfo.InvariantCulture) + "M";
            if (n >= 1000L) return (n / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k";
            return n.ToString(CultureInfo.InvariantCulture);
        }
        public static string Grp(long n) { return n.ToString("N0", CultureInfo.InvariantCulture); }
    }

    // ---------------------------------------------------------------- heatmap

    class HeatMapView : Control
    {
        public const int Cell = 13, Gap = 3, Step = Cell + Gap;
        const int Gutter = 28, MonthBar = 24;

        public DateTime End = DateTime.Today;
        public int RangeDays = 365;
        public Color[] Ramp = Theme.Ramp("claude");
        public Func<string, long> GetValue = k => 0;
        public Func<string, int> GetLevel;            // when set, used instead of relative scaling
        public Func<string, string> GetTooltip = k => null;

        readonly ToolTip _tip = new ToolTip();
        string _hot;

        public HeatMapView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;
            _tip.OwnerDraw = true;
            _tip.ShowAlways = true;
            _tip.UseAnimation = false;
            _tip.UseFading = false;
            _tip.Popup += (s, e) =>
            {
                var txt = _pendingText ?? "";
                using (var g = CreateGraphics())
                {
                    var sz = g.MeasureString(txt, Theme.Mono);
                    e.ToolTipSize = new Size((int)Math.Ceiling(sz.Width) + 28, (int)Math.Ceiling(sz.Height) + 22);
                }
            };
            _tip.Draw += (s, e) =>
            {
                e.Graphics.FillRectangle(new SolidBrush(ColorTranslator.FromHtml("#0a0a09")), e.Bounds);
                using (var pen = new Pen(ColorTranslator.FromHtml("#4a443e")))
                    e.Graphics.DrawRectangle(pen, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
                e.Graphics.DrawString(e.ToolTipText, Theme.Mono, new SolidBrush(Theme.Fg), 14, 11);
            };
        }

        string _pendingText;

        public int Weeks
        {
            get
            {
                var start = End.AddDays(-(RangeDays - 1));
                int lead = ((int)start.DayOfWeek + 6) % 7;
                return (int)Math.Ceiling((lead + RangeDays) / 7.0);
            }
        }

        public Size Preferred { get { return new Size(Gutter + Weeks * Step + 8, MonthBar + 7 * Step + 4); } }

        long MaxValue()
        {
            long max = 0;
            var start = End.AddDays(-(RangeDays - 1));
            for (var d = start; d <= End; d = d.AddDays(1))
            {
                long v = GetValue(Key(d));
                if (v > max) max = v;
            }
            return max;
        }

        static string Key(DateTime d) { return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }

        int LevelOf(string key, long max)
        {
            if (GetLevel != null) return Math.Min(5, Math.Max(0, GetLevel(key)));
            long v = GetValue(key);
            if (v <= 0 || max <= 0) return 0;
            double r = (double)v / max;
            return r > .75 ? 5 : r > .5 ? 4 : r > .28 ? 3 : r > .12 ? 2 : 1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var start = End.AddDays(-(RangeDays - 1));
            int lead = ((int)start.DayOfWeek + 6) % 7;
            long max = GetLevel != null ? 0 : MaxValue();

            using (var dim = new SolidBrush(Theme.Faint))
            {
                g.DrawString("M", Theme.MonoSmall, dim, 4, MonthBar + Step * 1 - 3);
                g.DrawString("W", Theme.MonoSmall, dim, 4, MonthBar + Step * 3 - 3);
                g.DrawString("F", Theme.MonoSmall, dim, 4, MonthBar + Step * 5 - 3);
            }

            int lastMonth = -1;
            var today = DateTime.Today;
            for (int i = 0; i < lead + RangeDays; i++)
            {
                int col = i / 7, row = i % 7;
                int x = Gutter + col * Step, y = MonthBar + row * Step;
                if (i < lead) continue;

                var date = start.AddDays(i - lead);
                var key = Key(date);

                if (row == 0 && date.Month != lastMonth)
                {
                    lastMonth = date.Month;
                    using (var b = new SolidBrush(Theme.Faint))
                        g.DrawString(date.ToString("MMM", CultureInfo.InvariantCulture), Theme.MonoSmall, b, x - 1, 1);
                }

                using (var b = new SolidBrush(Ramp[LevelOf(key, max)]))
                    Round(g, b, x, y, Cell, Cell, 3);

                if (date == today)
                    using (var pen = new Pen(ColorTranslator.FromHtml("#5a8dd6"), 1.4f))
                        g.DrawRectangle(pen, x - 1, y - 1, Cell + 1, Cell + 1);
            }
        }

        static void Round(Graphics g, Brush b, int x, int y, int w, int h, int r)
        {
            using (var p = new GraphicsPath())
            {
                p.AddArc(x, y, r * 2, r * 2, 180, 90);
                p.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
                p.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
                p.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
                p.CloseFigure();
                g.FillPath(b, p);
            }
        }

        string HitTest(Point p)
        {
            int col = (p.X - Gutter) / Step, row = (p.Y - MonthBar) / Step;
            if (col < 0 || row < 0 || row > 6) return null;
            if ((p.X - Gutter) % Step > Cell || (p.Y - MonthBar) % Step > Cell) return null;

            var start = End.AddDays(-(RangeDays - 1));
            int lead = ((int)start.DayOfWeek + 6) % 7;
            int i = col * 7 + row;
            if (i < lead || i >= lead + RangeDays) return null;
            return Key(start.AddDays(i - lead));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var key = HitTest(e.Location);
            if (key == _hot) return;
            _hot = key;
            if (key == null) { _tip.Hide(this); return; }

            var text = GetTooltip(key);
            if (string.IsNullOrEmpty(text)) { _tip.Hide(this); return; }
            _pendingText = text;
            _tip.Hide(this);                       // force a clean re-show when moving cell to cell
            _tip.Show(text, this, e.X + 22, e.Y + 24, 60000);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hot = null; _tip.Hide(this);
            base.OnMouseLeave(e);
        }
    }

    // ---------------------------------------------------------------- details

    class DetailsView : Control
    {
        public string[][] Tiles = new string[0][];
        public string BarsTitleA = "By model", BarsTitleB = "By project";
        public List<Bucket> BarsA = new List<Bucket>(), BarsB = new List<Bucket>();
        public List<string[]> Rows = new List<string[]>();
        public string RowsTitle = "Heaviest sessions";
        public Color Accent = Theme.Claude;

        public DetailsView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;
        }

        public int Preferred()
        {
            int tileRows = (int)Math.Ceiling(Tiles.Length / 3.0);
            int h = 12 + tileRows * 104 + 16;
            if (BarsA.Count > 0) h += 42 + Math.Min(6, BarsA.Count) * 38 + 16;
            if (BarsB.Count > 0) h += 42 + Math.Min(6, BarsB.Count) * 38 + 16;
            if (Rows.Count > 0) h += 42 + Math.Min(8, Rows.Count) * 36 + 16;
            return h;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int y = 10, w = Math.Max(400, Width - 24);

            // tiles, 3 per row (2x text needs the width)
            int tw = (w - 2 * 12) / 3;
            for (int i = 0; i < Tiles.Length; i++)
            {
                int col = i % 3, row = i / 3;
                int x = 12 + col * (tw + 12), ty = y + row * 104;
                using (var b = new SolidBrush(Theme.Panel2)) Round(g, b, x, ty, tw, 92, 10);
                using (var b = new SolidBrush(Theme.Dim))
                    g.DrawString(Tiles[i][0].ToUpperInvariant(), Theme.MonoSmall, b, x + 12, ty + 8);
                using (var b = new SolidBrush(i == 0 ? Accent : Theme.Fg))
                    g.DrawString(Tiles[i][1], Theme.Mono, b, x + 11, ty + 34);
                using (var b = new SolidBrush(Theme.Faint))
                    g.DrawString(Tiles[i][2], Theme.MonoSmall, b, x + 12, ty + 64);
            }
            y += (int)Math.Ceiling(Tiles.Length / 3.0) * 104 + 16;

            y = Bars(g, y, w, BarsTitleA, BarsA);
            y = Bars(g, y, w, BarsTitleB, BarsB);

            if (Rows.Count > 0)
            {
                using (var b = new SolidBrush(Theme.Dim))
                    g.DrawString(RowsTitle.ToUpperInvariant(), Theme.MonoSmall, b, 12, y);
                y += 40;
                foreach (var r in Rows.Take(8))
                {
                    using (var b = new SolidBrush(Theme.Faint))
                        g.DrawString(r[0], Theme.MonoSmall, b, 14, y);
                    using (var b = new SolidBrush(Theme.Dim))
                        g.DrawString(r[1], Theme.MonoSmall, b, 230, y);
                    using (var b = new SolidBrush(Theme.Fg))
                        g.DrawString(r[2], Theme.MonoSmall, b, w - 130, y);
                    y += 36;
                }
                y += 16;
            }
        }

        int Bars(Graphics g, int y, int w, string title, List<Bucket> list)
        {
            if (list.Count == 0) return y;
            using (var b = new SolidBrush(Theme.Dim))
                g.DrawString(title.ToUpperInvariant(), Theme.MonoSmall, b, 12, y);
            y += 40;
            long max = Math.Max(1, list.Max(z => z.Total));
            foreach (var it in list.Take(6))
            {
                var name = it.Name.Length > 22 ? it.Name.Substring(0, 21) + "…" : it.Name;
                using (var b = new SolidBrush(Theme.Dim))
                    g.DrawString(name, Theme.MonoSmall, b, 14, y + 2);
                int bx = 340, bw = Math.Max(40, w - 340 - 130);
                using (var b = new SolidBrush(Theme.Panel2)) Round(g, b, bx, y, bw, 26, 5);
                int fw = Math.Max(4, (int)(bw * (double)it.Total / max));
                using (var b = new SolidBrush(Accent)) Round(g, b, bx, y, fw, 26, 5);
                using (var b = new SolidBrush(Theme.Fg))
                    g.DrawString(Fmt.Short(it.Total), Theme.MonoSmall, b, bx + bw + 12, y + 2);
                y += 38;
            }
            return y + 16;
        }

        static void Round(Graphics g, Brush b, int x, int y, int w, int h, int r)
        {
            if (w < r * 2 || h < r * 2) { g.FillRectangle(b, x, y, w, h); return; }
            using (var p = new GraphicsPath())
            {
                p.AddArc(x, y, r * 2, r * 2, 180, 90);
                p.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
                p.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
                p.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
                p.CloseFigure();
                g.FillPath(b, p);
            }
        }
    }

    // ---------------------------------------------------------------- section

    class Section : Panel
    {
        public readonly HeatMapView Map = new HeatMapView();
        public readonly DetailsView Details = new DetailsView();
        readonly Label _title = new Label();
        readonly Label _amount = new Label();
        readonly Label _toggle = new Label();
        readonly Color _accent;
        bool _open;

        public Section(string name, Color accent, string rampKey)
        {
            _accent = accent;
            BackColor = Theme.Panel;
            Padding = new Padding(0);

            _title.Text = name;
            _title.Font = Theme.Head;
            _title.ForeColor = Theme.Fg;
            _title.AutoSize = true;
            _title.Location = new Point(42, 12);

            _amount.Font = Theme.Mono;
            _amount.ForeColor = accent;
            _amount.AutoSize = true;
            _amount.Location = new Point(42, 52);

            _toggle.Text = "▸ details";
            _toggle.Font = Theme.Mono;
            _toggle.ForeColor = Theme.Faint;
            _toggle.AutoSize = true;
            _toggle.Cursor = Cursors.Hand;
            _toggle.Click += (s, e) => { Open = !Open; };

            Map.Ramp = Theme.Ramp(rampKey);
            Details.Accent = accent;
            Details.Visible = false;

            Controls.Add(_title);
            Controls.Add(_amount);
            Controls.Add(_toggle);
            Controls.Add(Map);
            Controls.Add(Details);
        }

        public string Amount { set { _amount.Text = value; } }

        public bool Open
        {
            get { return _open; }
            set
            {
                _open = value;
                _toggle.Text = (_open ? "▾" : "▸") + " details";
                Details.Visible = _open;
                Relayout();
                var f = FindForm() as MainForm;
                if (f != null) f.Restack();
            }
        }

        public void Relayout()
        {
            Map.Location = new Point(30, 88);
            Map.Size = Map.Preferred;
            _toggle.Location = new Point(30, Map.Bottom + 10);
            Details.Location = new Point(14, _toggle.Bottom + 8);
            Details.Size = new Size(Math.Max(420, Width - 28), Details.Preferred());
            Height = (_open ? Details.Bottom : _toggle.Bottom) + 16;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(_accent))
                g.FillEllipse(b, 16, 22, 14, 14);
            using (var p = new Pen(Theme.Line))
                g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }
    }

    // ---------------------------------------------------------------- main form

    class MainForm : Form
    {
        readonly Section _gh = new Section("GitHub", Theme.GitHub, "github");
        readonly Section _cl = new Section("Claude Code", Theme.Claude, "claude");
        readonly Section _cx = new Section("Codex", Theme.Codex, "codex");
        readonly Panel _host = new Panel();
        readonly Label _status = new Label();
        readonly FlowLayoutPanel _bar = new FlowLayoutPanel();

        Agg _claude, _codex;
        GitHubData _github;
        string _metric = "total";
        int _range = 365;
        string _ghUser;
        volatile bool _busy;

        static readonly string[][] Metrics = {
            new[]{"total","Total"}, new[]{"output","Output"}, new[]{"input","Input"},
            new[]{"cacheWrite","Cache write"}, new[]{"cacheRead","Cache read"}, new[]{"msgs","Replies"}
        };
        static readonly int[] Ranges = { 30, 90, 180, 365 };

        public MainForm()
        {
            Text = "Dev Token Meter";
            BackColor = Theme.Bg;
            ForeColor = Theme.Fg;
            Font = Theme.UI;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(940, 620);
            ClientSize = new Size(1000, 780);

            _bar.Dock = DockStyle.Top;
            _bar.BackColor = Theme.Bg;
            _bar.Padding = new Padding(10, 10, 10, 6);
            _bar.WrapContents = true;               // 2x text no longer fits on one row
            _bar.AutoSize = true;
            _bar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _bar.AutoScroll = false;

            foreach (var r in Ranges) _bar.Controls.Add(MakeToggle(r + "d", () => _range == r, () => { _range = r; Refresh2(false); }));
            _bar.Controls.Add(Spacer(14));
            foreach (var m in Metrics)
            {
                var mm = m;
                _bar.Controls.Add(MakeToggle(mm[1], () => _metric == mm[0], () => { _metric = mm[0]; Refresh2(false); }));
            }
            _bar.Controls.Add(Spacer(14));
            _bar.Controls.Add(MakeButton("Rescan", () => Load2(true)));
            _bar.Controls.Add(MakeButton("GitHub…", PromptUser));

            _status.Dock = DockStyle.Bottom;
            _status.Height = 42;
            _status.ForeColor = Theme.Faint;
            _status.Font = Theme.MonoSmall;
            _status.TextAlign = ContentAlignment.MiddleLeft;
            _status.Padding = new Padding(12, 0, 0, 0);

            _host.Dock = DockStyle.Fill;
            _host.AutoScroll = true;
            _host.BackColor = Theme.Bg;
            _host.Padding = new Padding(10, 6, 10, 10);
            _host.Controls.Add(_gh);
            _host.Controls.Add(_cl);
            _host.Controls.Add(_cx);

            Controls.Add(_host);
            Controls.Add(_bar);
            Controls.Add(_status);

            Resize += (s, e) => Restack();
            Shown += (s, e) => Load2(false);
        }

        static Control Spacer(int w)
        {
            return new Panel { Width = w, Height = 46, BackColor = Color.Transparent };
        }

        readonly List<Tuple<Button, Func<bool>>> _toggles = new List<Tuple<Button, Func<bool>>>();

        Button MakeToggle(string text, Func<bool> isOn, Action onClick)
        {
            var b = BaseButton(text);
            b.Click += (s, e) => { onClick(); SyncToggles(); };
            _toggles.Add(Tuple.Create(b, isOn));
            return b;
        }

        Button MakeButton(string text, Action onClick)
        {
            var b = BaseButton(text);
            b.Click += (s, e) => onClick();
            return b;
        }

        static Button BaseButton(string text)
        {
            var b = new Button();
            b.Text = text;
            b.AutoSize = false;
            b.Height = 46;
            b.Width = TextRenderer.MeasureText(text, Theme.UI).Width + 30;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Theme.Line;
            b.FlatAppearance.MouseOverBackColor = ColorTranslator.FromHtml("#2b2927");
            b.BackColor = Theme.Panel2;
            b.ForeColor = Theme.Dim;
            b.Font = Theme.UI;
            b.Margin = new Padding(0, 0, 7, 0);
            b.TabStop = false;
            return b;
        }

        void SyncToggles()
        {
            foreach (var t in _toggles)
            {
                bool on = t.Item2();
                t.Item1.BackColor = on ? Accent() : Theme.Panel2;
                t.Item1.ForeColor = on ? Color.FromArgb(22, 17, 15) : Theme.Dim;
                t.Item1.Font = on ? Theme.UIBold : Theme.UI;
            }
        }

        static Color Accent() { return Theme.Claude; }

        void PromptUser()
        {
            using (var dlg = new InputDialog("GitHub username", _ghUser ?? ""))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _ghUser = dlg.Value.Trim();
                Config.WriteGhUser(_ghUser);
                Load2(true);
            }
        }

        void Load2(bool forceGh)
        {
            if (_busy) return;
            _busy = true;
            _status.Text = "scanning…";
            _ghUser = Config.ReadGhUser();
            var cutoff = DateTime.Today.AddDays(-399);

            var th = new Thread(() =>
            {
                Agg cl = null, cx = null; GitHubData gh = null;
                string err = null;
                try
                {
                    cl = Scan.Claude(cutoff);
                    cx = Scan.Codex(cutoff);
                    gh = Scan.GitHub(_ghUser, System.IO.Path.Combine(Config.Dir, "github-cache.json"), forceGh);
                }
                catch (Exception ex) { err = ex.Message; }

                BeginInvoke((Action)(() =>
                {
                    _busy = false;
                    if (err != null) { _status.Text = "failed: " + err; return; }
                    _claude = cl; _codex = cx; _github = gh;
                    Refresh2(true);
                }));
            });
            th.IsBackground = true;
            th.Start();
        }

        long DayVal(Day d)
        {
            if (d == null) return 0;
            switch (_metric)
            {
                case "output": return d.Output;
                case "input": return d.Input;
                case "cacheWrite": return d.CacheWrite;
                case "cacheRead": return d.CacheRead;
                case "msgs": return d.Msgs;
                default: return d.Total;
            }
        }

        static string DayTip(string key, Day d)
        {
            var date = DateTime.ParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var head = date.ToString("ddd, MMM d yyyy", CultureInfo.InvariantCulture);
            if (d == null) return head + "\nno activity";
            return head +
                "\n\nTotal tokens   " + Fmt.Grp(d.Total) +
                "\n  Output       " + Fmt.Grp(d.Output) +
                "\n  Input        " + Fmt.Grp(d.Input) +
                "\n  Cache write  " + Fmt.Grp(d.CacheWrite) +
                "\n  Cache read   " + Fmt.Grp(d.CacheRead) +
                "\n\nReplies        " + Fmt.Grp(d.Msgs) +
                "\nSessions       " + Fmt.Grp(d.Sessions.Count);
        }

        void Refresh2(bool rebuildDetails)
        {
            if (_claude == null) return;

            WireTokens(_cl, _claude);
            WireTokens(_cx, _codex);
            WireGitHub();

            if (rebuildDetails)
            {
                FillTokenDetails(_cl, _claude);
                FillTokenDetails(_cx, _codex);
                FillGhDetails();
            }

            SyncToggles();
            Restack();
            _cl.Map.Invalidate(); _cx.Map.Invalidate(); _gh.Map.Invalidate();
            _cl.Details.Invalidate(); _cx.Details.Invalidate(); _gh.Details.Invalidate();

            _status.Text = "claude " + Fmt.Grp(_claude.Total) + " tok  ·  codex " + Fmt.Grp(_codex.Total) +
                " tok  ·  github " + (_github != null && _github.Available
                    ? Fmt.Grp(_github.Total) + " contribs (@" + _github.User + ")"
                    : "not set up") +
                "  ·  " + DateTime.Now.ToString("HH:mm:ss");
        }

        void WireTokens(Section sec, Agg a)
        {
            sec.Map.RangeDays = _range;
            sec.Map.End = DateTime.Today;
            sec.Map.GetLevel = null;
            sec.Map.GetValue = k => { Day d; return a.Days.TryGetValue(k, out d) ? DayVal(d) : 0; };
            sec.Map.GetTooltip = k => { Day d; a.Days.TryGetValue(k, out d); return DayTip(k, d); };
            sec.Amount = Fmt.Short(a.Total) + " tokens  ·  " + Fmt.Grp(a.Msgs) + " replies  ·  " +
                         a.Days.Count + " active days";
        }

        void WireGitHub()
        {
            _gh.Map.RangeDays = _range;
            _gh.Map.End = DateTime.Today;
            if (_github == null || !_github.Available)
            {
                _gh.Map.GetValue = k => 0;
                _gh.Map.GetLevel = k => 0;
                _gh.Map.GetTooltip = k => null;
                _gh.Amount = _github != null && _github.Error != null
                    ? _github.Error
                    : "no username set — click GitHub…";
                return;
            }
            _gh.Map.GetValue = k => { GhDay d; return _github.Days.TryGetValue(k, out d) ? d.Count : 0; };
            _gh.Map.GetLevel = k => { GhDay d; return _github.Days.TryGetValue(k, out d) ? d.Level : 0; };
            _gh.Map.GetTooltip = k =>
            {
                var date = DateTime.ParseExact(k, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                var head = date.ToString("ddd, MMM d yyyy", CultureInfo.InvariantCulture);
                GhDay d;
                if (!_github.Days.TryGetValue(k, out d) || d.Count == 0) return head + "\nNo contributions";
                return head + "\n" + Fmt.Grp(d.Count) + (d.Count == 1 ? " contribution" : " contributions");
            };
            _gh.Amount = Fmt.Grp(_github.Total) + " contributions  ·  @" + _github.User;
        }

        void FillTokenDetails(Section sec, Agg a)
        {
            var best = a.Days.Values.OrderByDescending(d => d.Total).FirstOrDefault();
            int peak = 0; long peakTok = 0;
            for (int h = 0; h < 24; h++) if (a.HourTok[h] > peakTok) { peakTok = a.HourTok[h]; peak = h; }
            long avg = a.Days.Count > 0 ? a.Total / a.Days.Count : 0;
            var topModel = a.Models.Values.OrderByDescending(b => b.Total).FirstOrDefault();

            sec.Details.Tiles = new[]
            {
                new[]{"Total tokens", Fmt.Short(a.Total), Fmt.Grp(a.Msgs) + " replies"},
                new[]{"Output", Fmt.Short(a.Output), Fmt.Grp(a.Think) + " reasoning"},
                new[]{"Fresh (uncached)", Fmt.Short(a.Input + a.Output + a.CacheWrite), "in + out + writes"},
                new[]{"Cache reads", Fmt.Short(a.CacheRead),
                      a.Total > 0 ? (100 * a.CacheRead / a.Total) + "% of all" : "-"},
                new[]{"Avg / active day", Fmt.Short(avg), a.Days.Count + " active days"},
                new[]{"Biggest day", best != null ? Fmt.Short(best.Total) : "-", best != null ? best.D : "-"},
                new[]{"Peak hour", peakTok > 0 ? peak.ToString("00") + ":00" : "-", Fmt.Short(peakTok) + " tokens"},
                new[]{"Sessions", Fmt.Grp(a.Sessions.Count), topModel != null ? topModel.Name : "-"}
            };
            sec.Details.BarsTitleA = "By model";
            sec.Details.BarsA = a.Models.Values.OrderByDescending(b => b.Total).Take(6).ToList();
            sec.Details.BarsTitleB = "By project";
            sec.Details.BarsB = a.Projects.Values.OrderByDescending(b => b.Total).Take(6).ToList();
            sec.Details.RowsTitle = "Heaviest sessions";
            sec.Details.Rows = a.Sessions.Values.OrderByDescending(s => s.Total).Take(8)
                .Select(s => new[]
                {
                    s.First.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture),
                    s.Proj != null && s.Proj.Length > 26 ? s.Proj.Substring(0, 25) + "…" : (s.Proj ?? "-"),
                    Fmt.Short(s.Total)
                }).ToList();
        }

        void FillGhDetails()
        {
            var d = _gh.Details;
            d.BarsA = new List<Bucket>(); d.BarsB = new List<Bucket>(); d.Rows = new List<string[]>();
            if (_github == null || !_github.Available)
            {
                d.Tiles = new[] { new[] { "GitHub", "not set up", "click GitHub…" } };
                return;
            }
            var days = _github.Days.Values.ToList();
            int active = days.Count(x => x.Count > 0);
            var best = days.OrderByDescending(x => x.Count).FirstOrDefault();
            int cur = 0;
            for (var dt = DateTime.Today; ; dt = dt.AddDays(-1))
            {
                GhDay g;
                var k = dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (!_github.Days.TryGetValue(k, out g) || g.Count == 0)
                {
                    if (dt == DateTime.Today) continue;
                    break;
                }
                cur++;
                if (cur > 400) break;
            }
            d.Tiles = new[]
            {
                new[]{"Contributions", Fmt.Grp(_github.Total), "@" + _github.User},
                new[]{"Active days", active.ToString(), days.Count + " tracked"},
                new[]{"Current streak", cur + "d", "consecutive"},
                new[]{"Busiest day", best != null ? best.Count.ToString() : "-", best != null ? best.D : "-"}
            };
        }

        public void Restack()
        {
            int y = 6;
            int w = Math.Max(460, _host.ClientSize.Width - 24);
            foreach (var sec in new[] { _gh, _cl, _cx })
            {
                sec.Left = 10;
                sec.Top = y;
                sec.Width = w;
                sec.Relayout();
                y = sec.Bottom + 10;
            }
        }
    }

    class InputDialog : Form
    {
        readonly TextBox _box = new TextBox();
        public string Value { get { return _box.Text; } }

        public InputDialog(string prompt, string initial)
        {
            Text = prompt;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = MinimizeBox = false;
            ClientSize = new Size(660, 210);
            BackColor = Theme.Bg;
            ForeColor = Theme.Fg;
            Font = Theme.UI;

            var lbl = new Label
            {
                Text = prompt,
                ForeColor = Theme.Dim,
                AutoSize = true,
                Location = new Point(24, 22)
            };
            _box.Text = initial;
            _box.Font = Theme.Mono;
            _box.Location = new Point(24, 72);
            _box.Width = 610;
            _box.BackColor = Theme.Panel2;
            _box.ForeColor = Theme.Fg;
            _box.BorderStyle = BorderStyle.FixedSingle;

            var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Location = new Point(340, 136), Width = 140, Height = 48 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(494, 136), Width = 140, Height = 48 };
            foreach (var b in new[] { ok, cancel })
            {
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderColor = Theme.Line;
                b.BackColor = Theme.Panel2;
                b.ForeColor = Theme.Fg;
            }
            Controls.AddRange(new Control[] { lbl, _box, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }

    static class App
    {
        static void Log(string what, Exception ex)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Config.Dir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(Config.Dir, "crash.log"),
                    DateTime.Now + "  " + what + "\r\n" + ex + "\r\n\r\n");
            }
            catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
                if (args[i].ToLowerInvariant() == "--github" && i + 1 < args.Length)
                    Config.WriteGhUser(args[++i]);

            Application.ThreadException += (s, e) => Log("ui", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log("domain", e.ExceptionObject as Exception);

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                Log("startup", ex);
                MessageBox.Show(ex.ToString(), "Dev Token Meter crashed");
            }
        }
    }
}
