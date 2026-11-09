using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ScreenTimeout
{
    public sealed class PowerState
    {
        public Guid Scheme;
        public uint AcSeconds;
        public uint DcSeconds;
    }

    public static class PowerSettings
    {
        private static Guid Video = new Guid("7516b95f-f776-4464-8c53-06167f40cc99");
        private static Guid Timeout = new Guid("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
        [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
        [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid group, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid group, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid group, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

        private static void Check(uint code)
        {
            if (code != 0) throw new Win32Exception((int)code);
        }

        public static Guid ActiveScheme()
        {
            IntPtr pointer;
            Check(PowerGetActiveScheme(IntPtr.Zero, out pointer));
            try { return (Guid)Marshal.PtrToStructure(pointer, typeof(Guid)); }
            finally { LocalFree(pointer); }
        }

        public static PowerState Read()
        {
            PowerState state = new PowerState();
            state.Scheme = ActiveScheme();
            Check(PowerReadACValueIndex(IntPtr.Zero, ref state.Scheme, ref Video, ref Timeout, out state.AcSeconds));
            Check(PowerReadDCValueIndex(IntPtr.Zero, ref state.Scheme, ref Video, ref Timeout, out state.DcSeconds));
            return state;
        }

        public static PowerState Set(PowerState before, uint seconds)
        {
            if (ActiveScheme() != before.Scheme)
                throw new InvalidOperationException("电源计划正在变化，请稍后再试。");
            Check(PowerWriteACValueIndex(IntPtr.Zero, ref before.Scheme, ref Video, ref Timeout, seconds));
            try
            {
                // Do not activate an old plan if another app has switched it meanwhile.
                if (ActiveScheme() != before.Scheme)
                    throw new InvalidOperationException("电源计划已被其他程序切换，请重试。");
                Check(PowerSetActiveScheme(IntPtr.Zero, ref before.Scheme));
                PowerState after = Read();
                if (after.Scheme != before.Scheme || after.AcSeconds != seconds || after.DcSeconds != before.DcSeconds)
                    throw new InvalidOperationException("设置校验失败，电源计划可能被其他程序或系统策略修改。");
                return after;
            }
            catch
            {
                // Restore only our AC setting; never overwrite a concurrently selected plan.
                PowerWriteACValueIndex(IntPtr.Zero, ref before.Scheme, ref Video, ref Timeout, before.AcSeconds);
                if (ActiveScheme() == before.Scheme) PowerSetActiveScheme(IntPtr.Zero, ref before.Scheme);
                throw;
            }
        }

        public static string Describe(uint seconds)
        {
            if (seconds == 0) return "从不关屏";
            if (seconds % 60 == 0) return (seconds / 60) + " 分钟后关屏";
            return seconds + " 秒后关屏";
        }
    }

    internal sealed class StatusPopup : Form
    {
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                return cp;
            }
        }

        public StatusPopup(string heading, PowerState state, bool preview)
        {
            Text = "关屏时间切换";
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(370, 150);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = preview;
            TopMost = true;
            BackColor = Color.FromArgb(27, 33, 43);
            Padding = new Padding(18);
            Font = new Font("Microsoft YaHei UI", 9F);

            Label title = new Label();
            title.Text = heading;
            title.ForeColor = Color.FromArgb(160, 177, 198);
            title.SetBounds(20, 15, 320, 24);
            Controls.Add(title);

            Label value = new Label();
            value.Text = PowerSettings.Describe(state.AcSeconds);
            value.ForeColor = state.AcSeconds == 0 ? Color.FromArgb(107, 226, 179) : Color.FromArgb(132, 190, 255);
            value.Font = new Font("Microsoft YaHei UI", 19F, FontStyle.Bold);
            value.SetBounds(17, 43, 335, 42);
            Controls.Add(value);

            Label note = new Label();
            note.Text = "仅接通电源 · 电池及睡眠设置保持原样\n提示会自动关闭 · 点击此提示也可关闭";
            note.ForeColor = Color.FromArgb(180, 191, 207);
            note.SetBounds(20, 97, 335, 42);
            Controls.Add(note);
            foreach (Control control in Controls) control.Click += delegate { Close(); };
            Click += delegate { Close(); };
            Shown += delegate
            {
                Rectangle area = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(area.Right - Width - 20, area.Bottom - Height - 20);
                timer.Start();
            };
            timer.Interval = preview ? 12000 : 3200;
            timer.Tick += delegate { Close(); };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }

    internal static class Program
    {
        private static readonly string Folder = AppDomain.CurrentDomain.BaseDirectory;

        private static void SaveState(PowerState state, string action)
        {
            try
            {
                string json = "{\r\n  \"updatedAt\": \"" + DateTimeOffset.Now.ToString("o") +
                    "\",\r\n  \"action\": \"" + action + "\",\r\n  \"scheme\": \"" + state.Scheme +
                    "\",\r\n  \"acSeconds\": " + state.AcSeconds + ",\r\n  \"dcSeconds\": " + state.DcSeconds + "\r\n}\r\n";
                File.WriteAllText(Path.Combine(Folder, "last-state.json"), json, Encoding.UTF8);
                string errorPath = Path.Combine(Folder, "last-error.txt");
                if (File.Exists(errorPath)) File.Delete(errorPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            bool quiet = Array.IndexOf(args, "--quiet") >= 0;
            string mode = "--toggle";
            foreach (string arg in args)
            {
                if (arg == "--quiet") continue;
                if (arg != "--toggle" && arg != "--minute" && arg != "--never" && arg != "--status")
                {
                    if (!quiet) MessageBox.Show("支持参数：--toggle、--minute、--never、--status、--quiet", "关屏时间切换");
                    return 2;
                }
                mode = arg;
            }
            bool created;
            using (Mutex mutex = new Mutex(true, "Local\\ScreenTimeoutToggle.7F5CDE0B", out created))
            {
                if (!created) return 0;
                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    PowerState state = PowerSettings.Read();
                    if (mode != "--status")
                    {
                        uint target = mode == "--minute" ? 60U : mode == "--never" ? 0U : state.AcSeconds == 0 ? 60U : 0U;
                        state = PowerSettings.Set(state, target);
                    }
                    SaveState(state, mode.Substring(2));
                    if (!quiet)
                    {
                        using (StatusPopup popup = new StatusPopup(mode == "--status" ? "当前插电关屏设置" : "已切换插电关屏设置", state, mode == "--status"))
                            Application.Run(popup);
                    }
                    return 0;
                }
                catch (Exception ex)
                {
                    try { File.WriteAllText(Path.Combine(Folder, "last-error.txt"), DateTimeOffset.Now + Environment.NewLine + ex.ToString(), Encoding.UTF8); }
                    catch { }
                    if (!quiet) MessageBox.Show("无法修改关屏时间：\n" + ex.Message + "\n\n如果电脑由单位管理，请检查电源策略权限。", "关屏时间切换", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
