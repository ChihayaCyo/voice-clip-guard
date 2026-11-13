using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Forms;

namespace VoiceClipGuard
{
    static class Program
    {
        private static System.Threading.Mutex _single;

        [STAThread]
        static void Main(string[] args)
        {
            // 单实例：重复双击不会起第二份（两份同时监听会导致重复触发/重复写剪贴板）
            bool createdNew;
            _single = new System.Threading.Mutex(true, "VoiceClipGuard_SingleInstance", out createdNew);
            if (!createdNew)
            {
                MessageBox.Show(
                    "Voice Clip Guard 已经在运行了（看系统托盘）。\n不需要重复启动。",
                    "Voice Clip Guard",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayContext(args));
        }
    }

    /// <summary>
    /// 按键触发式语音兜底：
    ///   - 平时不轮询、不碰剪贴板
    ///   - 监听「整理键」，按下瞬间 + 350ms 各采样一次输入框全文，取更长的版本
    ///   - 剪贴板只保留更长版本，避免「开始说话」按同一键时把已捕获内容冲掉
    /// </summary>
    class TrayContext : ApplicationContext
    {
        // ---------- Win32 ----------
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        // ---------- 状态 ----------
        private readonly NotifyIcon _tray;
        private readonly Timer _pump;
        private readonly string _logDir;
        private readonly string _settingsFile;

        private IntPtr _hook = IntPtr.Zero;
        private LowLevelKeyboardProc _hookProc;

        private volatile bool _enabled = true;
        private volatile bool _learning = false;
        private volatile int _triggerVk = 0;
        private volatile bool _learnSaved = false;

        // 两次采样
        private volatile bool _sample1 = false;
        private volatile bool _sample2 = false;
        private DateTime _triggerAt = DateTime.MinValue;
        private string _roundMax = "";   // 本轮触发已捕获的最长文本（跨触发不比较）

        private string _lastCaptured = "";

        public TrayContext(string[] args)
        {
            _logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "voice-clip-guard");
            Directory.CreateDirectory(_logDir);
            _settingsFile = Path.Combine(_logDir, "settings.json");

            LoadSettings();

            _tray = new NotifyIcon
            {
                Icon = SystemIcons.Information,
                Text = BuildTrayText(),
                Visible = true
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add("学习「整理键」（5 秒内按下你平时按的那个键）", null, delegate { StartLearn(); });
            menu.Items.Add("手动兜底一次（立即复制当前输入框全文）", null, delegate { BeginSamples(); });
            menu.Items.Add("暂停 / 继续", null, delegate { Toggle(); });
            menu.Items.Add("重新复制最近一次兜底内容", null, delegate { RecopyLast(); });
            menu.Items.Add("打开日志目录", null, delegate
            {
                try { Process.Start("explorer.exe", _logDir); } catch { }
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { Quit(); });
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { RecopyLast(); };

            _pump = new Timer();
            _pump.Interval = 20;
            _pump.Tick += OnPump;
            _pump.Start();

            InstallHook();

            LogLine("[start] 按键触发模式 v3（双采样）；triggerVk=" + _triggerVk +
                    (_triggerVk == 0 ? "；尚未学习整理键" : "；已就绪"));
        }

        // ---------- 钩子 ----------
        private void InstallHook()
        {
            _hookProc = HookCallback;
            using (Process cur = Process.GetCurrentProcess())
            using (ProcessModule mod = cur.MainModule)
            {
                _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(mod.ModuleName), 0);
            }
            if (_hook == IntPtr.Zero)
                LogLine("[error] SetWindowsHookEx 失败: " + Marshal.GetLastWin32Error());
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
                {
                    KBDLLHOOKSTRUCT kb = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                    int vk = (int)kb.vkCode;

                    // 纯修饰键不参与学习（但右 Alt 0xA5 允许学习）
                    bool pureModifier = (vk == 0x10 || vk == 0x11 || vk == 0x12 ||
                                         vk == 0x5B || vk == 0x5C ||
                                         vk >= 0xA0 && vk <= 0xA4);
                    if (pureModifier)
                        return CallNextHookEx(_hook, nCode, wParam, lParam);

                    if (_learning)
                    {
                        _triggerVk = vk;
                        _learning = false;
                        _learnSaved = true;
                    }
                    else if (_enabled && _triggerVk != 0 && vk == _triggerVk)
                    {
                        BeginSamples();
                    }
                }
            }
            catch { }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private void BeginSamples()
        {
            _triggerAt = DateTime.Now;
            _roundMax = "";
            _sample1 = true;
            _sample2 = true;
        }

