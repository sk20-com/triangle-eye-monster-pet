using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TriangleEyeMonsterPet
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new PetForm());
        }
    }

    /// <summary>
    /// 三角独眼怪桌宠主窗体：无边框透明置顶，自研拖拽/点击/待机状态机。
    /// 渲染采用真 alpha 混合（WS_EX_LAYERED + UpdateLayeredWindow 提交 32bpp ARGB 位图），
    /// 彻底消除品红透明键抠色带来的紫边（PNG 半透明边缘不再与品红背景混色）。
    /// 交互逻辑：以"按下位移阈值"区分拖拽与点击；单击/双击/三击均为贴地反应。
    /// </summary>
    internal class PetForm : Form
    {
        private const int FrameSize = 150;      // 帧尺寸（素材 150x150）
        private const int TickMs = 70;          // 主循环 tick 间隔（约 14fps）
        private const int ClickWindowMs = 380;  // 连续点击判定窗口
        private const int DragThreshold = 6;    // 位移超过该像素视为拖拽
        private const int TotalFrames = 84;     // 帧总数

        // --- Layered 窗口常量 ---
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int ULW_ALPHA = 0x00000002;
        private const byte AC_SOURCE_OVER = 0;
        private const byte AC_SOURCE_ALPHA = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Point pptDst, ref Size psize, IntPtr hdcSrc, ref Point pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hDC);
        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        private readonly Bitmap[] _frames = new Bitmap[TotalFrames + 1];
        private readonly Random _rnd = new Random();
        private readonly System.Windows.Forms.Timer _tickTimer;
        private readonly System.Windows.Forms.Timer _clickTimer;
        private readonly ContextMenuStrip _menu;

        private enum PetState { Idle, React, Shiver, Run, Walk, Hover }
        private PetState _state = PetState.Idle;

        // 动画播放
        private int _frameIndex = 1;             // 当前显示帧
        private int _stateTick = 0;              // 当前状态已运行 tick 数
        private int _actionTotalTicks = 0;       // 当前动作总时长（tick）
        private int[] _actionFrames;             // 当前动作帧序列
        private int _framesPerTick = 1;          // 每 tick 前进帧数（循环用）

        // 待机随机
        private int _idleCountdown = 0;          // 距下次随机动作的 tick 数
        private int _hoverCooldown = 0;          // 悬停反应冷却（tick）

        // 点击 / 拖拽
        private int _clickCount = 0;
        private DateTime _lastClickTime = DateTime.MinValue;
        private bool _mouseDown = false;
        private bool _dragging = false;
        private Point _dragStartScreen;
        private Point _dragStartLocation;

        // 走动 / 逃跑
        private int _walkDir = 1;
        private int _groundY;

        public PetForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.Black;
            // 不使用 TransparencyKey 抠色，紫边根因已由真 alpha 渲染消除
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(FrameSize, FrameSize);
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            _groundY = wa.Bottom - FrameSize;
            int initX = wa.Left + _rnd.Next(Math.Max(1, wa.Width - FrameSize));
            Location = new Point(initX, _groundY);

            LoadFrames();

            // 右键菜单（layered 窗口下手动弹出，不依赖 ContextMenuStrip 属性自动触发）
            _menu = new ContextMenuStrip();
            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出桌宠");
            exitItem.Click += delegate { Application.Exit(); };
            _menu.Items.Add(exitItem);

            _tickTimer = new System.Windows.Forms.Timer();
            _tickTimer.Interval = TickMs;
            _tickTimer.Tick += OnTick;
            _tickTimer.Start();

            _clickTimer = new System.Windows.Forms.Timer();
            _clickTimer.Interval = ClickWindowMs;
            _clickTimer.Tick += OnClickTimeout;
        }

        // ---------- Layered 窗口渲染 ----------
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RenderLayered();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RenderLayered();
        }

        /// <summary>
        /// 把当前帧绘制到 32bpp ARGB 位图后，经 UpdateLayeredWindow 提交。
        /// 位图本身保留 PNG 的逐像素 alpha，Windows 按真 alpha 与桌面混合，无品红抠色。
        /// </summary>
        private void RenderLayered()
        {
            if (Handle == IntPtr.Zero || _frames[1] == null)
                return;

            using (Bitmap bmp = new Bitmap(FrameSize, FrameSize, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.DrawImageUnscaled(Frame(_frameIndex), 0, 0);
                }

                IntPtr hdcScreen = GetDC(IntPtr.Zero);
                IntPtr hdcMem = CreateCompatibleDC(hdcScreen);
                IntPtr hBitmap = IntPtr.Zero;
                try
                {
                    // 带 Color.FromArgb(0) 参数创建 HBITMAP 时保留 alpha 通道
                    hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
                    SelectObject(hdcMem, hBitmap);

                    BLENDFUNCTION blend = new BLENDFUNCTION();
                    blend.BlendOp = AC_SOURCE_OVER;
                    blend.SourceConstantAlpha = 255;
                    blend.AlphaFormat = AC_SOURCE_ALPHA;

                    Point dst = Location;
                    Size sz = bmp.Size;
                    Point src = Point.Empty;
                    UpdateLayeredWindow(Handle, hdcScreen, ref dst, ref sz, hdcMem, ref src, 0, ref blend, ULW_ALPHA);
                }
                finally
                {
                    if (hBitmap != IntPtr.Zero)
                        DeleteObject(hBitmap);
                    DeleteDC(hdcMem);
                    ReleaseDC(IntPtr.Zero, hdcScreen);
                }
            }
        }

        // ---------- 帧加载（缺帧兜底，不崩溃） ----------
        private void LoadFrames()
        {
            string dir = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "img");
            int loaded = 0;
            for (int i = 1; i <= TotalFrames; i++)
            {
                try
                {
                    string path = Path.Combine(dir, "shime" + i + ".png");
                    if (File.Exists(path))
                    {
                        _frames[i] = new Bitmap(path);
                        loaded++;
                    }
                }
                catch (Exception)
                {
                    _frames[i] = null;
                }
            }

            if (loaded == 0)
            {
                // 完全无帧：画透明底占位图，保证程序不崩溃、可交互
                Bitmap placeholder = new Bitmap(FrameSize, FrameSize, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(placeholder))
                {
                    g.Clear(Color.Transparent);
                    g.DrawEllipse(new Pen(Color.Red, 3), 20, 20, 110, 110);
                    g.DrawString("PET", new Font("Arial", 18, FontStyle.Bold), Brushes.Red, 40, 58);
                }
                for (int i = 1; i <= TotalFrames; i++)
                    _frames[i] = placeholder;
            }
            else
            {
                // 缺帧用最近的有效帧补位
                Bitmap last = _frames[1];
                if (last == null) last = FindFirstLoaded();
                for (int i = 1; i <= TotalFrames; i++)
                {
                    if (_frames[i] == null)
                        _frames[i] = last;
                    else
                        last = _frames[i];
                }
            }
        }

        private Bitmap FindFirstLoaded()
        {
            for (int i = 1; i <= TotalFrames; i++)
                if (_frames[i] != null) return _frames[i];
            return null;
        }

        private Bitmap Frame(int index)
        {
            if (index < 1 || index > TotalFrames || _frames[index] == null)
                return _frames[1];
            return _frames[index];
        }

        // ---------- 动作播放辅助 ----------
        /// <summary>设置一个帧序列动作：frames 循环播放，totalTicks 后回到 Idle。</summary>
        private void SetAction(int[] frames, int totalTicks, int framesPerTick)
        {
            _actionFrames = frames;
            _actionTotalTicks = totalTicks;
            _framesPerTick = framesPerTick;
            _stateTick = 0;
        }

        /// <summary>根据 tick 计算当前应显示的帧序号（循环）。</summary>
        private int CurrentActionFrame()
        {
            if (_actionFrames == null || _actionFrames.Length == 0)
                return 1;
            int pos = (_stateTick / _framesPerTick) % _actionFrames.Length;
            return _actionFrames[pos];
        }

        private void GotoIdle()
        {
            _state = PetState.Idle;
            _actionFrames = null;
            _frameIndex = 1;
        }

        private void SnapToGround()
        {
            if (Top < _groundY) Top = _groundY;
        }

        // ---------- 主循环 ----------
        private void OnTick(object sender, EventArgs e)
        {
            if (_hoverCooldown > 0) _hoverCooldown--;

            switch (_state)
            {
                case PetState.Idle: TickIdle(); break;
                case PetState.React: TickGenericAction(); break;
                case PetState.Shiver: TickShiver(); break;
                case PetState.Run: TickRun(); break;
                case PetState.Walk: TickWalk(); break;
                case PetState.Hover: TickGenericAction(); break;
            }
            RenderLayered();
        }

        // 待机：贴地、随机小动作、随机走动
        private void TickIdle()
        {
            // 缓慢回落贴地（被拖到高处后自然下落）
            if (Top < _groundY)
            {
                Top += 2;
                if (Top > _groundY) Top = _groundY;
            }

            if (_idleCountdown > 0)
            {
                _idleCountdown--;
                _frameIndex = 1;
                return;
            }

            // 触发随机待机行为
            _idleCountdown = _rnd.Next(35, 90); // 2.5s ~ 6.3s
            if (_rnd.Next(100) < 22)
            {
                // 随机走动
                StartWalk();
            }
            else
            {
                // 随机小动作
                StartRandomIdleReact();
            }
        }

        private void StartRandomIdleReact()
        {
            int kind = _rnd.Next(7);
            int[] seq;
            switch (kind)
            {
                case 0: seq = new int[] { 1, 45, 1, 45, 1 }; break;            // 眨眼
                case 1: seq = new int[] { 2, 20, 2 }; break;                   // 歪头
                case 2: seq = new int[] { 1, 3, 1, 3, 1 }; break;              // 点头
                case 3: seq = new int[] { 20, 21, 20, 21, 20 }; break;         // 摇摆
                case 4: seq = new int[] { 26, 45, 26 }; break;                 // 缩身
                case 5: seq = new int[] { 40, 84, 40 }; break;                 // 环顾
                default: seq = new int[] { 50, 1, 50, 1 }; break;              // 伸懒腰/揉眼
            }
            _state = PetState.React;
            SetAction(seq, seq.Length * 2, 2);
        }

        private void StartWalk()
        {
            _walkDir = (_rnd.Next(2) == 0) ? -1 : 1;
            SnapToGround();
            _state = PetState.Walk;
            SetAction(new int[] { 1, 2, 3 }, _rnd.Next(18, 45), 1);
        }

        private void TickWalk()
        {
            _stateTick++;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int nx = Left + _walkDir * 2;
            if (nx <= wa.Left) { nx = wa.Left; _walkDir = 1; }
            if (nx + FrameSize >= wa.Right) { nx = wa.Right - FrameSize; _walkDir = -1; }
            Left = nx;
            SnapToGround();
            _frameIndex = CurrentActionFrame();
            if (_stateTick >= _actionTotalTicks)
                GotoIdle();
        }

        // 单击/悬停等短动作
        private void TickGenericAction()
        {
            _stateTick++;
            _frameIndex = CurrentActionFrame();
            if (_stateTick >= _actionTotalTicks)
                GotoIdle();
        }

        // 双击发抖：77-80 循环两轮 + 缩身收尾
        private void TickShiver()
        {
            _stateTick++;
            if (_stateTick < 16)
            {
                int[] shiver = new int[] { 77, 78, 79, 80 };
                _frameIndex = shiver[(_stateTick / 1) % shiver.Length];
            }
            else if (_stateTick < 19)
            {
                _frameIndex = 45;
            }
            else
            {
                GotoIdle();
            }
        }

        // 三击逃跑：贴地横跑，远离鼠标方向
        private void TickRun()
        {
            _stateTick++;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int nx = Left + _walkDir * 5;
            if (nx <= wa.Left || nx + FrameSize >= wa.Right)
            {
                Left = Math.Max(wa.Left, Math.Min(nx, wa.Right - FrameSize));
                GotoIdle();
                return;
            }
            Left = nx;
            SnapToGround();
            _frameIndex = CurrentActionFrame();
            if (_stateTick >= _actionTotalTicks)
                GotoIdle();
        }

        // ---------- 鼠标交互：自研拖拽/点击（不再互斥） ----------
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left)
                return;

            _clickTimer.Stop();
            // 距离上次松开超过窗口期则视为新的一串点击
            if ((DateTime.Now - _lastClickTime).TotalMilliseconds > ClickWindowMs)
                _clickCount = 0;

            _mouseDown = true;
            _dragging = false;
            _dragStartScreen = Control.MousePosition;
            _dragStartLocation = Location;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            // 逃跑/走动中不响应拖拽与悬停（避免打断横向位移）
            if (_state == PetState.Run || _state == PetState.Walk)
                return;

            if (_mouseDown && !_dragging)
            {
                Point cur = Control.MousePosition;
                if (Math.Abs(cur.X - _dragStartScreen.X) > DragThreshold ||
                    Math.Abs(cur.Y - _dragStartScreen.Y) > DragThreshold)
                {
                    _dragging = true;
                    // 拖拽期间打断待机动画，显示"被抓住"的惊讶帧
                    _state = PetState.Idle;
                    _frameIndex = 2;
                }
            }

            if (_dragging)
            {
                Point cur = Control.MousePosition;
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                int nx = _dragStartLocation.X + (cur.X - _dragStartScreen.X);
                int ny = _dragStartLocation.Y + (cur.Y - _dragStartScreen.Y);
                if (nx < wa.Left) nx = wa.Left;
                if (ny < wa.Top) ny = wa.Top;
                if (nx + FrameSize > wa.Right) nx = wa.Right - FrameSize;
                if (ny + FrameSize > wa.Bottom) ny = wa.Bottom - FrameSize;
                Location = new Point(nx, ny);
                RenderLayered(); // 拖拽中立即重绘，保证跟随流畅
            }
            else if (!_mouseDown && _state == PetState.Idle && _hoverCooldown <= 0)
            {
                // 悬停小反应（节流，避免频繁打断）
                _hoverCooldown = 30; // 约 2.1s 冷却
                _state = PetState.Hover;
                SetAction(new int[] { 2, 20, 2 }, 3, 1); // 歪头看向
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            // 右键：layered 窗口下手动弹出菜单（不依赖 ContextMenuStrip 属性）
            if (e.Button == MouseButtons.Right)
            {
                if (!_menu.Visible)
                    _menu.Show(this, e.Location);
                return;
            }

            if (e.Button != MouseButtons.Left)
                return;

            _mouseDown = false;

            if (_dragging)
            {
                // 拖拽松手：不触发点击、不扔出、不飞起，直接回到待机
                _dragging = false;
                GotoIdle();
                return;
            }

            // 未发生拖拽 -> 计为一次点击
            _clickCount++;
            if (_clickCount > 3) _clickCount = 3;
            _lastClickTime = DateTime.Now;
            _clickTimer.Start();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_state == PetState.Hover)
                GotoIdle();
        }

        // 连续点击窗口结束：按点击数触发对应反应
        private void OnClickTimeout(object sender, EventArgs e)
        {
            _clickTimer.Stop();
            int count = _clickCount;
            _clickCount = 0;

            if (_dragging || _state == PetState.Run || _state == PetState.Walk)
                return;

            if (count == 1)
            {
                // 单击：随机贴地小反应（不飞起）
                StartRandomIdleReact();
                _state = PetState.React;
            }
            else if (count == 2)
            {
                // 双击：恐惧发抖
                _state = PetState.Shiver;
                _stateTick = 0;
            }
            else if (count >= 3)
            {
                // 三击：贴地横跑逃跑（远离鼠标）
                Point mouse = Control.MousePosition;
                _walkDir = (mouse.X > Left + FrameSize / 2) ? -1 : 1;
                SnapToGround();
                _state = PetState.Run;
                SetAction(new int[] { 1, 2, 3 }, 30, 1);
            }
        }
    }
}
