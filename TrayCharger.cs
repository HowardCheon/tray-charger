/*
 * TrayCharger - 시스템 트레이 배터리 충전전류 모니터
 * Windows 11 노트북용. C#/.NET Framework (별도 설치 불필요)
 * 빌드: build.bat
 */
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

class TrayCharger : ApplicationContext
{
    const string APP_NAME = "TrayCharger";
    const int ICON_SIZE = 256;
    const int REFRESH_MS = 3000;

    NotifyIcon _tray;
    System.Windows.Forms.Timer _timer;
    string _batteryPath;

    // ═══════════════════════════════════════════════════════
    //  P/Invoke
    // ═══════════════════════════════════════════════════════

    static readonly IntPtr INVALID_HANDLE = new IntPtr(-1);
    static readonly Guid GUID_BATTERY =
        new Guid("72631E54-78A4-11D0-BCF7-00AA00B7B32A");

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_BATTERY_STATE
    {
        public byte AcOnLine, BatteryPresent, Charging, Discharging;
        public byte _s0, _s1, _s2, _s3;          // Spare1[3] + Tag
        public uint MaxCapacity, RemainingCapacity;
        public int  Rate;                          // milliwatts (+충전 / -방전)
        public uint EstimatedTime, DefaultAlert1, DefaultAlert2;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BATTERY_WAIT_STATUS
    {
        public uint BatteryTag, Timeout, PowerState, LowCapacity, HighCapacity;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BATTERY_STATUS
    {
        public uint PowerState, Capacity;
        public uint Voltage;   // millivolts
        public int  Rate;      // milliwatts
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("powrprof.dll")]
    static extern uint CallNtPowerInformation(
        int level, IntPtr inBuf, uint inSize,
        out SYSTEM_BATTERY_STATE outBuf, uint outSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
    static extern IntPtr SetupDiGetClassDevs(
        ref Guid guid, IntPtr enumerator, IntPtr hwnd, uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
    static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr hDev, IntPtr devInfo, ref Guid guid, uint idx,
        ref SP_DEVICE_INTERFACE_DATA ifData);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr hDev, ref SP_DEVICE_INTERFACE_DATA ifData,
        IntPtr detailBuf, uint detailSize,
        out uint reqSize, IntPtr devInfoOut);

    [DllImport("setupapi.dll")]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr hDev);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern IntPtr CreateFile(
        string path, uint access, uint share,
        IntPtr sec, uint disp, uint flags, IntPtr tmpl);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(
        IntPtr h, uint code,
        ref uint inBuf, uint inSz, ref uint outBuf, uint outSz,
        ref uint ret, IntPtr ovlp);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(
        IntPtr h, uint code,
        ref BATTERY_WAIT_STATUS inBuf, uint inSz,
        ref BATTERY_STATUS outBuf, uint outSz,
        ref uint ret, IntPtr ovlp);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    const uint DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
    const uint IOCTL_BATTERY_QUERY_TAG    = 0x294040;
    const uint IOCTL_BATTERY_QUERY_STATUS = 0x29404C;
    const uint GENERIC_RW = 0xC0000000, FILE_SHARE_RW = 3, OPEN_EXISTING = 3;

    // ═══════════════════════════════════════════════════════
    //  Battery Reading
    // ═══════════════════════════════════════════════════════

    struct Reading
    {
        public bool Ok, AcOn, IsCharging;
        public float Watts, Volts, Amps, Percent;
    }

    string FindBatteryPath()
    {
        Guid g = GUID_BATTERY;
        IntPtr hDev = SetupDiGetClassDevs(
            ref g, IntPtr.Zero, IntPtr.Zero,
            DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (hDev == INVALID_HANDLE) return null;

        try
        {
            var did = new SP_DEVICE_INTERFACE_DATA();
            did.cbSize = (uint)Marshal.SizeOf(did);
            if (!SetupDiEnumDeviceInterfaces(
                    hDev, IntPtr.Zero, ref g, 0, ref did))
                return null;

            uint reqSize;
            SetupDiGetDeviceInterfaceDetail(
                hDev, ref did, IntPtr.Zero, 0, out reqSize, IntPtr.Zero);

            IntPtr buf = Marshal.AllocHGlobal((int)reqSize);
            try
            {
                Marshal.WriteInt32(buf,
                    IntPtr.Size == 8 ? 8 : (4 + Marshal.SystemDefaultCharSize));
                if (!SetupDiGetDeviceInterfaceDetail(
                        hDev, ref did, buf, reqSize, out reqSize, IntPtr.Zero))
                    return null;
                return Marshal.PtrToStringAuto(
                    new IntPtr(buf.ToInt64() + 4));
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { SetupDiDestroyDeviceInfoList(hDev); }
    }

    float ReadVoltage()
    {
        if (_batteryPath == null)
        {
            _batteryPath = FindBatteryPath();
            if (_batteryPath == null) return 0;
        }

        IntPtr h = CreateFile(_batteryPath, GENERIC_RW,
            FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == INVALID_HANDLE) return 0;

        try
        {
            uint tag = 0, timeout = 0, br = 0;
            if (!DeviceIoControl(h, IOCTL_BATTERY_QUERY_TAG,
                    ref timeout, 4, ref tag, 4, ref br, IntPtr.Zero))
                return 0;

            var bws = new BATTERY_WAIT_STATUS { BatteryTag = tag };
            var bs  = new BATTERY_STATUS();
            if (!DeviceIoControl(h, IOCTL_BATTERY_QUERY_STATUS,
                    ref bws, (uint)Marshal.SizeOf(bws),
                    ref bs,  (uint)Marshal.SizeOf(bs),
                    ref br, IntPtr.Zero))
                return 0;

            return bs.Voltage / 1000f;   // mV → V
        }
        finally { CloseHandle(h); }
    }

    Reading ReadBattery()
    {
        var r = new Reading();
        SYSTEM_BATTERY_STATE sbs;
        if (CallNtPowerInformation(5, IntPtr.Zero, 0,
                out sbs, (uint)Marshal.SizeOf(typeof(SYSTEM_BATTERY_STATE))) != 0)
            return r;
        if (sbs.BatteryPresent == 0) return r;

        r.Ok   = true;
        r.AcOn = sbs.AcOnLine != 0;
        r.Watts = sbs.Rate / 1000f;
        r.IsCharging = sbs.Rate > 0 || (sbs.Rate == 0 && r.AcOn);

        if (sbs.MaxCapacity > 0)
            r.Percent = (float)sbs.RemainingCapacity / sbs.MaxCapacity * 100;

        r.Volts = ReadVoltage();
        if (r.Volts > 0)
            r.Amps = Math.Abs(r.Watts) / r.Volts;

        return r;
    }

    // ═══════════════════════════════════════════════════════
    //  Icon
    // ═══════════════════════════════════════════════════════

    Icon MakeIcon(float value, bool isCharging, bool isAmps)
    {
        // 텍스트 결정 (A 소수점 한자리)
        string text;
        if (value < 0.05f)
            text = "0";
        else if (value >= 10f)
            text = ((int)Math.Round(value)).ToString();
        else
            text = value.ToString("0.0");

        if (!isCharging && value >= 0.05f)
            text = "-" + text;

        Color fg = isCharging
            ? Color.Black
            : Color.FromArgb(235, 30, 30);

        using (var bmp = new Bitmap(ICON_SIZE, ICON_SIZE,
                   System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode     = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                using (var ff = new FontFamily("Arial"))
                using (var path = new GraphicsPath())
                {
                    // Auto-fit: 큰 emSize로 시작 → 아이콘에 맞게 축소
                    float em = 220f;
                    path.AddString(text, ff, (int)FontStyle.Bold, em,
                        Point.Empty, StringFormat.GenericTypographic);
                    RectangleF b = path.GetBounds();
                    float target = ICON_SIZE - 2;
                    float maxDim = Math.Max(b.Width, b.Height);
                    if (maxDim > 0 && maxDim > target)
                    {
                        path.Reset();
                        em *= target / maxDim;
                        path.AddString(text, ff, (int)FontStyle.Bold, em,
                            Point.Empty, StringFormat.GenericTypographic);
                        b = path.GetBounds();
                    }

                    // 중앙 정렬
                    float dx = (ICON_SIZE - b.Width)  / 2 - b.X;
                    float dy = (ICON_SIZE - b.Height) / 2 - b.Y;
                    using (var m = new Matrix()) { m.Translate(dx, dy); path.Transform(m); }

                    // 흰색 외곽선 → 다크/라이트 테마 모두 가독
                    using (var pen = new Pen(Color.White, 3) { LineJoin = LineJoin.Round })
                        g.DrawPath(pen, path);
                    using (var brush = new SolidBrush(fg))
                        g.FillPath(brush, path);
                }
            }

            // Icon 생성 (alpha 보존, 메모리 누수 방지)
            IntPtr hIcon = bmp.GetHicon();
            Icon tmp    = Icon.FromHandle(hIcon);
            Icon result = (Icon)tmp.Clone();
            tmp.Dispose();
            DestroyIcon(hIcon);
            return result;
        }
    }

    // ═══════════════════════════════════════════════════════
    //  Auto-Start
    // ═══════════════════════════════════════════════════════

    const string REG_RUN = @"Software\Microsoft\Windows\CurrentVersion\Run";

    static bool IsAutoStart()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(REG_RUN, false))
                return k != null && k.GetValue(APP_NAME) != null;
        }
        catch { return false; }
    }

    static void ToggleAutoStart()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(REG_RUN, true))
        {
            if (k == null) return;
            if (IsAutoStart())
                k.DeleteValue(APP_NAME, false);
            else
                k.SetValue(APP_NAME,
                    "\"" + Application.ExecutablePath + "\"");
        }
    }

    // ═══════════════════════════════════════════════════════
    //  UI
    // ═══════════════════════════════════════════════════════

    TrayCharger()
    {
        _batteryPath = FindBatteryPath();

        var menu = new ContextMenuStrip();

        var autoItem = new ToolStripMenuItem("시작시 자동 실행");
        autoItem.Checked = IsAutoStart();
        autoItem.Click += (s, e) =>
        {
            ToggleAutoStart();
            autoItem.Checked = IsAutoStart();
        };
        menu.Items.Add(autoItem);

        menu.Items.Add("트레이에 항상 표시 (설정)", null,
            (s, e) => Process.Start("ms-settings:taskbar"));

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("종료", null, (s, e) =>
        {
            _tray.Visible = false;
            Application.Exit();
        });

        _tray = new NotifyIcon
        {
            Icon = MakeIcon(0, true, true),
            Text = APP_NAME,
            Visible = true,
            ContextMenuStrip = menu
        };

        _timer = new System.Windows.Forms.Timer { Interval = REFRESH_MS };
        _timer.Tick += OnTick;
        _timer.Start();
        OnTick(null, null);
    }

    void OnTick(object sender, EventArgs e)
    {
        Reading r = ReadBattery();
        Icon newIcon;
        string tip;

        if (r.Ok)
        {
            string status = r.IsCharging ? "충전" : "방전";
            if (r.Volts > 0)
            {
                float mA = r.Amps * 1000f;
                newIcon = MakeIcon(r.Amps, r.IsCharging, true);
                tip = string.Format(
                    "{0}: {1:0}mA ({2:0.0}W)\n전압: {3:0.0}V\n배터리: {4:0}%\nAC: {5}",
                    status, mA, Math.Abs(r.Watts), r.Volts,
                    r.Percent, r.AcOn ? "연결" : "분리");
            }
            else
            {
                // 전압 읽기 실패 → W 표시 fallback
                newIcon = MakeIcon(Math.Abs(r.Watts), r.IsCharging, false);
                tip = string.Format(
                    "{0}: {1:0.0}W\n배터리: {2:0}%\nAC: {3}",
                    status, Math.Abs(r.Watts),
                    r.Percent, r.AcOn ? "연결" : "분리");
            }
        }
        else
        {
            newIcon = MakeIcon(0, true, true);
            tip = "배터리 없음";
        }

        Icon old = _tray.Icon;
        _tray.Icon = newIcon;
        _tray.Text = tip.Length > 127 ? tip.Substring(0, 127) : tip;
        if (old != null) old.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_timer != null) { _timer.Stop(); _timer.Dispose(); }
            if (_tray != null) _tray.Dispose();
        }
        base.Dispose(disposing);
    }

    // ═══════════════════════════════════════════════════════
    //  Entry
    // ═══════════════════════════════════════════════════════

    [STAThread]
    static void Main()
    {
        bool created;
        using (var mtx = new Mutex(true, APP_NAME + "_Mtx", out created))
        {
            if (!created) return;            // 이미 실행중이면 종료
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayCharger());
        }
    }
}
