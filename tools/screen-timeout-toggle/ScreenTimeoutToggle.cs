using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml;

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

    public sealed class MonitorBrightness
    {
        public string InstanceName;
        public byte Current;
        public byte Minimum;
    }

    public sealed class BrightnessResult
    {
        public string Message;
        public bool Warning;
        public BrightnessResult(string message, bool warning) { Message = message; Warning = warning; }
    }

    public static class BrightnessSettings
    {
        public static readonly string BackupPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "brightness-backup.xml");

        public static List<MonitorBrightness> Read()
        {
            List<MonitorBrightness> result = new List<MonitorBrightness>();
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("root\\wmi", "SELECT * FROM WmiMonitorBrightness WHERE Active=TRUE"))
            using (ManagementObjectCollection monitors = searcher.Get())
            {
                foreach (ManagementObject monitor in monitors)
                using (monitor)
                {
                    byte[] levels = (byte[])monitor["Level"];
                    if (levels == null || levels.Length == 0) continue;
                    byte minimum = levels[0];
                    foreach (byte level in levels) if (level < minimum) minimum = level;
                    result.Add(new MonitorBrightness { InstanceName = (string)monitor["InstanceName"], Current = (byte)monitor["CurrentBrightness"], Minimum = minimum });
                }
            }
            return result;
        }

        private static MonitorBrightness Find(List<MonitorBrightness> monitors, string instance)
        {
            return monitors.Find(delegate(MonitorBrightness monitor) { return String.Equals(monitor.InstanceName, instance, StringComparison.OrdinalIgnoreCase); });
        }

        private static void SetOne(string instance, byte value)
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("root\\wmi", "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active=TRUE"))
            using (ManagementObjectCollection methods = searcher.Get())
            {
                foreach (ManagementObject method in methods)
                using (method)
                {
                    if (!String.Equals((string)method["InstanceName"], instance, StringComparison.OrdinalIgnoreCase)) continue;
                    using (ManagementBaseObject parameters = method.GetMethodParameters("WmiSetBrightness"))
                    {
                        parameters["Timeout"] = 0U;
                        parameters["Brightness"] = value;
                        using (ManagementBaseObject output = method.InvokeMethod("WmiSetBrightness", parameters, null))
                        {
                            // Some laptop providers implement this as a void method.
                            // Always verify the real brightness below, even without a return value.
                            if (output != null && output.Properties["ReturnValue"] != null)
                            {
                                uint code = Convert.ToUInt32(output["ReturnValue"]);
                                if (code != 0) throw new Win32Exception((int)code, "显示器拒绝调整亮度。");
                            }
                        }
                    }
                    for (int attempt = 0; attempt < 12; attempt++)
                    {
                        MonitorBrightness actual = Find(Read(), instance);
                        if (actual != null && actual.Current == value) return;
                        Thread.Sleep(100);
                    }
                    throw new InvalidOperationException("亮度未达到目标值，请检查显示器或自动亮度设置。");
                }
            }
            throw new InvalidOperationException("未找到对应屏幕的亮度控制接口。");
        }

        private static void Apply(List<MonitorBrightness> before, List<MonitorBrightness> targets)
        {
            try
            {
                foreach (MonitorBrightness target in targets) SetOne(target.InstanceName, target.Current);
            }
            catch
            {
                // An unsuccessful multi-monitor operation must not leave some screens dimmed.
                foreach (MonitorBrightness monitor in before)
                {
                    if (Find(targets, monitor.InstanceName) == null) continue;
                    try { SetOne(monitor.InstanceName, monitor.Current); } catch { }
                }
                throw;
            }
        }

        private static List<MonitorBrightness> LoadBackup()
        {
            List<MonitorBrightness> result = new List<MonitorBrightness>();
            if (!File.Exists(BackupPath)) return result;
            XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using (XmlReader reader = XmlReader.Create(BackupPath, settings))
            {
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element || reader.Name != "monitor") continue;
                    string instance = reader.GetAttribute("instance");
                    byte value;
                    if (String.IsNullOrEmpty(instance) || !Byte.TryParse(reader.GetAttribute("value"), out value) || value > 100)
                        throw new InvalidDataException("保存的亮度记录无效，已保留记录供检查。");
                    result.Add(new MonitorBrightness { InstanceName = instance, Current = value });
                }
            }
            return result;
        }

        private static void SaveBackup(List<MonitorBrightness> monitors)
        {
            string temporaryPath = BackupPath + ".tmp";
            XmlWriterSettings settings = new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 };
            using (XmlWriter writer = XmlWriter.Create(temporaryPath, settings))
            {
                writer.WriteStartElement("brightnessBackup");
                foreach (MonitorBrightness monitor in monitors)
                {
                    writer.WriteStartElement("monitor");
                    writer.WriteAttributeString("instance", monitor.InstanceName);
                    writer.WriteAttributeString("value", monitor.Current.ToString());
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }
            if (File.Exists(BackupPath)) File.Replace(temporaryPath, BackupPath, null);
            else File.Move(temporaryPath, BackupPath);
        }

        public static BrightnessResult Update(bool dim, bool statusOnly)
        {
            try
            {
                List<MonitorBrightness> current = Read();
                if (current.Count == 0) return new BrightnessResult("未检测到支持亮度控制的屏幕", true);
                if (statusOnly) return new BrightnessResult("当前亮度：" + current[0].Current + "%" + (current.Count > 1 ? "（首块屏幕）" : ""), false);
                List<MonitorBrightness> backup = LoadBackup();
                if (dim)
                {
                    List<MonitorBrightness> targets = new List<MonitorBrightness>();
                    foreach (MonitorBrightness monitor in current)
                    {
                        // Keep the first original value across repeated --minute invocations.
                        if (Find(backup, monitor.InstanceName) == null)
                            backup.Add(new MonitorBrightness { InstanceName = monitor.InstanceName, Current = monitor.Current });
                        targets.Add(new MonitorBrightness { InstanceName = monitor.InstanceName, Current = monitor.Minimum });
                    }
                    // Persist the restoration value before changing the hardware.
                    SaveBackup(backup);
                    Apply(current, targets);
                    return new BrightnessResult("亮度已降至最低：" + current[0].Minimum + "%" + (current.Count > 1 ? "（多屏已联动）" : ""), false);
                }
                if (backup.Count == 0) return new BrightnessResult("没有待恢复的亮度，保持当前 " + current[0].Current + "%", false);
                List<MonitorBrightness> restore = new List<MonitorBrightness>();
                List<MonitorBrightness> remaining = new List<MonitorBrightness>();
                foreach (MonitorBrightness monitor in backup)
                {
                    if (Find(current, monitor.InstanceName) != null) restore.Add(monitor);
                    else remaining.Add(monitor);
                }
                Apply(current, restore);
                if (remaining.Count == 0) File.Delete(BackupPath);
                else SaveBackup(remaining);
                if (restore.Count == 0) return new BrightnessResult("原屏幕暂未连接，已保留亮度恢复记录", true);
                return new BrightnessResult("已恢复原亮度：" + restore[0].Current + "%" + (remaining.Count > 0 ? "（部分屏幕待连接）" : ""), remaining.Count > 0);
            }
            catch (Exception ex)
            {
                // Timeout switching remains available on unsupported monitors or driver failures.
                return new BrightnessResult("亮度未完成：" + ex.Message, true);
            }
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

        public StatusPopup(string heading, PowerState state, BrightnessResult brightness, bool preview)
        {
            Text = "关屏时间切换";
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(410, 190);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = preview;
            TopMost = true;
            BackColor = Color.FromArgb(27, 33, 43);
            Padding = new Padding(18);
            Font = new Font("Microsoft YaHei UI", 9F);

            Label title = new Label();
            title.Text = heading;
            title.ForeColor = Color.FromArgb(160, 177, 198);
            title.SetBounds(20, 15, 370, 24);
            Controls.Add(title);

            Label value = new Label();
            value.Text = PowerSettings.Describe(state.AcSeconds);
            value.ForeColor = state.AcSeconds == 0 ? Color.FromArgb(107, 226, 179) : Color.FromArgb(132, 190, 255);
            value.Font = new Font("Microsoft YaHei UI", 19F, FontStyle.Bold);
            value.SetBounds(17, 43, 335, 42);
            Controls.Add(value);

            Label brightnessLabel = new Label();
            brightnessLabel.Text = brightness.Message;
            brightnessLabel.ForeColor = brightness.Warning ? Color.FromArgb(255, 203, 145) : Color.FromArgb(190, 220, 235);
            brightnessLabel.SetBounds(20, 91, 370, 42);
            Controls.Add(brightnessLabel);

            Label note = new Label();
            note.Text = "插电关屏计时 + 屏幕亮度联动\n电池关屏与睡眠时间保持原样 · 点击关闭";
            note.ForeColor = Color.FromArgb(180, 191, 207);
            note.SetBounds(20, 140, 370, 42);
            Controls.Add(note);
            foreach (Control control in Controls) control.Click += delegate { Close(); };
            Click += delegate { Close(); };
            Shown += delegate
            {
                Rectangle area = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(area.Right - Width - 20, area.Bottom - Height - 20);
                timer.Start();
            };
            timer.Interval = preview ? 12000 : brightness.Warning ? 7000 : 3200;
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

        private static string JsonString(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }

        private static void SaveState(PowerState state, string action, BrightnessResult brightness)
        {
            try
            {
                string json = "{\r\n  \"updatedAt\": \"" + DateTimeOffset.Now.ToString("o") +
                    "\",\r\n  \"action\": \"" + action + "\",\r\n  \"scheme\": \"" + state.Scheme +
                    "\",\r\n  \"acSeconds\": " + state.AcSeconds + ",\r\n  \"dcSeconds\": " + state.DcSeconds +
                    ",\r\n  \"brightnessMessage\": \"" + JsonString(brightness.Message) + "\",\r\n  \"brightnessWarning\": " + (brightness.Warning ? "true" : "false") + "\r\n}\r\n";
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
                    BrightnessResult brightness = BrightnessSettings.Update(state.AcSeconds == 60, mode == "--status");
                    SaveState(state, mode.Substring(2), brightness);
                    if (!quiet)
                    {
                        using (StatusPopup popup = new StatusPopup(mode == "--status" ? "当前关屏与亮度设置" : "已切换关屏与亮度设置", state, brightness, mode == "--status"))
                            Application.Run(popup);
                    }
                    return brightness.Warning && mode != "--status" ? 3 : 0;
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
