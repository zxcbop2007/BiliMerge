using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace BiliMerge
{
    public class Item
    {
        public string Dir;
        public string Title;
        public string Part;
        public long Aid;
        public long Cid;
        public string Video;
        public string Audio;
        public long Size;
        public string Status = "待处理";
    }

    /// <summary>与界面无关的核心逻辑，GUI 和命令行共用。</summary>
    public static class Core
    {
        public static string SafeName(string s)
        {
            if (string.IsNullOrEmpty(s)) s = "untitled";
            StringBuilder sb = new StringBuilder();
            foreach (char c in s)
            {
                if (c < 32 || c == '\\' || c == '/' || c == ':' || c == '*' ||
                    c == '?' || c == '"' || c == '<' || c == '>' || c == '|')
                    sb.Append(' ');
                else
                    sb.Append(c);
            }
            string r = sb.ToString();
            while (r.Contains("  ")) r = r.Replace("  ", " ");
            r = r.Trim().TrimEnd('.', ' ');
            if (r.Length > 120) r = r.Substring(0, 120).TrimEnd('.', ' ');
            if (r.Length == 0) r = "untitled";
            return r;
        }

        public static string Short(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s.Length > 300 ? s.Substring(s.Length - 300) : s;
        }

        /// <summary>扫描根目录下所有 c_* 缓存目录。</summary>
        public static List<Item> Scan(string root, out int missingEntry, out List<string> errors)
        {
            errors = new List<string>();
            missingEntry = 0;
            List<Item> list = new List<Item>();
            string[] dirs = Directory.GetDirectories(root, "c_*", SearchOption.AllDirectories);

            foreach (string dir in dirs)
            {
                string entry = Path.Combine(dir, "entry.json");
                if (!File.Exists(entry)) { missingEntry++; continue; }
                try
                {
                    Item it = Parse(dir, entry);
                    if (it != null) list.Add(it);
                }
                catch (Exception ex)
                {
                    errors.Add(dir + " : " + ex.Message);
                }
            }
            list.Sort(delegate (Item a, Item b)
            {
                int c = a.Aid.CompareTo(b.Aid);
                return c != 0 ? c : a.Cid.CompareTo(b.Cid);
            });
            return list;
        }

        private static Item Parse(string dir, string entryFile)
        {
            string json = File.ReadAllText(entryFile, Encoding.UTF8);
            JavaScriptSerializer ser = new JavaScriptSerializer();
            Dictionary<string, object> root = ser.Deserialize<Dictionary<string, object>>(json);

            Item it = new Item();
            it.Dir = dir;
            it.Title = (root.ContainsKey("title") && root["title"] != null) ? Convert.ToString(root["title"]) : "";
            it.Aid = (root.ContainsKey("avid") && root["avid"] != null) ? Convert.ToInt64(root["avid"]) : 0;

            object pdObj;
            if (root.TryGetValue("page_data", out pdObj) && pdObj is Dictionary<string, object>)
            {
                Dictionary<string, object> pd = (Dictionary<string, object>)pdObj;
                object v;
                if (pd.TryGetValue("part", out v) && v != null) it.Part = Convert.ToString(v);
                if (pd.TryGetValue("cid", out v) && v != null) it.Cid = Convert.ToInt64(v);
            }

            long best = -1;
            foreach (string sub in Directory.GetDirectories(dir))
            {
                string vfile = Path.Combine(sub, "video.m4s");
                if (!File.Exists(vfile)) continue;
                long sz = new FileInfo(vfile).Length;
                if (sz > best)
                {
                    best = sz;
                    it.Video = vfile;
                    string afile = Path.Combine(sub, "audio.m4s");
                    it.Audio = File.Exists(afile) ? afile : null;
                    it.Size = sz + (it.Audio != null ? new FileInfo(it.Audio).Length : 0);
                }
            }
            return it.Video == null ? null : it;
        }

        public static string ProbeCodec(string ffprobePath, string video)
        {
            if (string.IsNullOrEmpty(ffprobePath) || !File.Exists(ffprobePath)) return null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(ffprobePath,
                    "-hide_banner -v error -select_streams v:0 -show_entries stream=codec_name " +
                    "-of default=noprint_wrappers=1:nokey=1 \"" + video + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    string s = p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                    return s.Trim();
                }
            }
            catch { return null; }
        }

        /// <summary>把 video.m4s + audio.m4s 无损封装成 mp4。</summary>
        public static bool MergeOne(string ffmpegPath, string ffprobePath, Item it, string dest,
                                    out string errmsg, out Process running)
        {
            running = null;
            string codec = ProbeCodec(ffprobePath, it.Video);
            string tag = null;
            if (codec == "hevc") tag = "hvc1";
            else if (codec == "h264") tag = "avc1";
            else if (codec == "av1") tag = "av01";
            else if (codec == "vp9") tag = "vp09";

            StringBuilder sb = new StringBuilder();
            sb.Append("-hide_banner -y -loglevel error ");
            sb.Append("-i \"").Append(it.Video).Append("\" ");
            if (it.Audio != null)
            {
                sb.Append("-i \"").Append(it.Audio).Append("\" ");
                sb.Append("-map 0:v:0 -map 1:a:0 ");
            }
            else sb.Append("-map 0:v:0 ");
            sb.Append("-c copy ");
            if (tag != null) sb.Append("-tag:v ").Append(tag).Append(' ');
            sb.Append("-movflags +faststart ");
            sb.Append('"').Append(dest).Append('"');

            ProcessStartInfo psi = new ProcessStartInfo(ffmpegPath, sb.ToString());
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;
            psi.StandardErrorEncoding = Encoding.UTF8;

            using (Process p = new Process())
            {
                p.StartInfo = psi;
                p.Start();
                running = p;
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                errmsg = err;
                if (p.ExitCode != 0) return false;
                return File.Exists(dest) && new FileInfo(dest).Length > 0;
            }
        }

        public static string FindFfmpeg(out string appDirCandidates)
        {
            List<string> cands = new List<string>();
            string app = Application.StartupPath;
            cands.Add(Path.Combine(app, "ffmpeg.exe"));
            cands.Add(Path.Combine(app, "bin", "ffmpeg.exe"));
            cands.Add(Path.Combine(app, "ffmpeg", "bin", "ffmpeg.exe"));
            cands.Add(@"C:\ffmpeg\bin\ffmpeg.exe");
            cands.Add(@"C:\ffmpeg\ffmpeg.exe");

            string pathVar = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathVar))
            {
                foreach (string p in pathVar.Split(';'))
                {
                    string d = p.Trim().Trim('"');
                    if (d.Length == 0) continue;
                    try { cands.Add(Path.Combine(d, "ffmpeg.exe")); } catch { }
                }
            }
            appDirCandidates = string.Join(";", cands.ToArray());
            foreach (string c in cands)
            {
                try { if (File.Exists(c)) return c; } catch { }
            }
            return null;
        }

        public static string Version(string ffmpegPath)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(ffmpegPath, "-hide_banner -version");
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    string so = p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return null; }
                    if (p.ExitCode != 0) return null;
                    string line = so.Split('\n').FirstOrDefault();
                    return line == null ? "ffmpeg" : line.Trim();
                }
            }
            catch { return null; }
        }
    }

    public class MainForm : Form
    {
        private Label lblFfmpeg;
        private Button btnDetect, btnBrowseFf, btnDownload;
        private TextBox txtIn, txtOut;
        private Button btnIn, btnOut;
        private Button btnScan, btnStart, btnStop, btnOpen;
        private Button btnAll, btnNone;
        private Label lblSel;
        private ProgressBar bar;
        private Label lblStatus;
        private ListView lv;
        private TextBox txtLog;

        private string ffmpegPath = "";
        private string ffprobePath = "";
        private List<Item> items = new List<Item>();
        private Thread worker;
        private volatile bool stopFlag = false;
        private Process currentProc;

        private static readonly string SettingsDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BiliMerge");
        private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

        public MainForm()
        {
            BuildUi();
            LoadSettings();
            DetectFfmpeg(true);
            Log("B站缓存合并工具 v1.4   作者：zxcbop2007");
        }

        private void BuildUi()
        {
            Text = "B站缓存合并工具  v1.4   作者：zxcbop2007";
            ClientSize = new Size(1000, 730);
            MinimumSize = new Size(880, 620);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F);

            Label l1 = MakeLabel("FFmpeg:", 12, 15, 60);
            lblFfmpeg = MakeLabel("检测中...", 78, 15, 610);
            lblFfmpeg.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);

            btnDetect = MakeButton("重新检测", 700, 10, 88, 28);
            btnDetect.Click += delegate { DetectFfmpeg(true); };
            btnBrowseFf = MakeButton("指定路径", 794, 10, 88, 28);
            btnBrowseFf.Click += delegate { BrowseFfmpeg(); };
            btnDownload = MakeButton("下载 FFmpeg", 888, 10, 100, 28);
            btnDownload.Click += delegate { DownloadFfmpeg(); };

            Label l2 = MakeLabel("输入目录:", 12, 52, 68);
            txtIn = MakeText(84, 49, 790, 26);
            btnIn = MakeButton("浏览", 880, 48, 106, 28);
            btnIn.Click += delegate { PickFolder(txtIn); };

            Label l3 = MakeLabel("输出目录:", 12, 88, 68);
            txtOut = MakeText(84, 85, 790, 26);
            btnOut = MakeButton("浏览", 880, 84, 106, 28);
            btnOut.Click += delegate { PickFolder(txtOut); };

            btnScan = MakeButton("扫描", 84, 122, 90, 30);
            btnScan.Click += delegate { DoScan(true); };
            btnStart = MakeButton("开始合并", 182, 122, 110, 30);
            btnStart.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            btnStart.Click += delegate { StartMerge(); };
            btnStop = MakeButton("停止", 300, 122, 80, 30);
            btnStop.Enabled = false;
            btnStop.Click += delegate { RequestStop(); };
            btnOpen = MakeButton("打开输出目录", 388, 122, 116, 30);
            btnOpen.Click += delegate { OpenOutDir(); };

            Label l4 = MakeLabel("勾选:", 520, 127, 40);
            btnAll = MakeButton("全选", 562, 122, 76, 30);
            btnAll.Click += delegate { SetAllChecked(true); };
            btnNone = MakeButton("全不选", 644, 122, 84, 30);
            btnNone.Click += delegate { SetAllChecked(false); };
            lblSel = MakeLabel("", 736, 127, 252);

            bar = new ProgressBar();
            bar.Location = new Point(12, 162);
            bar.Size = new Size(976, 18);
            bar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            bar.Style = ProgressBarStyle.Continuous;

            lblStatus = MakeLabel("就绪。请确认 FFmpeg 状态，选择目录后点「扫描」。", 12, 186, 976);

            lv = new ListView();
            lv.Location = new Point(12, 212);
            lv.Size = new Size(976, 300);
            lv.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            lv.View = View.Details;
            lv.CheckBoxes = true;
            lv.FullRowSelect = true;
            lv.GridLines = true;
            lv.HideSelection = false;
            lv.Columns.Add("#", 46);
            lv.Columns.Add("标题", 440);
            lv.Columns.Add("分P", 140);
            lv.Columns.Add("大小", 85, HorizontalAlignment.Right);
            lv.Columns.Add("状态", 250);

            txtLog = new TextBox();
            txtLog.Location = new Point(12, 520);
            txtLog.Size = new Size(976, 198);
            txtLog.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            txtLog.Multiline = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.ReadOnly = true;
            txtLog.BackColor = Color.FromArgb(250, 250, 250);
            txtLog.Font = new Font("Consolas", 9F);

            Controls.Add(l1); Controls.Add(lblFfmpeg);
            Controls.Add(btnDetect); Controls.Add(btnBrowseFf); Controls.Add(btnDownload);
            Controls.Add(l2); Controls.Add(txtIn); Controls.Add(btnIn);
            Controls.Add(l3); Controls.Add(txtOut); Controls.Add(btnOut);
            Controls.Add(btnScan); Controls.Add(btnStart); Controls.Add(btnStop);
            Controls.Add(btnOpen); Controls.Add(l4); Controls.Add(btnAll); Controls.Add(btnNone); Controls.Add(lblSel);
            Controls.Add(bar); Controls.Add(lblStatus); Controls.Add(lv); Controls.Add(txtLog);

            lv.ItemCheck += delegate { BeginInvoke((MethodInvoker)delegate { UpdateSelectionLabel(); }); };
        }

        private Label MakeLabel(string text, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = text;
            l.Location = new Point(x, y);
            l.Size = new Size(w, 20);
            l.AutoEllipsis = true;
            return l;
        }

        private Button MakeButton(string text, int x, int y, int w, int h)
        {
            Button b = new Button();
            b.Text = text;
            b.Location = new Point(x, y);
            b.Size = new Size(w, h);
            b.UseVisualStyleBackColor = true;
            return b;
        }

        private TextBox MakeText(int x, int y, int w, int h)
        {
            TextBox t = new TextBox();
            t.Location = new Point(x, y);
            t.Size = new Size(w, h);
            t.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            return t;
        }

        // ================= FFmpeg =================
        private void DetectFfmpeg(bool verbose)
        {
            string dump;
            string found = Core.FindFfmpeg(out dump);

            string saved = LoadSetting("ffmpeg");
            if (found == null && !string.IsNullOrEmpty(saved) && File.Exists(saved))
            {
                // 用户上次指定的路径优先
                string v0 = Core.Version(saved);
                if (v0 != null) { found = saved; }
            }

            if (found == null)
            {
                ffmpegPath = "";
                lblFfmpeg.Text = "未找到 FFmpeg  →  点「下载 FFmpeg」自动安装，或点「指定路径」";
                lblFfmpeg.ForeColor = Color.Firebrick;
                btnDownload.Enabled = true;
                if (verbose) Log("未检测到 ffmpeg.exe。已查找：程序目录、PATH、C:\\ffmpeg\\bin。");
                return;
            }

            ffmpegPath = found;
            ffprobePath = Path.Combine(Path.GetDirectoryName(ffmpegPath), "ffprobe.exe");
            if (!File.Exists(ffprobePath)) ffprobePath = "";

            string ver = Core.Version(ffmpegPath);
            if (ver == null)
            {
                lblFfmpeg.Text = "找到 " + ffmpegPath + " 但无法运行";
                lblFfmpeg.ForeColor = Color.Firebrick;
                ffmpegPath = "";
                btnDownload.Enabled = true;
                return;
            }
            string shortVer = ver;
            int idx = ver.IndexOf(" Copyright");
            if (idx > 0) shortVer = ver.Substring(0, idx);
            lblFfmpeg.Text = "已就绪  " + shortVer + "   [" + ffmpegPath + "]";
            lblFfmpeg.ForeColor = Color.ForestGreen;
            btnDownload.Enabled = false;
            if (verbose) Log("FFmpeg: " + shortVer + " @ " + ffmpegPath);
            if (ffprobePath.Length == 0) Log("提示：同目录没有 ffprobe.exe，跳过编码探测（视频标签可能不精确）。");
        }

        private void BrowseFfmpeg()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "选择 ffmpeg.exe";
                d.Filter = "ffmpeg.exe|ffmpeg.exe|可执行文件|*.exe";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string ver = Core.Version(d.FileName);
                if (ver == null)
                {
                    MessageBox.Show(this, "这个文件无法运行，请换一个。", "无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                ffmpegPath = d.FileName;
                ffprobePath = Path.Combine(Path.GetDirectoryName(ffmpegPath), "ffprobe.exe");
                if (!File.Exists(ffprobePath)) ffprobePath = "";
                lblFfmpeg.Text = "已就绪  [" + ffmpegPath + "]";
                lblFfmpeg.ForeColor = Color.ForestGreen;
                btnDownload.Enabled = false;
                SaveSetting("ffmpeg", ffmpegPath);
                Log("已指定 FFmpeg: " + ffmpegPath);
            }
        }

        private void DownloadFfmpeg()
        {
            DialogResult r = MessageBox.Show(this,
                "将从 GitHub 下载官方构建的 FFmpeg（约 110 MB），自动解压到本程序目录下的 ffmpeg 文件夹。\n\n继续吗？",
                "下载 FFmpeg", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (r != DialogResult.OK) return;

            btnDownload.Enabled = false;
            Thread t = new Thread(delegate ()
            {
                string url = "https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip";
                string zip = Path.Combine(Path.GetTempPath(), "ffmpeg_dl.zip");
                string target = Path.Combine(Application.StartupPath, "ffmpeg");
                try
                {
                    Log("开始下载 " + url);
                    ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
                    using (WebClient wc = new WebClient())
                    {
                        wc.Headers.Add("User-Agent", "BiliMerge/1.1");
             