        // ---------- 主线程泵 ----------
        private void OnPump(object sender, EventArgs e)
        {
            try
            {
                if (_learnSaved)
                {
                    _learnSaved = false;
                    SaveSettings();
                    _tray.Text = BuildTrayText();
                    LogLine("[learn] triggerVk=" + _triggerVk);
                    Balloon("已记住整理键：" + VkName(_triggerVk));
                }

                if (_learning && (DateTime.Now - _triggerAt).TotalSeconds > 5 &&
                    _triggerAt != DateTime.MinValue)
                {
                    _learning = false;
                    Balloon("学习超时，请重新点「学习整理键」");
                }

                // 采样 1：按下瞬间 —— 无条件写剪贴板（保证短句也能进）
                if (_sample1)
                {
                    _sample1 = false;
                    DoCapture("t0", true);
                }

                // 采样 2：按下 350ms 后 —— 只有比本轮 t0 更完整才补写
                if (_sample2 && (DateTime.Now - _triggerAt).TotalMilliseconds >= 350)
                {
                    _sample2 = false;
                    DoCapture("t350", false);
                }
            }
            catch { }
        }

        // ---------- 捕获 ----------
        private void DoCapture(string tag, bool force)
        {
            AutomationElement el;
            try { el = AutomationElement.FocusedElement; }
            catch (Exception ex) { LogLine("[" + tag + "] 取焦点失败: " + ex.Message); return; }
            if (el == null) { LogLine("[" + tag + "] 无焦点元素"); return; }

            AutomationElement.AutomationElementInformation info;
            try { info = el.Current; }
            catch { return; }

            if (info.IsPassword) { LogLine("[" + tag + "] 密码框，跳过"); return; }
            if (info.ProcessId == Process.GetCurrentProcess().Id) return;

            string text = ReadFullText(el);
            if (string.IsNullOrWhiteSpace(text) || IsPlaceholder(text))
            {
                LogLine("[" + tag + "] 读不到有效文本（class=" + info.ClassName +
                        " pid=" + info.ProcessId + " 原文长度=" + (text == null ? -1 : text.Length) + "）");
                return;
            }

            // 1) 与上次写入的内容完全相同 → 不重复写剪贴板（避免历史里堆一堆一样的记录）
            if (text == _lastCaptured)
            {
                LogLine("[" + tag + "] 内容同上（" + text.Length + " 字），跳过写入");
                return;
            }

            // 2) 同一轮内，t350 不允许比 t0 短（防止整理把原文改短后覆盖掉原文）
            bool better = force || text.Length > _roundMax.Length;
            if (!better)
            {
                LogLine("[" + tag + "] 本轮已有更长版本（" + _roundMax.Length + " 字），跳过覆盖");
                return;
            }

            _roundMax = text;
            _lastCaptured = text;
            try { Clipboard.SetText(text); } catch (Exception ex) { LogLine("[clipboard] " + ex.Message); }

            try
            {
                string file = Path.Combine(_logDir, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                File.AppendAllText(
                    file,
                    "\n===== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + tag + "]" +
                    " | pid=" + info.ProcessId + " | " + text.Length + " chars =====\n" + text + "\n",
                    new UTF8Encoding(false));
            }
            catch { }

            LogLine("[" + tag + "] capture pid=" + info.ProcessId + " class=" + info.ClassName +
                    " chars=" + text.Length + " | " + Head(text, 50));

            Balloon("已复制当前输入框全文 " + text.Length + " 字 → 剪贴板");
        }

        private string ReadFullText(AutomationElement el)
        {
            object pattern;

            // ValuePattern 优先：它返回的是控件真实内容（空就是空）。
            // 关键：空字符串也直接返回，绝不回退到 TextPattern——否则会拿到 placeholder 占位提示。
            if (el.TryGetCurrentPattern(ValuePattern.Pattern, out pattern))
            {
                try
                {
                    string v = ((ValuePattern)pattern).Current.Value;
                    if (v != null) return v;
                }
                catch { }
            }

            // 仅当控件不支持 ValuePattern 时才用 TextPattern
            if (el.TryGetCurrentPattern(TextPattern.Pattern, out pattern))
            {
                try
                {
                    string t = ((TextPattern)pattern).DocumentRange.GetText(-1);
                    if (!string.IsNullOrEmpty(t)) return CleanTrailing(t);
                }
                catch { }
            }
            return null;
        }

        private static string CleanTrailing(string s)
        {
            return s.TrimEnd('\0', '\r', '\n', '\uFFFF');
        }

        private static bool IsPlaceholder(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string t = s.Trim();
            // 各家输入框的占位提示特征
            if (t.StartsWith("发消息或创建任务")) return true;
            if (t == "输入消息，Enter 发送（Shift+Enter 换行）") return true;
            if (t.StartsWith("输入消息，Enter 发送")) return true;
            if (t.StartsWith("搜索")) return false; // “搜索”单独出现不算，避免误杀
            return false;
        }

        // ---------- 菜单动作 ----------
        private void StartLearn()
        {
            _learning = true;
            _triggerAt = DateTime.Now;
            Balloon("请在 5 秒内按下你平时按的「整理键」");
            LogLine("[learn] 进入学习模式");
        }

        private void Toggle()
        {
            _enabled = !_enabled;
            _tray.Text = BuildTrayText();
            LogLine("[toggle] enabled=" + _enabled);
        }

        private void RecopyLast()
        {
            if (_lastCaptured.Length == 0) return;
            try
            {
                Clipboard.SetText(_lastCaptured);
                Balloon("已重新复制 " + _lastCaptured.Length + " 字");
            }
            catch { }
        }

        private void Quit()
        {
            try { _pump.Stop(); } catch { }
            try { if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook); } catch { }
            try { _tray.Visible = false; _tray.Dispose(); } catch { }
            LogLine("[exit]");
            ExitThread();
        }

        // ---------- 设置 ----------
        private void LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsFile))
                {
                    string json = File.ReadAllText(_settingsFile, Encoding.UTF8);
                    int idx = json.IndexOf("\"triggerVk\"", StringComparison.Ordinal);
                    if (idx >= 0)
                    {
                        int colon = json.IndexOf(':', idx);
                        int end = json.IndexOfAny(new[] { ',', '}' }, colon + 1);
                        int v;
                        if (colon > 0 && end > colon &&
                            int.TryParse(json.Substring(colon + 1, end - colon - 1).Trim(), out v))
                        {
                            _triggerVk = v;
                        }
                    }
                }
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                File.WriteAllText(_settingsFile, "{\n  \"triggerVk\": " + _triggerVk + "\n}\n", new UTF8Encoding(false));
            }
            catch { }
        }

        // ---------- 杂项 ----------
        private string BuildTrayText()
        {
            if (!_enabled) return "Voice Clip Guard（已暂停）";
            if (_triggerVk == 0) return "Voice Clip Guard（待学习整理键）";
            return "Voice Clip Guard（整理键 " + VkName(_triggerVk) + "）";
        }

        private static string VkName(int vk)
        {
            try { return ((Keys)vk).ToString(); }
            catch { return "0x" + vk.ToString("X2"); }
        }

        private void Balloon(string msg)
        {
            try { _tray.ShowBalloonTip(1500, "Voice Clip Guard", msg, ToolTipIcon.Info); } catch { }
        }

        private void LogLine(string s)
        {
            try
            {
                string file = Path.Combine(_logDir, "guard.log");
                File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + s + "\n", new UTF8Encoding(false));
            }
            catch { }
        }

        private static string Head(string s, int n)
        {
            return s.Length <= n ? s : s.Substring(0, n) + "...";
        }
    }
}
