using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.Installers;
using CmlLib.Core.ProcessBuilder;
using LarpLand.Core;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Runtime.InteropServices;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace LarpLand
{
    public partial class MainWindow : Window
    {
        private AppSettings _settings = null!;
        private MinecraftPath _minecraftPath = null!;
        private MinecraftLauncher _launcher = null!;
        private Process? _gameProcess;
        private bool _isBusy;
        private bool _needsModpackUpdate;
        private double _scrollTarget = -1;
        private bool _scrolling;

        private System.Windows.Threading.DispatcherTimer? _sysMonTimer;
        private System.Collections.Generic.List<string> _logLines = new();
        private long _lastProgressTick;
        private long _lastByteTick;
        private long _lastByteCount;
        private long _lastByteSpeedTick;
        private long _lastNetLogTick;


        private class StatusTextDummy
        {
            private MainWindow _w;
            public StatusTextDummy(MainWindow w) { _w = w; }
            public string Text { set { _w.Log(value); } get { return ""; } }
        }
        private StatusTextDummy StatusText => new StatusTextDummy(this);

        private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

        private const string VER = "2026.09.18";
        private static string VerDisplay => ReleaseVersion.Display(VER);
        private const string MC = GameVersions.Minecraft;
        private const string LOADER = GameVersions.NeoForge;
        private const string FULL_ID = GameVersions.NeoForgeProfileId;
        private const string DefPrimary = "#0A0F17";
        private const string DefAccent = "#57C7F2";
        private const string MODPACK_VER_URL = Endpoints.ModpackVersionUrl;
        private const string LAUNCHER_VER_URL = Endpoints.LauncherVersionUrl;
        private const string MODPACK_URL = Endpoints.ModpackUrl;
        private const string LAUNCHER_EXE_URL = Endpoints.LauncherExeUrl;
        private const string LOADER_JAR_URL = GameVersions.NeoForgeInstallerUrl;

        private string _onlineModpackVer = "0.0";
        private static readonly Random _rnd = new();

        private const int ShenandoahHeapThresholdMb = 8192;

        private readonly string[] _jvmArgs = {
            "-XX:+ParallelRefProcEnabled","-XX:+ExplicitGCInvokesConcurrent",
            "-XX:+PerfDisableSharedMem"};

        private readonly string[] _shenandoahArgs = {
            "-XX:+UseShenandoahGC","-XX:ShenandoahGCHeuristics=adaptive"};

        private static readonly System.Text.RegularExpressions.Regex _defaultCollectorArgs =
            new(@"\s-XX:\+UseG1GC(?=\s|$)|\s-XX:G1\w+=\S+|\s-XX:MaxGCPauseMillis=\S+");

        private TaskCompletionSource<bool>? _dialogTcs;
        private readonly System.Threading.SemaphoreSlim _dialogGate = new(1, 1);

        private async Task<bool> ShowCustomDialog(string message, string title = "Внимание", bool isYesNo = false)
        {
            await _dialogGate.WaitAsync();
            try
            {
                CustomDialogTitle.Text = Lang.T(title);
                CustomDialogMessage.Text = message;
                CustomDialogMessage.Visibility = Visibility.Visible;
                CustomDialogInputBox.Visibility = Visibility.Collapsed;
                CustomDialogBtnCancel.Visibility = isYesNo ? Visibility.Visible : Visibility.Collapsed;
                CustomDialogBtnOk.Content = isYesNo ? Lang.T("Да") : "OK";

                CustomDialogOverlay.Visibility = Visibility.Visible;
                TweenOpacity(CustomDialogOverlay, 0, 1, 200, Linear);
                TweenScale(CustomDialogScale, 0.95, 1, 250, OutCubic);
                TweenY(CustomDialogTranslate, 15, 0, 250, OutCubic);

                _dialogTcs = new TaskCompletionSource<bool>();
                return await _dialogTcs.Task;
            }
            finally
            {
                _dialogGate.Release();
            }
        }

        private void CustomDialogOk_Click(object s, RoutedEventArgs e)
        {
            CloseCustomDialog();
            _dialogTcs?.TrySetResult(true);
        }

        private void CustomDialogCancel_Click(object s, RoutedEventArgs e)
        {
            CloseCustomDialog();
            _dialogTcs?.TrySetResult(false);
        }

        private void CloseCustomDialog()
        {
            TweenOpacity(CustomDialogOverlay, 1, 0, 150, Linear, 0, () =>
            {
                CustomDialogOverlay.Visibility = Visibility.Hidden;
                CustomDialogOverlay.Opacity = 1;
                CustomDialogInputBox.Visibility = Visibility.Collapsed;
            });
        }

        private async Task<string?> ShowInputDialogAsync(string title, string defaultValue = "")
        {
            await _dialogGate.WaitAsync();
            try
            {
                CustomDialogTitle.Text = Lang.T(title);
                CustomDialogMessage.Text = "";
                CustomDialogMessage.Visibility = Visibility.Collapsed;
                CustomDialogInputBox.Visibility = Visibility.Visible;
                CustomDialogInputBox.Text = defaultValue;
                CustomDialogBtnCancel.Visibility = Visibility.Visible;
                CustomDialogBtnOk.Content = "OK";

                CustomDialogOverlay.Visibility = Visibility.Visible;
                TweenOpacity(CustomDialogOverlay, 0, 1, 200, Linear);
                TweenScale(CustomDialogScale, 0.95, 1, 250, OutCubic);
                TweenY(CustomDialogTranslate, 15, 0, 250, OutCubic);

                _dialogTcs = new TaskCompletionSource<bool>();
                bool confirmed = await _dialogTcs.Task;

                CustomDialogMessage.Visibility = Visibility.Visible;

                if (!confirmed)
                    return null;

                string trimmedInput = CustomDialogInputBox.Text.Trim();
                return string.IsNullOrWhiteSpace(trimmedInput) ? null : trimmedInput;
            }
            finally
            {
                _dialogGate.Release();
            }
        }

        public MainWindow()
        {
            LauncherLog.Init();
            RefreshDisplayRate(true);
            InitializeComponent();
            InitializeLauncherCore();
            InitPixelWorld();
            StateChanged += (s, e) => UpdateSceneAnimation();
            Activated += (s, e) => UpdateSceneAnimation();
            Deactivated += (s, e) => UpdateSceneAnimation();
            LocationChanged += (s, e) => RefreshDisplayRate();
            _ = BootSequenceAsync();

            ResilientHttpClientFactory.DownloadRetry += notice => Log(notice);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            RefreshDisplayRate(true);
            if (System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle) is { } src)
                src.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
                {
                    if (msg == WM_DISPLAYCHANGE || msg == WM_SETTINGCHANGE) RefreshDisplayRate(true);
                    return IntPtr.Zero;
                });
        }

        protected override void OnClosed(EventArgs e)
        {
            _sceneAlive = false;
            try { _sceneGate.Set(); } catch { }
            base.OnClosed(e);
        }

        #region Пиксельная сцена (ночной берег)

        private const int SCN_W = 200;
        private const int SCN_H = 118;
        private const int HORIZON = 72;
        private const int RidgeCount = 3;
        private const int StarCount = 70;
        private const int MeteorTrail = 9;

        private WriteableBitmap? _sceneBmp;
        private readonly byte[] _sceneBuf = new byte[SCN_W * SCN_H * 4];
        private readonly byte[] _scenePresent = new byte[SCN_W * SCN_H * 4];
        private System.Threading.Thread? _sceneThread;
        private readonly System.Threading.ManualResetEventSlim _sceneGate = new(true);
        private readonly object _frameLock = new();
        private volatile bool _sceneAlive;
        private volatile bool _framePending;
        private volatile bool _sceneRunning = true;
        private volatile bool _bgAnimated = true;
        private volatile int _accentArgb = 0x57C7F2;
        private int _frame;

        private readonly Random _sceneRng = new();
        private static readonly double[] RidgeSpeed = { 0.035, 0.085, 0.17 };
        private static readonly double[] RidgeShade = { 0.34, 0.22, 0.12 };
        private readonly int[,] _ridge = new int[RidgeCount, SCN_W];
        private readonly double[] _ridgeShift = new double[RidgeCount];

        private readonly int[] _starX = new int[StarCount];
        private readonly int[] _starY = new int[StarCount];
        private readonly double[] _starPhase = new double[StarCount];
        private readonly double[] _starSpeed = new double[StarCount];

        private readonly double[] _cloudX = new double[4];
        private readonly int[] _cloudY = new int[4];
        private readonly int[] _cloudW = new int[4];

        private double _meteorX, _meteorY, _meteorVx, _meteorVy;
        private int _meteorLife;
        private int _meteorWait = 160;

        private double _wave;
        private double _moonGlow;

        private string _bgModeApplied = "";
        private bool _gradientInit;
        private bool _plainInit;
        private FrameworkElement? _activeBackdrop;
        private ScaleTransform? _activeScale;
        private Color _bgPrimary = (Color)ColorConverter.ConvertFromString(DefPrimary);
        private Color _bgAccent = (Color)ColorConverter.ConvertFromString(DefAccent);
        private readonly Stopwatch _fxClock = Stopwatch.StartNew();
        private TranslateTransform[] _flowTr = Array.Empty<TranslateTransform>();
        private RotateTransform[] _flowRot = Array.Empty<RotateTransform>();

        private Color AccentSnapshot()
        {
            int argb = _accentArgb;
            return Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        }

        private void InitPixelWorld()
        {
            _sceneBmp = new WriteableBitmap(SCN_W, SCN_H, 96, 96, PixelFormats.Bgra32, null);
            PixelSceneImage.Source = _sceneBmp;
            InitCube();

            for (int layer = 0; layer < RidgeCount; layer++) BuildRidge(layer);

            for (int i = 0; i < StarCount; i++)
            {
                _starX[i] = _sceneRng.Next(SCN_W);
                _starY[i] = _sceneRng.Next(HORIZON - 8);
                _starPhase[i] = _sceneRng.NextDouble() * Math.PI * 2;
                _starSpeed[i] = 0.015 + _sceneRng.NextDouble() * 0.05;
            }

            for (int i = 0; i < _cloudX.Length; i++)
            {
                _cloudX[i] = _sceneRng.Next(SCN_W);
                _cloudY[i] = 10 + _sceneRng.Next(28);
                _cloudW[i] = 22 + _sceneRng.Next(26);
            }

            _sceneAlive = true;
            _sceneThread = new System.Threading.Thread(SceneLoop)
            {
                IsBackground = true,
                Priority = System.Threading.ThreadPriority.BelowNormal,
                Name = "LarpLandScene"
            };
            _sceneThread.Start();
        }

        // WHY: гребень строится замкнутым случайным блужданием, иначе при закольцованном
        // WHY: параллаксе на стыке массива в кадре появляется вертикальный обрыв
        private void BuildRidge(int layer)
        {
            double baseHeight = HORIZON - 30 + layer * 10;
            double amplitude = 11 - layer * 2.5;
            int peaks = 3 + layer * 2;
            double phase = _sceneRng.NextDouble() * Math.PI * 2;
            double phaseTwo = _sceneRng.NextDouble() * Math.PI * 2;

            for (int x = 0; x < SCN_W; x++)
            {
                double angle = x / (double)SCN_W * Math.PI * 2;
                double profile =
                    Math.Sin(angle * peaks + phase) * 0.6 +
                    Math.Sin(angle * (peaks * 2) + phaseTwo) * 0.28 +
                    Math.Sin(angle * (peaks * 4) + phase * 1.7) * 0.12;

                _ridge[layer, x] = (int)Math.Round(baseHeight - profile * amplitude);
            }
        }

        private void SceneLoop()
        {
            while (_sceneAlive)
            {
                _sceneGate.Wait();
                if (!_sceneAlive) return;

                WorldTick();
                PresentFrame();

                System.Threading.Thread.Sleep(80);
            }
        }

        private void PresentFrame()
        {
            lock (_frameLock)
            {
                Buffer.BlockCopy(_sceneBuf, 0, _scenePresent, 0, _sceneBuf.Length);
                Buffer.BlockCopy(_cubeBuf, 0, _cubePresent, 0, _cubeBuf.Length);
                if (_framePending) return;
                _framePending = true;
            }

            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(PushFrame));
        }

        private void PushFrame()
        {
            lock (_frameLock)
            {
                _framePending = false;
                if (_bgAnimated) _sceneBmp?.WritePixels(new Int32Rect(0, 0, SCN_W, SCN_H), _scenePresent, SCN_W * 4, 0);
                _cubeBmp?.WritePixels(new Int32Rect(0, 0, CUBE_W, CUBE_H), _cubePresent, CUBE_W * 4, 0);
            }

            if (_bgModeApplied == "gradient" && _gradientInit) TickGradient();
        }

        private void UpdateSceneAnimation()
        {
            bool shouldRun = WindowState != WindowState.Minimized && IsActive;
            if (shouldRun == _sceneRunning) return;

            _sceneRunning = shouldRun;
            if (shouldRun) _sceneGate.Set(); else _sceneGate.Reset();
        }

        private void WorldTick()
        {
            _frame++;
            _wave += 0.06;
            _moonGlow = 0.5 + 0.5 * Math.Sin(_frame * 0.012);

            for (int layer = 0; layer < RidgeCount; layer++)
            {
                _ridgeShift[layer] += RidgeSpeed[layer];
                if (_ridgeShift[layer] >= SCN_W) _ridgeShift[layer] -= SCN_W;
            }

            for (int i = 0; i < _cloudX.Length; i++)
            {
                _cloudX[i] += 0.06 + i * 0.015;
                if (_cloudX[i] > SCN_W + _cloudW[i]) _cloudX[i] = -_cloudW[i];
            }

            StepMeteor();

            if (_bgAnimated) RenderScene();
            RenderCube();
        }

        private void StepMeteor()
        {
            if (_meteorLife > 0)
            {
                _meteorX += _meteorVx;
                _meteorY += _meteorVy;
                _meteorLife--;
                if (_meteorY > HORIZON - 6 || _meteorX < -10 || _meteorX > SCN_W + 10) _meteorLife = 0;
                return;
            }

            if (--_meteorWait > 0) return;

            _meteorWait = 220 + _sceneRng.Next(420);
            _meteorLife = 40 + _sceneRng.Next(30);
            _meteorX = _sceneRng.Next(SCN_W);
            _meteorY = 4 + _sceneRng.Next(22);
            _meteorVx = 1.1 + _sceneRng.NextDouble() * 1.3;
            if (_sceneRng.Next(2) == 0) _meteorVx = -_meteorVx;
            _meteorVy = 0.5 + _sceneRng.NextDouble() * 0.5;
        }

        // WHY: рисуется в фоновом потоке, поэтому цвет темы берётся снимком, а не из ресурсов окна
        private void RenderScene()
        {
            Color accent = AccentSnapshot();

            FillSky(accent);
            DrawStars(accent);
            DrawMoon(accent);
            DrawClouds(accent);
            DrawMeteor(accent);
            DrawRidges(accent);
            FillWater(accent);
            DrawMoonPath(accent);
            DrawRipples(accent);
            DrawShoreFog(accent);
        }

        private void FillSky(Color accent)
        {
            for (int y = 0; y < HORIZON; y++)
            {
                double t = (double)y / HORIZON;
                double glow = t * t;
                byte r = (byte)(5 + accent.R * 0.05 * glow);
                byte g = (byte)(9 + accent.G * 0.17 * glow);
                byte b = (byte)(18 + accent.B * 0.26 * glow);
                FillRow(y, r, g, b);
            }
        }

        private void FillRow(int y, byte r, byte g, byte b)
        {
            int start = y * SCN_W * 4;
            for (int x = 0; x < SCN_W; x++)
            {
                int i = start + x * 4;
                _sceneBuf[i] = b;
                _sceneBuf[i + 1] = g;
                _sceneBuf[i + 2] = r;
                _sceneBuf[i + 3] = 255;
            }
        }

        private void DrawStars(Color accent)
        {
            Color pale = LerpColor(accent, Colors.White, 0.7);

            for (int i = 0; i < StarCount; i++)
            {
                _starPhase[i] += _starSpeed[i];
                double twinkle = 0.5 + 0.5 * Math.Sin(_starPhase[i]);
                double alpha = 0.18 + 0.62 * twinkle * (1.0 - (double)_starY[i] / HORIZON * 0.5);

                BP(_starX[i], _starY[i], pale.R, pale.G, pale.B, alpha);

                if (twinkle > 0.96)
                {
                    BP(_starX[i] - 1, _starY[i], accent.R, accent.G, accent.B, alpha * 0.4);
                    BP(_starX[i] + 1, _starY[i], accent.R, accent.G, accent.B, alpha * 0.4);
                }
            }
        }

        private void DrawMoon(Color accent)
        {
            const int moonX = 152;
            const int moonY = 22;
            const int moonSize = 9;
            Color body = LerpColor(accent, Colors.White, 0.82);

            for (int radius = moonSize + 7; radius > moonSize; radius--)
            {
                double alpha = 0.05 * _moonGlow * (moonSize + 8 - radius) / 7.0;
                DrawHalo(moonX, moonY, radius, accent, alpha);
            }

            for (int y = 0; y < moonSize; y++)
            {
                for (int x = 0; x < moonSize; x++)
                {
                    bool corner = (x == 0 || x == moonSize - 1) && (y == 0 || y == moonSize - 1);
                    if (corner) continue;
                    BP(moonX + x, moonY + y, body.R, body.G, body.B, 1);
                }
            }

            BP(moonX + 2, moonY + 3, accent.R, accent.G, accent.B, 0.45);
            BP(moonX + 6, moonY + 5, accent.R, accent.G, accent.B, 0.35);
            BP(moonX + 4, moonY + 6, accent.R, accent.G, accent.B, 0.3);
        }

        private void DrawHalo(int cx, int cy, int radius, Color color, double alpha)
        {
            for (int y = -radius; y <= radius; y++)
            {
                for (int x = -radius; x <= radius; x++)
                {
                    int distance = x * x + y * y;
                    if (distance > radius * radius || distance < (radius - 1) * (radius - 1)) continue;
                    BP(cx + 4 + x, cy + 4 + y, color.R, color.G, color.B, alpha);
                }
            }
        }

        private void DrawClouds(Color accent)
        {
            Color cloud = LerpColor(accent, Colors.White, 0.25);

            for (int i = 0; i < _cloudX.Length; i++)
            {
                int width = _cloudW[i];
                int y = _cloudY[i];
                double alpha = 0.10 + 0.04 * i;

                for (int x = 0; x < width; x++)
                {
                    int px = (int)_cloudX[i] + x;
                    double edge = Math.Sin(Math.PI * x / width);
                    int thickness = (int)(edge * 3.2);

                    for (int t = 0; t <= thickness; t++)
                        BP(px, y + t, cloud.R, cloud.G, cloud.B, alpha * edge);
                }
            }
        }

        private void DrawMeteor(Color accent)
        {
            if (_meteorLife <= 0) return;

            Color head = LerpColor(accent, Colors.White, 0.85);

            for (int t = 0; t < MeteorTrail; t++)
            {
                int x = (int)(_meteorX - _meteorVx * t);
                int y = (int)(_meteorY - _meteorVy * t);
                double alpha = (1.0 - (double)t / MeteorTrail) * 0.85;
                BP(x, y, head.R, head.G, head.B, alpha);
            }
        }

        private void DrawRidges(Color accent)
        {
            for (int layer = 0; layer < RidgeCount; layer++)
            {
                double shade = RidgeShade[layer];
                byte r = (byte)(accent.R * shade * 0.30 + 2);
                byte g = (byte)(accent.G * shade * 0.48 + 5);
                byte b = (byte)(accent.B * shade * 0.70 + 9);
                int shift = (int)_ridgeShift[layer];

                for (int x = 0; x < SCN_W; x++)
                {
                    int sample = (x + shift) % SCN_W;
                    int top = _ridge[layer, sample];

                    for (int y = top; y < HORIZON; y++) SP(x, y, r, g, b);

                    byte er = (byte)Math.Min(255, r + accent.R * 0.22);
                    byte eg = (byte)Math.Min(255, g + accent.G * 0.26);
                    byte eb = (byte)Math.Min(255, b + accent.B * 0.30);
                    SP(x, top, er, eg, eb);
                }
            }
        }

        private void FillWater(Color accent)
        {
            for (int y = HORIZON; y < SCN_H; y++)
            {
                double depth = (double)(y - HORIZON) / (SCN_H - HORIZON);
                double near = (1 - depth) * (1 - depth);
                byte r = (byte)(1 + accent.R * 0.04 * near);
                byte g = (byte)(3 + accent.G * 0.09 * near);
                byte b = (byte)(7 + accent.B * 0.16 * near);
                FillRow(y, r, g, b);
            }
        }

        private void DrawMoonPath(Color accent)
        {
            Color shine = LerpColor(accent, Colors.White, 0.6);

            for (int y = HORIZON; y < SCN_H; y++)
            {
                double depth = (double)(y - HORIZON) / (SCN_H - HORIZON);
                int spread = 2 + (int)(depth * 9);
                double alpha = (1 - depth) * 0.35;
                int center = 156 + (int)(Math.Sin(_wave * 0.7 + y * 0.35) * 2);

                for (int x = center - spread; x <= center + spread; x++)
                {
                    if (((x + y + (int)(_wave * 2)) % 3) != 0) continue;
                    BP(x, y, shine.R, shine.G, shine.B, alpha);
                }
            }
        }

        private void DrawRipples(Color accent)
        {
            Color crest = LerpColor(accent, Colors.White, 0.3);

            for (int y = HORIZON + 2; y < SCN_H; y += 2)
            {
                double depth = (double)(y - HORIZON) / (SCN_H - HORIZON);
                double speed = 0.5 + depth * 1.6;
                int length = 1 + (int)(depth * 2);
                int gap = 9 + ((y * 7) % 11);
                int offset = (int)(_wave * speed * 4 + y * 3) % gap;
                double alpha = (0.05 + 0.13 * (1 - depth)) * (0.6 + 0.4 * Math.Sin(_wave * 0.5 + y));

                for (int x = -offset; x < SCN_W; x += gap)
                    for (int i = 0; i < length; i++)
                        BP(x + i, y, crest.R, crest.G, crest.B, alpha);
            }
        }

        private void DrawShoreFog(Color accent)
        {
            Color mist = LerpColor(accent, Colors.White, 0.4);

            for (int y = HORIZON - 3; y < HORIZON + 3; y++)
            {
                double alpha = 0.16 * (1 - Math.Abs(y - HORIZON) / 3.0);
                for (int x = 0; x < SCN_W; x++)
                {
                    double band = Math.Sin(x * 0.07 + _wave * 0.4);
                    if (band < 0.1) continue;
                    BP(x, y, mist.R, mist.G, mist.B, alpha * band);
                }
            }
        }

        private void SP(int x, int y, byte r, byte g, byte b)
        {
            if (x < 0 || y < 0 || x >= SCN_W || y >= SCN_H) return;

            int i = (y * SCN_W + x) * 4;
            _sceneBuf[i] = b;
            _sceneBuf[i + 1] = g;
            _sceneBuf[i + 2] = r;
            _sceneBuf[i + 3] = 255;
        }

        private void BP(int x, int y, byte r, byte g, byte b, double a)
        {
            if (x < 0 || y < 0 || x >= SCN_W || y >= SCN_H || a <= 0) return;
            if (a > 1) a = 1;

            int i = (y * SCN_W + x) * 4;
            _sceneBuf[i] = (byte)(_sceneBuf[i] + (b - _sceneBuf[i]) * a);
            _sceneBuf[i + 1] = (byte)(_sceneBuf[i + 1] + (g - _sceneBuf[i + 1]) * a);
            _sceneBuf[i + 2] = (byte)(_sceneBuf[i + 2] + (r - _sceneBuf[i + 2]) * a);
        }

        private void BackgroundCombo_Changed(object s, SelectionChangedEventArgs e)
        {
            if (_settings == null) return;
            if (BackgroundCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string mode) return;
            if (mode == _settings.BackgroundMode) return;

            _settings.BackgroundMode = mode;
            AppSettings.Save(_settings);
            ApplyBackgroundMode(mode, animate: true);
        }

        private void ApplyBackgroundMode(string mode, bool animate)
        {
            if (mode != "animated" && mode != "gradient" && mode != "plain") mode = "animated";
            if (mode == _bgModeApplied) return;
            _bgModeApplied = mode;

            _bgAnimated = mode == "animated";
            UpdateSceneAnimation();

            FrameworkElement? target = null;
            ScaleTransform? scale = null;

            if (mode == "gradient")
            {
                EnsureGradientBackdrop();
                target = GradientBackdrop;
                scale = GradientScale;
            }
            else if (mode == "plain")
            {
                EnsurePlainBackdrop();
                target = PlainBackdrop;
                scale = PlainScale;
            }

            if (_activeBackdrop != null && _activeBackdrop != target)
            {
                FrameworkElement leaving = _activeBackdrop;
                TweenOpacity(leaving, leaving.Opacity, 0, animate ? 320 : 1, OutCubic, done: () => leaving.Visibility = Visibility.Collapsed);
            }

            PixelSceneImage.Visibility = mode == "animated" ? Visibility.Visible : Visibility.Collapsed;

            _activeBackdrop = target;
            _activeScale = scale;

            if (target == null) return;

            target.Visibility = Visibility.Visible;
            TweenOpacity(target, target.Opacity, 1, animate ? 380 : 1, OutCubic);
            if (scale != null) TweenScale(scale, animate ? 1.06 : 1, 1, animate ? 520 : 1, OutCubic);
        }

        private void TickGradient()
        {
            double t = _fxClock.Elapsed.TotalSeconds;
            _flowRot[0].Angle = t * 360 / 120;
            _flowRot[1].Angle = -t * 360 / 150;
            _flowRot[2].Angle = t * 360 / 100;
            _flowRot[3].Angle = -t * 360 / 170;
            Flow1.Opacity = Breath(t, 0.40, 0.62, 9);
            Flow2.Opacity = Breath(t, 0.34, 0.56, 12);
            Flow3.Opacity = Breath(t, 0.30, 0.52, 10);
            Flow4.Opacity = Breath(t, 0.28, 0.48, 14);
        }

        private static double Breath(double t, double from, double to, double seconds) =>
            from + (to - from) * (0.5 + 0.5 * Math.Sin(t * Math.PI * 2 / seconds));

        private static TranslateTransform FxTr(FrameworkElement element)
        {
            if (element.RenderTransform is TransformGroup group)
                foreach (Transform part in group.Children)
                    if (part is TranslateTransform found) return found;

            var created = new TranslateTransform();
            var built = new TransformGroup();
            built.Children.Add(created);
            built.Children.Add(new RotateTransform());
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = built;
            return created;
        }

        private static RotateTransform FxRot(FrameworkElement element)
        {
            if (element.RenderTransform is TransformGroup group)
                foreach (Transform part in group.Children)
                    if (part is RotateTransform found) return found;

            var created = new RotateTransform();
            var built = new TransformGroup();
            built.Children.Add(new TranslateTransform());
            built.Children.Add(created);
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = built;
            return created;
        }

        private static Color ShiftHue(Color c, double deg)
        {
            double r = c.R / 255.0, g = c.G / 255.0, bl = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, bl)), min = Math.Min(r, Math.Min(g, bl));
            double d = max - min;
            double h = 0;
            if (d > 0)
            {
                if (max == r) h = 60 * (((g - bl) / d) % 6);
                else if (max == g) h = 60 * ((bl - r) / d + 2);
                else h = 60 * ((r - g) / d + 4);
            }
            if (h < 0) h += 360;
            double s = max <= 0 ? 0 : d / max;
            double v = max;
            h = (h + deg) % 360;
            if (h < 0) h += 360;
            double cc = v * s;
            double x = cc * (1 - Math.Abs(h / 60 % 2 - 1));
            double m = v - cc;
            (double r2, double g2, double b2) = ((int)(h / 60)) switch
            {
                0 => (cc, x, 0.0),
                1 => (x, cc, 0.0),
                2 => (0.0, cc, x),
                3 => (0.0, x, cc),
                4 => (x, 0.0, cc),
                _ => (cc, 0.0, x)
            };
            return Color.FromRgb((byte)((r2 + m) * 255), (byte)((g2 + m) * 255), (byte)((b2 + m) * 255));
        }

        private void EnsureGradientBackdrop()
        {
            if (_gradientInit) return;
            _gradientInit = true;
            UpdateGradientTheme();
            _flowTr = new[] { FxTr(Flow1), FxTr(Flow2), FxTr(Flow3), FxTr(Flow4) };
            _flowRot = new[] { FxRot(Flow1), FxRot(Flow2), FxRot(Flow3), FxRot(Flow4) };
        }

        private void EnsurePlainBackdrop()
        {
            if (_plainInit) return;
            _plainInit = true;
            UpdatePlainTheme();
        }

        private void UpdateBackdropTheme()
        {
            UpdateGradientTheme();
            UpdatePlainTheme();
        }

        private static Brush AuroraBrush(Color c, byte alpha)
        {
            var brush = new RadialGradientBrush { RadiusX = 0.72, RadiusY = 0.52 };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, c.R, c.G, c.B), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
            brush.Freeze();
            return brush;
        }

        private void UpdateGradientTheme()
        {
            if (!_gradientInit) return;
            var p = _bgPrimary;
            var a = _bgAccent;
            var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            b.GradientStops.Add(new GradientStop(Darken(p, 0.5), 0));
            b.GradientStops.Add(new GradientStop(p, 1));
            b.Freeze();
            GradientBase.Fill = b;
            Flow1.Fill = AuroraBrush(a, 0x80);
            Flow2.Fill = AuroraBrush(ShiftHue(a, 45), 0x6C);
            Flow3.Fill = AuroraBrush(ShiftHue(a, -50), 0x62);
            Flow4.Fill = AuroraBrush(Lighten(p, 0.4), 0x6C);
        }

        private void UpdatePlainTheme()
        {
            if (!_plainInit) return;
            var p = _bgPrimary;
            var a = _bgAccent;
            var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0.7, 1) };
            b.GradientStops.Add(new GradientStop(Darken(p, 0.55), 0));
            b.GradientStops.Add(new GradientStop(p, 0.55));
            b.GradientStops.Add(new GradientStop(Darken(p, 0.72), 1));
            b.Freeze();
            PlainBase.Fill = b;

            var g = new RadialGradientBrush { Center = new Point(0.5, 1.15), GradientOrigin = new Point(0.5, 1.15), RadiusX = 0.95, RadiusY = 0.8 };
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x30, a.R, a.G, a.B), 0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0, a.R, a.G, a.B), 1));
            g.Freeze();
            PlainGlow.Fill = g;
        }

        private static Color Darken(Color c, double f) =>
            Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));

        private static Color Lighten(Color c, double f) =>
            Color.FromRgb((byte)(c.R + (255 - c.R) * f), (byte)(c.G + (255 - c.G) * f), (byte)(c.B + (255 - c.B) * f));

        #endregion

        #region Загрузочный экран (boot / pixel-art / terminal)

        private static readonly string[] CreeperMap =
        {
            "00000000",
            "00000000",
            "01100110",
            "01100110",
            "00011000",
            "00111100",
            "00111100",
            "00100100",
        };

        private async Task BootSequenceAsync()
        {
            if (BootOverlay == null) return;
            try
            {
                await Task.Delay(300);

                BuildBootPixelArt();

                if (BootTitle != null)
                {
                    TweenOpacity(BootTitle, BootTitle.Opacity, 1, 400, Linear);
                    if (BootTitleTr != null) TweenY(BootTitleTr, BootTitleTr.Y, 0, 450, OutCubic);
                }
                if (BootSubtitle != null)
                    TweenOpacity(BootSubtitle, BootSubtitle.Opacity, 1, 350, Linear, 100);

                await Task.Delay(200);

                var accentBrush = (Brush)FindResource("AccentBrush");
                var okBrush = new SolidColorBrush(Color.FromRgb(0x7C, 0xDB, 0x6A));
                var lines = new (string tag, Brush tagBrush, string msg)[]
                {
                    ("[BOOT] ", accentBrush, "LarpLand Launcher " + VerDisplay),
                    ("[ OK ] ", okBrush,     Lang.T("Обнаружена ОС: ") + GetWindowsVersionName()),
                    ("[ OK ] ", okBrush,     Lang.T("Инициализация ядра")),
                    ("[ OK ] ", okBrush,     Lang.T("Загрузка конфигурации")),
                    ("[ OK ] ", okBrush,     Lang.T("Проверка целостности библиотек")),
                    ("[ OK ] ", okBrush,     Lang.T("Подготовка интерфейса")),
                    ("  >    ", accentBrush, Lang.T("Запуск лаунчера")),
                };

                for (int i = 0; i < lines.Length; i++)
                {
                    await TypeBootLineAsync(lines[i].tag, lines[i].tagBrush, lines[i].msg);
                    SetBootProgress((i + 1) * 100.0 / lines.Length);
                    await Task.Delay(45);
                }

                await Task.Delay(250);
                await FadeOutBootAsync();
            }
            catch { }

            await CheckPrerequisitesAsync();
            await WarnAboutRejectedNickname();
        }

        private bool _nicknameNeedsRepair;

        private bool NicknameAccepted() => _settings.UserType == "msa" || Nickname.IsValid(_settings.Username);

        private async Task WarnAboutRejectedNickname()
        {
            if (!_nicknameNeedsRepair) return;

            _nicknameNeedsRepair = false;
            await ShowCustomDialog(Lang.T(Nickname.RejectedMessage));
        }

        private async Task CheckPrerequisitesAsync()
        {
            try
            {
                if (WebView2Runtime.Installed()) return;

                MessageBox.Show(this,
                    Lang.T("Для входа через Microsoft нужен компонент Microsoft Edge WebView2 Runtime, который не установлен в системе.\n\nСейчас он будет загружен и установлен."),
                    Lang.T("Требуется компонент"), MessageBoxButton.OK, MessageBoxImage.Information);

                ShowSpinnerOverlay(Lang.T("Установка компонента"), Lang.T("Загрузка WebView2 Runtime…"), false);
                bool ok = await WebView2Runtime.InstallAsync();
                HideUpdateOverlay();

                if (!ok)
                    MessageBox.Show(this,
                        Lang.T("Не удалось установить WebView2 Runtime автоматически. Открою страницу загрузки — установите его вручную, иначе вход через Microsoft работать не будет."),
                        Lang.T("Требуется компонент"), MessageBoxButton.OK, MessageBoxImage.Warning);

                if (!ok) OpenInShell(WebView2Runtime.ManualPage);
            }
            catch (Exception error)
            {
                LauncherLog.Write($"[ERROR] Проверка WebView2 сорвалась: {error.Message}");
            }
        }

        // WHY: логотип собирается из квадратов на Canvas, потому что растянутый шрифт
        // WHY: в загрузочном экране мылится, а пиксельная решётка держит стиль сборки
        private void BuildBootPixelArt()
        {
            if (BootPixelCanvas == null) return;

            string[] rows =
            {
                "..####..",
                ".#....#.",
                "#..##..#",
                "#.#..#.#",
                "#.#..#.#",
                "#..##..#",
                ".#....#.",
                "..####.."
            };

            const double cell = 20;
            var accent = (Color)FindResource("AccentColor");
            var bright = LerpColor(accent, Colors.White, 0.35);

            BootPixelCanvas.Children.Clear();

            for (int row = 0; row < rows.Length; row++)
            {
                for (int column = 0; column < rows[row].Length; column++)
                {
                    if (rows[row][column] != '#') continue;

                    var block = new System.Windows.Shapes.Rectangle
                    {
                        Width = cell,
                        Height = cell,
                        Fill = new SolidColorBrush((row + column) % 2 == 0 ? accent : bright),
                        Opacity = 0
                    };

                    Canvas.SetLeft(block, column * cell + 10);
                    Canvas.SetTop(block, row * cell + 10);
                    BootPixelCanvas.Children.Add(block);

                    TweenOpacity(block, 0, 1, 260, OutCubic, 20 * (row + column));
                }
            }

            var glow = new DropShadowEffect
            {
                Color = accent,
                BlurRadius = 26,
                ShadowDepth = 0,
                Opacity = 0.5
            };
            BootPixelCanvas.Effect = glow;
            StartPulse(glow, 0.3, 0.75, 1600);
        }

        private async Task TypeBootLineAsync(string tag, Brush tagBrush, string message)
        {
            if (BootLogText == null) return;
            var tagRun = new Run(tag) { Foreground = tagBrush, FontWeight = FontWeights.Bold };
            var msgRun = new Run("") { Foreground = new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF)) };
            BootLogText.Inlines.Add(tagRun);
            BootLogText.Inlines.Add(msgRun);
            BootLogText.Inlines.Add(new LineBreak());

            foreach (char ch in message)
            {
                msgRun.Text += ch;
                await Task.Delay(6 + _rnd.Next(7));
            }
        }

        private void SetBootProgress(double target)
        {
            if (BootProgress == null) return;
            TweenValue(BootProgress, target, 280, OutCubic);
            if (BootPercent != null) BootPercent.Text = $"{Math.Round(target)}%";
        }

        private Task FadeOutBootAsync()
        {
            var tcs = new TaskCompletionSource<bool>();
            if (BootOverlay == null) { tcs.SetResult(true); return tcs.Task; }
            TweenOpacity(BootOverlay, BootOverlay.Opacity, 0, 400, InCubic, 0, () =>
            {
                StopPulse();
                if (BootPixelCanvas != null) { BootPixelCanvas.Effect = null; BootPixelCanvas.Children.Clear(); }
                BootOverlay.Visibility = Visibility.Collapsed;
                tcs.TrySetResult(true);
            });
            return tcs.Task;
        }

        #endregion

        #region Пиксельный куб справа

        private const int CUBE_W = 64;
        private const int CUBE_H = 86;

        private WriteableBitmap? _cubeBmp;
        private readonly byte[] _cubeBuf = new byte[CUBE_W * CUBE_H * 4];
        private readonly byte[] _cubePresent = new byte[CUBE_W * CUBE_H * 4];
        private readonly double[] _cubeProjX = new double[8];
        private readonly double[] _cubeProjY = new double[8];
        private readonly double[] _cubeDepth = new double[8];
        private double _cubeAngle;

        private static readonly double[,] CubeVertices =
        {
            { -1, -1, -1 }, { 1, -1, -1 }, { 1, 1, -1 }, { -1, 1, -1 },
            { -1, -1, 1 }, { 1, -1, 1 }, { 1, 1, 1 }, { -1, 1, 1 }
        };

        private static readonly int[,] CubeEdges =
        {
            { 0, 1 }, { 1, 2 }, { 2, 3 }, { 3, 0 },
            { 4, 5 }, { 5, 6 }, { 6, 7 }, { 7, 4 },
            { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 }
        };

        private void InitCube()
        {
            _cubeBmp = new WriteableBitmap(CUBE_W, CUBE_H, 96, 96, PixelFormats.Bgra32, null);
            if (BlobImage != null) BlobImage.Source = _cubeBmp;
        }

        private void RenderCube()
        {
            Array.Clear(_cubeBuf, 0, _cubeBuf.Length);

            Color accent = AccentSnapshot();
            Color bright = LerpColor(accent, Colors.White, 0.55);

            _cubeAngle += 0.024;
            ProjectCube(_cubeAngle);
            DrawCubeGrid(accent);

            for (int edge = 0; edge < CubeEdges.GetLength(0); edge++)
            {
                int a = CubeEdges[edge, 0];
                int b = CubeEdges[edge, 1];
                double depth = (_cubeDepth[a] + _cubeDepth[b]) * 0.5;
                double alpha = 0.32 + 0.68 * depth;
                DrawCubeLine((int)_cubeProjX[a], (int)_cubeProjY[a], (int)_cubeProjX[b], (int)_cubeProjY[b], accent, alpha);
            }

            for (int vertex = 0; vertex < 8; vertex++)
            {
                double alpha = 0.45 + 0.55 * _cubeDepth[vertex];
                int size = _cubeDepth[vertex] > 0.5 ? 2 : 1;
                for (int dy = 0; dy < size; dy++)
                    for (int dx = 0; dx < size; dx++)
                        CubePixel((int)_cubeProjX[vertex] + dx, (int)_cubeProjY[vertex] + dy, bright, alpha);
            }
        }

        private void ProjectCube(double angle)
        {
            double cosY = Math.Cos(angle), sinY = Math.Sin(angle);
            double cosX = Math.Cos(angle * 0.62), sinX = Math.Sin(angle * 0.62);

            for (int i = 0; i < 8; i++)
            {
                double x = CubeVertices[i, 0], y = CubeVertices[i, 1], z = CubeVertices[i, 2];

                double rx = x * cosY + z * sinY;
                double rz = z * cosY - x * sinY;
                double ry = y * cosX - rz * sinX;
                rz = rz * cosX + y * sinX;

                double scale = 2.2 / (3.4 + rz);
                _cubeProjX[i] = CUBE_W * 0.5 + rx * scale * CUBE_W * 0.30;
                _cubeProjY[i] = CUBE_H * 0.5 + ry * scale * CUBE_W * 0.30;
                _cubeDepth[i] = Math.Clamp((2.0 - rz) / 4.0, 0, 1);
            }
        }

        private void DrawCubeGrid(Color accent)
        {
            for (int y = 2; y < CUBE_H; y += 6)
                for (int x = 2; x < CUBE_W; x += 6)
                    CubePixel(x, y, accent, 0.16);
        }

        private void DrawCubeLine(int x0, int y0, int x1, int y1, Color color, double alpha)
        {
            int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
            int stepX = x0 < x1 ? 1 : -1, stepY = y0 < y1 ? 1 : -1;
            int error = dx - dy;

            while (true)
            {
                CubePixel(x0, y0, color, alpha);
                if (x0 == x1 && y0 == y1) return;

                int doubled = error * 2;
                if (doubled > -dy) { error -= dy; x0 += stepX; }
                if (doubled < dx) { error += dx; y0 += stepY; }
            }
        }

        private void CubePixel(int x, int y, Color color, double alpha)
        {
            if (x < 0 || y < 0 || x >= CUBE_W || y >= CUBE_H || alpha <= 0) return;
            if (alpha > 1) alpha = 1;

            int i = (y * CUBE_W + x) * 4;
            byte current = _cubeBuf[i + 3];
            _cubeBuf[i] = (byte)(color.B * alpha);
            _cubeBuf[i + 1] = (byte)(color.G * alpha);
            _cubeBuf[i + 2] = (byte)(color.R * alpha);
            _cubeBuf[i + 3] = (byte)Math.Max(current, (byte)(255 * alpha));
        }

        private static Color LerpColor(Color a, Color b, double t)
        {
            return Color.FromRgb(
                (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t),
                (byte)(a.B + (b.B - a.B) * t));
        }

        #endregion

        private void Log(string message)
        {
            string prefix = message.Contains("Ошибка") || message.Contains("Error") || message.Contains("error") ? "[ERR]" : "[SYS]";
            LogTagged(prefix, message);
        }

        private void LogNet(string message) => LogTagged("[NET]", message);

        private void LogError(string message) => LogTagged("[ERR]", message);

        private void LogTagged(string prefix, string message)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => LogTagged(prefix, message)); return; }
            LauncherLog.Write($"{prefix} {message}");
            _logLines.Add($"{prefix} {message}");
            if (_logLines.Count > 200) _logLines.RemoveAt(0);
            LogTerminalText.Text = string.Join("\n", _logLines);
            LogScroll?.Dispatcher.BeginInvoke(new Action(LogScroll.ScrollToBottom), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void StartTimers()
        {
            try { if (_sysMonTimer != null) { _sysMonTimer.Stop(); _sysMonTimer = null; } _sysMonTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) }; _sysMonTimer.Tick += (s, e) => UpdateSysMonitor(); _sysMonTimer.Start(); UpdateSysMonitor(); } catch { }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buf);

        private void UpdateSysMonitor()
        {
            try
            {
                if (MemoryText == null) return;
                var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref ms) && ms.ullTotalPhys > 0)
                {
                    double total = ms.ullTotalPhys / 1073741824.0;
                    double used = (ms.ullTotalPhys - ms.ullAvailPhys) / 1073741824.0;
                    MemoryText.Text = $"{used:F1} / {total:F1} GiB";
                }
                else
                {
                    var mi = GC.GetGCMemoryInfo();
                    MemoryText.Text = $"{mi.MemoryLoadBytes / 1073741824.0:F1} / {mi.TotalAvailableMemoryBytes / 1073741824.0:F1} GiB";
                }
            }
            catch { }
        }

        private static string GetWindowsVersionName()
        {
            try
            {
                var v = Environment.OSVersion.Version;
                if (v.Major >= 10)
                {
                    if (v.Build >= 22000) return "Windows 11";
                    return "Windows 10";
                }
                if (v.Major == 6)
                {
                    return v.Minor switch
                    {
                        3 => "Windows 8.1",
                        2 => "Windows 8",
                        1 => "Windows 7",
                        _ => "Windows Vista",
                    };
                }
                return $"Windows {v.Major}.{v.Minor}";
            }
            catch { return "Windows"; }
        }

        private void InitializeLauncherCore()
        {
            RestrictNicknameFields();
            _settings = AppSettings.Load();
            if (string.IsNullOrWhiteSpace(_settings.Language))
                _settings.Language = _settings.IsFirstRun && System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ru" ? "en" : "ru";
            Lang.Current = _settings.Language;
            FillColorPresets();
            ApplyThemeFromSettings();
            ApplyLanguage();
            StartTimers();


            if (_settings.IsFirstRun) { _ = AnimateTerminalText(TopLeftTitleText, "LARPLAND LAUNCHER"); ShowSetupPanel(); RunStartupChecks(); }
            else
            {
                UsernameBox.Text = _settings.Username;
                RamSlider.Value = _settings.RamMb > 0 ? _settings.RamMb : 4096;
                PathBox.Text = _settings.GamePath;
                if (!ReleaseVersion.IsValid(_settings.ModpackVersion)) _settings.ModpackVersion = "0.0";
                if (NicknameAccepted()) SwitchToMain();
                else { _nicknameNeedsRepair = true; LoginGridState(); }
            }
        }

        private void ShowSetupPanel()
        {
            SetupPanel.Visibility = Visibility.Visible;
            LoginPanel.Visibility = Visibility.Hidden;
            MainPanel.Visibility = Visibility.Hidden;
            TopButtons.Visibility = Visibility.Collapsed;

            SetupPathBox.Text = _settings.GamePath;

            SetupPanel.Opacity = 0;
            TweenOpacity(SetupPanel, 0, 1, 800, OutQuart, 200);
        }

        private void BtnSetupSelectFolder_Click(object s, RoutedEventArgs e)
        { var d = new OpenFolderDialog(); if (d.ShowDialog() == true) SetupPathBox.Text = ResolveGamePath(d.FolderName); }

        private static string ResolveGamePath(string chosen)
        {
            if (string.IsNullOrWhiteSpace(chosen)) return "";
            chosen = chosen.Trim();
            string trimmed = chosen.TrimEnd('\\', '/');
            if (string.Equals(Path.GetFileName(trimmed), "BattleCraft", StringComparison.OrdinalIgnoreCase))
                return trimmed;
            return Path.Combine(chosen, "BattleCraft");
        }

        private async Task PrepareGameFolderAsync(string path)
        {
            try
            {
                await Task.Run(() =>
                {
                    if (Directory.Exists(path)) Directory.Delete(path, true);
                    Directory.CreateDirectory(path);
                });
            }
            catch (Exception ex) { LogError(Lang.F("Ошибка подготовки папки игры: {0}", ex.Message)); }
        }

        private async void BtnSetupMicrosoft_Click(object s, RoutedEventArgs e)
        {
            try
            {
                var handler = JELoginHandlerBuilder.BuildDefault();
                var sessionObj = await handler.AuthenticateInteractively();

                if (sessionObj == null || string.IsNullOrEmpty(sessionObj.Username)) return;

                _settings.Username = sessionObj.Username;
                _settings.UserType = "msa";
                AppSettings.Save(_settings);

                SetupUsernameBox.Text = sessionObj.Username;
                SetupUsernameBox.IsEnabled = false;
                SetupUsernameBox.Opacity = 0.5;
                await ShowCustomDialog(Lang.F("Авторизован как: {0}", sessionObj.Username));
            }
            catch (Exception ex)
            {
                await ShowCustomDialog(Lang.F("Ошибка авторизации: {0}", ex.Message));
            }
        }

        private async void BtnSetupComplete_Click(object s, RoutedEventArgs e)
        {
            string nick = SetupUsernameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(nick)) { await ShowCustomDialog(Lang.T("Авторизуйтесь через Microsoft или введите никнейм!")); return; }
            if (_settings.UserType != "msa" && !Nickname.IsValid(nick)) { await ShowCustomDialog(Lang.T(Nickname.RuleMessage)); return; }
            string path = ResolveGamePath(SetupPathBox.Text);
            if (string.IsNullOrWhiteSpace(path)) { await ShowCustomDialog(Lang.T("Выберите папку для игры!")); return; }
            if (!await EnsureFreeSpace(path, DiskSpace.ClientRequiredBytes)) return;

            if (!string.Equals(path, _settings.GamePath, StringComparison.OrdinalIgnoreCase))
            {
                await PrepareGameFolderAsync(path);
                _settings.IsModpackInstalled = false;
                _settings.ModpackVersion = "0.0";
            }

            _settings.GamePath = path; _settings.RamMb = 4096;

            if (_settings.UserType != "msa" || _settings.Username != nick)
            {
                _settings.Username = nick;
                _settings.UserType = "offline";
            }

            AppSettings.Save(_settings);

            TopButtons.Visibility = Visibility.Visible;
            UsernameBox.Text = nick; RamSlider.Value = 4096; SetupPathBox.Text = path; PathBox.Text = path;
            _ = AnimateTerminalText(TopLeftTitleText, "LARPLAND LAUNCHER");

            TweenOpacity(SetupPanel, 1, 0, 220, InCubic, 0, () =>
            {
                SetupPanel.Visibility = Visibility.Hidden;
                SetupPanel.Opacity = 1;
                SwitchToMain();
            });
        }

        public sealed class ColorPreset
        {
            public string Name { get; init; } = "";
            public string Primary { get; init; } = "";
            public string Accent { get; init; } = "";
            public Brush PrimarySwatch { get; init; } = Brushes.Transparent;
            public Brush AccentSwatch { get; init; } = Brushes.Transparent;
            public bool IsCustom { get; init; }
        }

        private static Brush SwatchBrush(string hex)
        {
            try
            {
                var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                b.Freeze();
                return b;
            }
            catch { return Brushes.Transparent; }
        }

        private void FillColorPresets()
        {
            if (ColorPresetCombo == null) return;
            ColorPresetCombo.ItemsSource = null;
            ColorPresetCombo.MaxDropDownHeight = 480;
            (string name, string tag)[] presets =
            {
                ("Глубина",   "#0A0F17|#57C7F2"),
                ("Лёд",       "#08121A|#8FE3FF"),
                ("Циан",      "#04121A|#22D3EE"),
                ("Аква",      "#071418|#3FD9C9"),
                ("Бирюза",    "#061414|#45E0D0"),
                ("Неон",      "#080C18|#4DA3FF"),
                ("Кобальт",   "#0A1020|#5B8CFF"),
                ("Полночь",   "#060A14|#3E7CB1"),
                ("Индиго",    "#0C0F1E|#8E9CD6"),
                ("Сталь",     "#0E1319|#9FB6C6"),
                ("Туман",     "#0D1218|#C3D8E6"),
                ("Мята",      "#081512|#6FE3A8"),
                ("Аметист",   "#0E0C1A|#A78BFA"),
                ("Пурпур",    "#120A18|#E879C6"),
                ("Янтарь",    "#161008|#F2B45C"),
                ("Малина",    "#15090D|#FF6B8A"),
            };
            var list = new List<ColorPreset>();
            foreach (var (name, tag) in presets)
            {
                var p = tag.Split('|');
                list.Add(new ColorPreset
                {
                    Name = Lang.T(name),
                    Primary = p[0],
                    Accent = p[1],
                    PrimarySwatch = SwatchBrush(p[0]),
                    AccentSwatch = SwatchBrush(p[1])
                });
            }
            foreach (var custom in _settings.CustomPresets)
            {
                list.Add(new ColorPreset
                {
                    Name = custom.Name,
                    Primary = custom.Primary,
                    Accent = custom.Accent,
                    PrimarySwatch = SwatchBrush(custom.Primary),
                    AccentSwatch = SwatchBrush(custom.Accent),
                    IsCustom = true
                });
            }
            ColorPresetCombo.ItemsSource = list;
        }

        private void BtnSavePreset_Click(object s, RoutedEventArgs e)
        {
            string name = (PresetNameBox.Text ?? "").Trim();
            if (name.Length == 0) { Log(Lang.T("Введите имя пресета.")); return; }

            string primary = NormalizeHex(PrimaryColorBox.Text, _settings.PrimaryColor ?? DefPrimary);
            string accent = NormalizeHex(AccentColorBox.Text, _settings.AccentColor ?? DefAccent);

            var existing = _settings.CustomPresets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null) { existing.Primary = primary; existing.Accent = accent; }
            else _settings.CustomPresets.Add(new CustomPreset { Name = name, Primary = primary, Accent = accent });

            AppSettings.Save(_settings);
            FillColorPresets();
            SelectPresetByColors(primary, accent);
            Log(Lang.F("Пресет «{0}» сохранён.", name));
        }

        private void BtnDeletePreset_Click(object s, RoutedEventArgs e)
        {
            if (ColorPresetCombo.SelectedItem is not ColorPreset cp || !cp.IsCustom)
            { Log(Lang.T("Удалить можно только свой пресет.")); return; }

            _settings.CustomPresets.RemoveAll(p => string.Equals(p.Name, cp.Name, StringComparison.OrdinalIgnoreCase));
            AppSettings.Save(_settings);
            FillColorPresets();
            SelectPresetByColors(_settings.PrimaryColor ?? DefPrimary, _settings.AccentColor ?? DefAccent);
            PresetNameBox.Text = "";
            Log(Lang.F("Пресет «{0}» удалён.", cp.Name));
        }

        private static string NormalizeHex(string? value, string fallback)
        {
            try { var c = (Color)ColorConverter.ConvertFromString(value); return $"#{c.R:X2}{c.G:X2}{c.B:X2}"; }
            catch { return fallback; }
        }

        private void SelectPresetByColors(string primary, string accent)
        {
            if (ColorPresetCombo == null) return;
            ColorPresetCombo.SelectionChanged -= ColorPreset_Changed;
            ColorPresetCombo.SelectedItem = null;
            foreach (var item in ColorPresetCombo.Items)
            {
                if (item is ColorPreset cp
                    && string.Equals(cp.Primary, primary, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(cp.Accent, accent, StringComparison.OrdinalIgnoreCase))
                {
                    ColorPresetCombo.SelectedItem = item;
                    break;
                }
            }
            ColorPresetCombo.SelectionChanged += ColorPreset_Changed;
            if (ColorPresetCombo.SelectedItem is ColorPreset sel && sel.IsCustom) PresetNameBox.Text = sel.Name;
        }

        private static void SelectLangCombo(ComboBox combo, string code)
        {
            if (combo == null) return;
            foreach (ComboBoxItem it in combo.Items)
                if (it.Tag as string == code) { combo.SelectedItem = it; break; }
        }

        private void LangCombo_Changed(object s, SelectionChangedEventArgs e)
        {
            if (s is not ComboBox cb) return;
            if (cb.SelectedItem is not ComboBoxItem item || item.Tag is not string code) return;
            if (_settings == null || code == Lang.Current) return;
            Lang.Current = code;
            _settings.Language = code;
            AppSettings.Save(_settings);
            FillColorPresets();
            ApplyThemeFromSettings();
            ApplyLanguage();
        }

        private void ApplyLanguage()
        {
            SelectLangCombo(SetupLangCombo, Lang.Current);
            SelectLangCombo(SettingsLangCombo, Lang.Current);

            LoginMsLabel.Text = Lang.T("Лицензия (Microsoft):");
            LoginMsBtn.Content = Lang.T("Войти через Microsoft");
            LoginNickLabel.Text = Lang.T("ИЛИ Пиратка (Никнейм):");
            LoginOfflineBtn.Content = Lang.T("Войти оффлайн");

            SetupMsLabel.Text = Lang.T("Лицензия (Microsoft):");
            SetupMsBtn.Content = Lang.T("Войти через Microsoft");
            SetupNickLabel.Text = Lang.T("ИЛИ Пиратка (Никнейм):");
            SetupFolderLabel.Text = Lang.T("Папка для Minecraft:");
            SetupStartBtn.Content = Lang.T("НАЧАТЬ");

            BtnReinstallText.Text = Lang.T("Перекачать сборку");
            BtnSettingsText.Text = Lang.T("Настройки");
            BtnChecksText.Text = Lang.T("Проверка системы");
            LoaderWarnText.Text = Lang.F("Установка библиотек NeoForge. Этот этап займёт от 1 до 5 минут.");
            SetPlayState(_gameProcess == null ? "idle" : "running");

            SettingsTitleRun.Text = Lang.T("настройки");
            AppearanceHeaderRun.Text = Lang.T("ВНЕШНИЙ ВИД");
            SystemHeaderRun.Text = Lang.T("СИСТЕМА");
            PresetsLabel.Text = Lang.T("Готовые пресеты:");
            PresetHintLabel.Text = Lang.T("Свой пресет запоминает текущие цвета под именем.");
            BtnSavePreset.Content = Lang.T("Сохранить");
            BtnDeletePreset.Content = Lang.T("Удалить");
            ManualColorsLabel.Text = Lang.T("Ручная настройка цветов:");
            PrimaryHexLabel.Text = Lang.T("Основной (HEX):");
            AccentHexLabel.Text = Lang.T("Акцент (HEX):");
            BloomEnabledCheck.Content = Lang.T("Включить свечение");
            BloomStrengthLabel.Text = Lang.T("Сила свечения:");
            ConsoleOpacityLabel.Text = Lang.T("Прозрачность терминала:");
            BgLabel.Text = Lang.T("Фон:");
            BgItemAnimated.Content = Lang.T("Пиксельная глубина");
            BgItemGradient.Content = Lang.T("Переливы темы");
            BgItemPlain.Content = Lang.T("Минимализм");
            LangLabel.Text = Lang.T("Язык:");
            RamLabel.Text = Lang.T("Выделенная память (RAM МБ):");
            GamePathLabel.Text = Lang.T("Путь к игре:");
            BtnResetAll.Content = Lang.T("СБРОСИТЬ ВСЕ НАСТРОЙКИ");
            BtnSaveClose.Content = Lang.T("СОХРАНИТЬ И ЗАКРЫТЬ");

            ChecksTitleRun.Text = Lang.T("проверка системы");
            BtnRecheckText.Text = Lang.T("Проверить снова");
            BtnOpenLogText.Text = Lang.T("Открыть лог");
            BtnCloseChecksText.Text = Lang.T("Закрыть");
            CustomDialogBtnCancel.Content = Lang.T("Отмена");
        }

        private void ApplyThemeFromSettings()
        {
            string p = string.IsNullOrWhiteSpace(_settings.PrimaryColor) ? DefPrimary : _settings.PrimaryColor!;
            string a = string.IsNullOrWhiteSpace(_settings.AccentColor) ? DefAccent : _settings.AccentColor!;
            if (string.Equals(p, "#0D0D1E", StringComparison.OrdinalIgnoreCase) && string.Equals(a, "#BB86FC", StringComparison.OrdinalIgnoreCase))
            { p = DefPrimary; a = DefAccent; }
            ApplyPrimaryColor(p, false); ApplyAccentColor(a, false);
            ApplyBloom(_settings.BloomEnabled ?? true, _settings.BloomStrength ?? 60, false);
            if (PrimaryColorBox != null) PrimaryColorBox.Text = p.ToUpper();
            if (AccentColorBox != null) AccentColorBox.Text = a.ToUpper();
            if (BloomEnabledCheck != null) BloomEnabledCheck.IsChecked = _settings.BloomEnabled ?? true;
            if (BloomStrengthSlider != null) BloomStrengthSlider.Value = _settings.BloomStrength ?? 60;

            double consolePct = (_settings.ConsoleOpacity ?? 1.0) * 100.0;
            ApplyConsoleOpacity(consolePct, false);
            if (ConsoleOpacitySlider != null) ConsoleOpacitySlider.Value = consolePct;

            ApplyBackgroundMode(_settings.BackgroundMode, IsLoaded);

            if (BackgroundCombo != null)
            {
                BackgroundCombo.SelectionChanged -= BackgroundCombo_Changed;
                BackgroundCombo.SelectedItem = _settings.BackgroundMode switch
                {
                    "gradient" => BgItemGradient,
                    "plain" => BgItemPlain,
                    _ => BgItemAnimated
                };
                BackgroundCombo.SelectionChanged += BackgroundCombo_Changed;
            }

            SelectPresetByColors(p, a);
        }

        private void ApplyPrimaryColor(string hex, bool save = true)
        {
            try { var c = (Color)ColorConverter.ConvertFromString(hex);
                AnimateColorResource("PrimaryColor", c, onStep: cur => { _bgPrimary = cur; UpdateBackdropTheme(); }); AnimateBrushResource("PrimaryBrush", c);
                if (save) { _settings.PrimaryColor = hex; AppSettings.Save(_settings); }
            } catch (Exception ex) { LauncherLog.Write("[ERR] ApplyPrimaryColor: " + ex.Message); }
        }

        private readonly Dictionary<string, System.Windows.Threading.DispatcherTimer> _colorResTimers = new();

        private void AnimateThemeValue(string timerKey, Color from, Color to, int ms, Action<Color> apply)
        {
            if (_colorResTimers.TryGetValue(timerKey, out var old)) { old.Stop(); _colorResTimers.Remove(timerKey); }

            if (from == to || !IsLoaded)
            {
                apply(to);
                return;
            }

            var sw = Stopwatch.StartNew();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            timer.Tick += (s, e) =>
            {
                double f = Math.Min(1, sw.ElapsedMilliseconds / (double)ms);
                double ease = 1 - Math.Pow(1 - f, 3);
                var cur = Color.FromRgb(
                    (byte)(from.R + (to.R - from.R) * ease),
                    (byte)(from.G + (to.G - from.G) * ease),
                    (byte)(from.B + (to.B - from.B) * ease));
                apply(cur);
                if (f >= 1)
                {
                    timer.Stop();
                    _colorResTimers.Remove(timerKey);
                }
            };
            _colorResTimers[timerKey] = timer;
            timer.Start();
        }

        private void AnimateBrushResource(string key, Color to, int ms = 600)
        {
            Color from = this.Resources[key] is SolidColorBrush ob ? ob.Color : to;
            AnimateThemeValue("b:" + key, from, to, ms, cur =>
            {
                var b = new SolidColorBrush(cur);
                b.Freeze();
                this.Resources[key] = b;
            });
        }

        private void AnimateColorResource(string key, Color to, int ms = 600, Action<Color>? onStep = null)
        {
            Color from = this.Resources[key] is Color c ? c : to;
            AnimateThemeValue("c:" + key, from, to, ms, cur =>
            {
                this.Resources[key] = cur;
                onStep?.Invoke(cur);
            });
        }

        private void ApplyAccentColor(string hex, bool save = true)
        {
            try { var c = (Color)ColorConverter.ConvertFromString(hex);
                AnimateBrushResource("AccentBrush", c);
                AnimateColorResource("AccentColor", c,
                    onStep: cur =>
                    {
                        _accentArgb = (cur.R << 16) | (cur.G << 8) | cur.B;
                        _bgAccent = cur;
                        UpdateBackdropTheme();
                    });

                double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
                var onAccent = lum > 0.6 ? Color.FromRgb(0x10, 0x0C, 0x18) : Colors.White;
                AnimateBrushResource("OnAccentBrush", onAccent);
                if (save) { _settings.AccentColor = hex; AppSettings.Save(_settings); }
            } catch (Exception ex) { LauncherLog.Write("[ERR] ApplyAccentColor: " + ex.Message); }
        }

        private void ApplyBloom(bool on, double str, bool save = true)
        {
            double k = str / 100.0;
            this.Resources["BloomBlurRadius"] = 5 + 25 * k;
            this.Resources["BloomOpacity"] = on ? 0.2 + 0.8 * k : 0.0;
            this.Resources["SettingsBloomOpacity"] = on ? (0.2 + 0.8 * k) * 0.6 : 0.0;

            this.Resources["TitleBloomBlurRadius"] = 20 + 40 * k;
            this.Resources["TitleBloomOpacity"] = on ? 0.2 + 0.3 * k : 0.0;

            if (save) { _settings.BloomEnabled = on; _settings.BloomStrength = str; AppSettings.Save(_settings); }
            if (BloomStrengthSlider != null)
            {
                BloomStrengthSlider.IsEnabled = on;
                BloomStrengthSlider.Opacity = on ? 1.0 : 0.35;
            }
        }

        private void ApplyConsoleOpacity(double pct, bool save = true)
        {
            double o = pct / 100.0;
            if (PlayContentPanel != null) PlayContentPanel.Opacity = o;
            if (save) { _settings.ConsoleOpacity = o; AppSettings.Save(_settings); }
        }

        private void ConsoleOpacitySlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (IsLoaded) ApplyConsoleOpacity(e.NewValue); }

        private void BtnApplyPrimaryColor_Click(object s, RoutedEventArgs e) => ApplyPrimaryColor(PrimaryColorBox.Text);
        private void BtnApplyAccentColor_Click(object s, RoutedEventArgs e) => ApplyAccentColor(AccentColorBox.Text);

        private void ColorPreset_Changed(object s, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            if (ColorPresetCombo.SelectedItem is ColorPreset cp)
            {
                ApplyPrimaryColor(cp.Primary);
                ApplyAccentColor(cp.Accent);
                PrimaryColorBox.Text = cp.Primary.ToUpper();
                AccentColorBox.Text = cp.Accent.ToUpper();
                PresetNameBox.Text = cp.IsCustom ? cp.Name : "";
            }
        }

        private async void BtnResetAllSettings_Click(object s, RoutedEventArgs e)
        {
            if (await ShowCustomDialog(Lang.T("Сбросить все?"), "Сброс", true) != true) return;
            _settings = new AppSettings(); AppSettings.Save(_settings);
            MainWnd.Background = null; MainWnd.SetResourceReference(Control.BackgroundProperty, "PrimaryBrush"); Icon = null;
            CloseSettingsPanel();
            InitializeLauncherCore();
        }

        private void InitializeLauncher()
        {
            Log(Lang.F("Платформа: {0} ({1})", GetWindowsVersionName(), Environment.OSVersion.Version));
            Log(Lang.F("Дисплей: {0} Гц — анимации идут в такт монитору", Math.Round(_refreshHz)));
            _minecraftPath = new MinecraftPath(_settings.GamePath);

            var parameters = MinecraftLauncherParameters.CreateDefault(_minecraftPath, ResilientHttpClientFactory.Shared);
            if (_settings.DownloadLanes > 0)
                parameters.GameInstaller = new ParallelGameInstaller(
                    DownloadCheckers, _settings.DownloadLanes, DownloadQueueSize, ResilientHttpClientFactory.Shared);
            // WHY: обрыв загрузки оставляет обрезанный jar, а без сверки размера установщик
            // WHY: считает его готовым, и игра падает на SecureJar ещё до своего лога
            if (parameters.GameInstaller is GameInstallerBase installerBase)
            {
                installerBase.CheckFileSize = true;
                installerBase.CheckFileChecksum = _settings.RepairGameFiles;
            }
            _launcher = new MinecraftLauncher(parameters);
            _launcher.FileProgressChanged += (s, e) =>
            {
                string name = e.Name ?? "";
                int done = e.ProgressedTasks;
                int total = e.TotalTasks;

                long now = Environment.TickCount64;
                long prev = System.Threading.Interlocked.Read(ref _lastProgressTick);
                if (now - prev < 120) return;
                if (System.Threading.Interlocked.CompareExchange(ref _lastProgressTick, now, prev) != prev) return;

                double percent = total > 0 ? (double)done / total * 100 : -1;
                bool noBytes = now - System.Threading.Interlocked.Read(ref _lastByteTick) > 1500;
                bool debug = _settings.DebugConsole;
                Dispatcher.BeginInvoke(() =>
                {
                    if (debug && !string.IsNullOrEmpty(name)) Log(Lang.F("Файл: {0} ({1}/{2})", name, done, total));
                    else StatusText.Text = name;
                    if (percent >= 0 && noBytes) SetProgress(percent);
                }, System.Windows.Threading.DispatcherPriority.Background);
            };

            _launcher.ByteProgressChanged += (s, e) =>
            {
                long total = e.TotalBytes;
                long done = e.ProgressedBytes;
                if (total <= 0) return;

                long now = Environment.TickCount64;
                long prev = System.Threading.Interlocked.Read(ref _lastByteTick);
                if (now - prev < 200) return;
                if (System.Threading.Interlocked.CompareExchange(ref _lastByteTick, now, prev) != prev) return;

                double percent = (double)done / total * 100;

                double speed = 0;
                long lastBytes = System.Threading.Interlocked.Read(ref _lastByteCount);
                long lastTick = System.Threading.Interlocked.Read(ref _lastByteSpeedTick);
                if (done >= lastBytes && lastTick > 0 && now > lastTick)
                    speed = (done - lastBytes) * 1000.0 / (now - lastTick);
                System.Threading.Interlocked.Exchange(ref _lastByteCount, done);
                System.Threading.Interlocked.Exchange(ref _lastByteSpeedTick, now);

                bool logNow = now - System.Threading.Interlocked.Read(ref _lastNetLogTick) > 1500;
                if (logNow) System.Threading.Interlocked.Exchange(ref _lastNetLogTick, now);

                Dispatcher.BeginInvoke(() =>
                {
                    SetProgress(percent);
                    if (logNow)
                    {
                        string line = Lang.F("Загрузка {0:F0}% · {1} / {2}", percent, FileDownloader.FormatSize(done), FileDownloader.FormatSize(total));
                        if (speed > 0) line += $" · {FileDownloader.FormatSpeed(speed)}";
                        LogNet(line);
                    }
                }, System.Windows.Threading.DispatcherPriority.Background);
            };
        }

        private async void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            if (_gameProcess != null)
            {
                try { _gameProcess.Kill(entireProcessTree: true); } catch { }
                _gameProcess = null;
                SetPlayState("idle");
                StatusText.Text = Lang.T("Готов");
                SetProgress(0);
                return;
            }
            if (!_settings.HasGamePath) { await ShowCustomDialog(Lang.T("Выберите папку для игры в настройках!")); return; }
            if (!NicknameAccepted()) { await ShowCustomDialog(Lang.T(Nickname.RejectedMessage)); LoginGridState(); return; }

            BtnPlay.IsEnabled = false; SetBusy(true);
            bool didInstall = false;
            try
            {
                if (!Directory.Exists(_settings.GamePath)) Directory.CreateDirectory(_settings.GamePath);

                InitializeLauncher();

                if (!NeoForgeInstall.Present(_settings.GamePath))
                {
                    NeoForgeInstall.RemoveOtherProfiles(_settings.GamePath);
                    didInstall = true;
                    await InstallLoaderSilent();
                }
                if (!_settings.IsModpackInstalled) { didInstall = true; await InstallModpack(); }
                else if (_needsModpackUpdate) { didInstall = true; await InstallModpack(); _needsModpackUpdate = false; }

                if (didInstall) { Log(Lang.T("Готово!")); StatusText.Text = Lang.T("Установка завершена! Нажмите ИГРАТЬ."); SetProgress(0); SetPlayState("idle"); BtnPlay.IsEnabled = true; SetBusy(false); return; }

                StatusText.Text = Lang.T("Запуск..."); SetProgress(100);
                string? loaderProfile = await FindLoaderProfile();
                if (loaderProfile == null)
                {
                    Log(Lang.T("Профиль NeoForge не найден, лаунчер ставит его заново."));
                    await InstallLoaderSilent();
                    loaderProfile = await FindLoaderProfile();
                }
                if (loaderProfile == null)
                {
                    await ShowCustomDialog(Lang.T("Не удалось установить NeoForge. Проверьте подключение к сети и попробуйте ещё раз."));
                    StatusText.Text = Lang.T("Готов");
                    return;
                }

                MSession? mSession = null;
                if (_settings.UserType == "msa")
                {
                    ShowSpinnerOverlay(Lang.T("Загрузка профиля Minecraft"), Lang.T("Проверка лицензии Microsoft…"), false);
                    var handler = JELoginHandlerBuilder.BuildDefault();
                    dynamic? sessionObj = null;
                    try { sessionObj = await handler.AuthenticateSilently(); }
                    catch (Exception error)
                    {
                        LauncherLog.Write($"[WARN] Тихий вход Microsoft не прошёл: {error.Message}");
                    }

                    string? msaName = sessionObj is null ? null : (string?)sessionObj.Username;
                    if (sessionObj is not null && !string.IsNullOrEmpty(msaName))
                    {
                        mSession = new MSession();
                        mSession.Username = msaName;
                        mSession.AccessToken = sessionObj.AccessToken;
                        mSession.UUID = sessionObj.UUID;
                        mSession.UserType = "msa";
                        HideUpdateOverlay();
                    }
                    else
                    {
                        HideUpdateOverlay();
                        await ShowCustomDialog(Lang.T("Срок действия сессии истек. Пожалуйста, авторизуйтесь заново."));
                        SetProgress(0); SetPlayState("idle"); BtnPlay.IsEnabled = true; SetBusy(false);
                        LoginGridState();
                        return;
                    }
                }
                else
                {
                    mSession = MSession.CreateOfflineSession(_settings.Username);
                }

                PerformanceConfig.Apply(_settings.GamePath);

                string java = FindJava();
                if (!JavaRuntime.IsBundled(java))
                {
                    Log(Lang.T("Java сборки не найдена, лаунчер ставит её заново"));
                    await DownloadAndInstallJava();
                    java = FindJava();
                    if (!JavaRuntime.IsBundled(java))
                    {
                        await ShowCustomDialog(
                            Lang.T("Java для игры не установилась, запуск невозможен. Откройте «Проверка системы» и посмотрите, что мешает."),
                            Lang.T("Ошибка запуска"));
                        SetPlayState("idle"); BtnPlay.IsEnabled = true; SetBusy(false);
                        return;
                    }
                }

                var opt = new MLaunchOption { MaximumRamMb = _settings.RamMb, Session = mSession, JavaPath = java };
                Process game = await KeepDownloading(() => _launcher.CreateProcessAsync(loaderProfile, opt).AsTask());
                _gameProcess = game;
                InjectJvmArgs(game);

                game.StartInfo.CreateNoWindow = true;
                game.StartInfo.UseShellExecute = false;
                CatchGameOutput(game);
                game.StartInfo.Environment["FML_EARLY_WINDOW_DARK"] = "1";

                game.Start();
                game.BeginOutputReadLine();
                game.BeginErrorReadLine();
                DateTime started = DateTime.Now;
                _logLines.Clear(); LogTerminalText.Text = "";
                SetPlayState("running"); BtnPlay.IsEnabled = true; SetBusy(false);

                // WHY: отмена обнуляла поле, и продолжение уже убитого запуска гасило состояние
                // WHY: следующего: кнопка звала «Играть» под работающей игрой, а окно настроек
                // WHY: открывалось поверх неё и теряло правки на выходе Minecraft
                await game.WaitForExitAsync();
                if (_gameProcess != game) return;

                _gameProcess = null;
                SetPlayState("idle");
                StatusText.Text = Lang.T("Готов");
                _gameOutput.Close();
                ForgetRepairFlag(game.ExitCode, DateTime.Now - started);
                if (await RestartWithSafeJvm(game.ExitCode, DateTime.Now - started)) return;
                if (await RestartAfterRepair(game.ExitCode, DateTime.Now - started)) return;
                await ReportGameExit(game.ExitCode, DateTime.Now - started);
            }
            catch (OperationCanceledException) { Log(Lang.T("Установка отменена.")); StatusText.Text = Lang.T("Отменено"); SetPlayState("idle"); }
            catch (Exception ex) { await HandleErrorAsync(ex, Lang.T("Ошибка запуска")); }
            finally { HideUpdateOverlay(); SetProgress(0); BtnPlay.IsEnabled = true; SetBusy(false); }
        }

        private readonly GameOutput _gameOutput = new();

        private void CatchGameOutput(Process game)
        {
            game.StartInfo.RedirectStandardOutput = true;
            game.StartInfo.RedirectStandardError = true;
            game.StartInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
            game.StartInfo.StandardErrorEncoding = System.Text.Encoding.UTF8;

            _gameOutput.Begin(game.StartInfo.FileName + " " + game.StartInfo.Arguments);
            game.OutputDataReceived += (_, e) => TakeGameLine(e.Data);
            game.ErrorDataReceived += (_, e) => TakeGameLine(e.Data);
        }

        private void TakeGameLine(string? line)
        {
            if (string.IsNullOrEmpty(line)) return;

            _gameOutput.Add(line);
            if (_settings.DebugConsole) Dispatcher.BeginInvoke(() => Log(line));
        }

        // WHY: JVM без Shenandoah или с нехваткой памяти под кучу падает мгновенно и молча,
        // WHY: до создания logs/latest.log, поэтому разбираем её собственный вывод и повторяем
        private static readonly string[] JvmRefusedMarkers =
        {
            "Unrecognized VM option",
            "Could not create the Java Virtual Machine",
            "Could not reserve enough space for object heap",
            "Error occurred during initialization of VM",
            "Unrecognized option"
        };

        private const string HeapRefusedMarker = "Could not reserve enough space for object heap";
        private const int MinimalHeapMb = 2048;

        private async Task<bool> RestartWithSafeJvm(int exitCode, TimeSpan ran)
        {
            if (exitCode == 0 || ran > TimeSpan.FromSeconds(30)) return false;

            bool heapTooBig = _gameOutput.Mentions(HeapRefusedMarker);
            bool optionsRefused = !_settings.SafeJvm && _gameOutput.Mentions(JvmRefusedMarkers);
            if (!heapTooBig && !optionsRefused) return false;

            if (heapTooBig && !ShrinkHeap()) return false;
            if (optionsRefused) _settings.SafeJvm = true;

            AppSettings.Save(_settings);
            LauncherLog.Write($"[SYS] Java отказалась стартовать, повтор с другими настройками: память {_settings.RamMb} МБ, безопасный режим {_settings.SafeJvm}");
            Log(heapTooBig
                ? Lang.F("Java не смогла занять {0} МБ под игру. Пробую ещё раз с меньшим объёмом.", _settings.RamMb)
                : Lang.T("Java не приняла настройки запуска сборки. Пробую ещё раз с безопасными настройками."));

            await Task.Delay(TimeSpan.FromSeconds(1));
            BtnPlay_Click(this, new RoutedEventArgs());
            return true;
        }

        private bool ShrinkHeap()
        {
            int reduced = Math.Max(MinimalHeapMb, _settings.RamMb / 2);
            if (reduced >= _settings.RamMb) return false;

            _settings.RamMb = reduced;
            Dispatcher.BeginInvoke(() => { if (RamSlider != null) RamSlider.Value = reduced; });
            return true;
        }

        // WHY: Forge открывает каждый jar через SecureJar, и первый же обрезанный файл роняет
        // WHY: запуск за секунду - лечится только повторной закачкой со сверкой контрольных сумм
        private static readonly string[] BrokenJarMarkers =
        {
            "UnionFileSystemProvider",
            "SecureJar",
            "ZipException",
            "zip END header not found",
            "Invalid or corrupt jarfile",
            "Could not find or load main class"
        };

        private async Task<bool> RestartAfterRepair(int exitCode, TimeSpan ran)
        {
            if (exitCode == 0 || ran > TimeSpan.FromSeconds(30)) return false;
            if (_settings.RepairGameFiles || !_gameOutput.Mentions(BrokenJarMarkers)) return false;

            List<string> broken = BrokenJars.Find(_settings.GamePath);
            if (broken.Count == 0) return false;

            bool modsHurt = broken.Any(jar => jar.Contains(Path.DirectorySeparatorChar + "mods" + Path.DirectorySeparatorChar));
            _settings.RepairGameFiles = true;
            AppSettings.Save(_settings);

            Log(Lang.F("Повреждённых файлов после обрыва загрузки: {0}. Перекачиваю их и запускаю снова.", broken.Count));
            BrokenJars.Remove(broken);
            SetBusy(true);

            try
            {
                if (modsHurt)
                {
                }
                else
                {
                    await InstallLoaderSilent();
                }
            }
            catch (Exception error)
            {
                SetBusy(false);
                await HandleErrorAsync(error, Lang.T("Ошибка восстановления файлов"));
                return true;
            }

            SetBusy(false);
            await Task.Delay(TimeSpan.FromSeconds(1));
            BtnPlay_Click(this, new RoutedEventArgs());
            return true;
        }

        private void ForgetRepairFlag(int exitCode, TimeSpan ran)
        {
            if (!_settings.RepairGameFiles) return;
            if (exitCode != 0 && ran < TimeSpan.FromSeconds(30)) return;

            _settings.RepairGameFiles = false;
            AppSettings.Save(_settings);
            LauncherLog.Write("[SYS] Файлы игры в порядке, сверка контрольных сумм снова выключена");
        }

        private static readonly TimeSpan SuspiciouslyShortSession = TimeSpan.FromSeconds(40);

        // WHY: Minecraft уносит свои ошибки в собственный лог и молча закрывается, а игрок
        // WHY: видит только вернувшийся лаунчер и не понимает, что вообще произошло
        private async Task ReportGameExit(int exitCode, TimeSpan ran)
        {
            bool crashed = exitCode != 0;
            bool diedOnLoading = !crashed && ran < SuspiciouslyShortSession
                && !GameLogTail.ReachedMenu(_settings.GamePath);

            LauncherLog.Write($"[SYS] Игра завершилась с кодом {exitCode} через {ran.TotalSeconds:F0} с");
            if (!crashed && !diedOnLoading) return;

            string report = GameLogTail.NewestCrashReport(_settings.GamePath, TimeSpan.FromMinutes(5));
            List<string> problems = GameLogTail.Problems(_settings.GamePath, 6);
            if (problems.Count == 0) problems = _gameOutput.Tail(8);

            string message = crashed
                ? Lang.F("Игра закрылась с ошибкой (код {0}) через {1} секунд.", exitCode, (int)ran.TotalSeconds)
                : Lang.F("Игра закрылась сама через {0} секунд, до загрузки меню.", (int)ran.TotalSeconds);

            if (problems.Count > 0)
                message += "\n\n" + Lang.T("Последние ошибки из лога игры:") + "\n" + string.Join("\n", problems);

            if (!string.IsNullOrEmpty(report))
                message += "\n\n" + Lang.F("Отчёт игры: {0}", report);

            message += "\n\n" + Lang.F("Вывод игры: {0}", _gameOutput.Path_);
            if (File.Exists(GameLogTail.LatestLogPath(_settings.GamePath)))
                message += "\n" + Lang.F("Полный лог: {0}", GameLogTail.LatestLogPath(_settings.GamePath));

            Log(message.Replace("\n", " "));
            await ShowCustomDialog(message, Lang.T("Игра завершилась с ошибкой"));
        }

        private void SetBusy(bool busy)
        {
            _isBusy = busy;
            BtnReinstall.IsEnabled = !busy;
            BtnSettingsOpen.IsEnabled = !busy;
        }

        private void SetPlayState(string st)
        {
            if (st == "running")
            {
                BtnPlay.Content = Lang.T("ОТМЕНА");
                SetButtonIcon(BtnPlay, "IconStop");
                BtnPlay.Background = new SolidColorBrush(Color.FromRgb(180, 60, 60));
            }
            else
            {
                BtnPlay.Content = Lang.T("ИГРАТЬ");
                SetButtonIcon(BtnPlay, "IconPlay");
                BtnPlay.SetResourceReference(Control.BackgroundProperty, "AccentBrush");
            }
        }

        private void SetButtonIcon(Button btn, string geometryKey)
        {
            if (TryFindResource(geometryKey) is Geometry icon) btn.Tag = icon;
        }

        private void InjectJvmArgs(Process p)
        {
            if (string.IsNullOrEmpty(p.StartInfo.Arguments)) return;
            if (_settings.SafeJvm)
            {
                Log(Lang.T("Сборщик мусора: по умолчанию (безопасный режим Java)"));
                return;
            }

            bool preferShenandoah = _settings.RamMb >= ShenandoahHeapThresholdMb;
            string a = preferShenandoah ? StripDefaultCollectorArgs(p.StartInfo.Arguments) : p.StartInfo.Arguments;
            string jvm = string.Join(" ", preferShenandoah ? _shenandoahArgs.Concat(_jvmArgs) : _jvmArgs);
            Log(Lang.T(preferShenandoah ? "Сборщик мусора: Shenandoah" : "Сборщик мусора: G1"));
            int i = a.IndexOf(" -cp "); if (i < 0) i = a.IndexOf(" -classpath ");
            p.StartInfo.Arguments = i > 0 ? a.Insert(i, " " + jvm) : jvm + " " + a;
        }

        private static string StripDefaultCollectorArgs(string arguments) => _defaultCollectorArgs.Replace(arguments, "");

        private async Task<bool> EnsureFreeSpace(string path, long requiredBytes)
        {
            if (string.IsNullOrWhiteSpace(path))
                return true;

            if (DiskSpace.HasEnough(path, requiredBytes, out long freeBytes))
                return true;

            await ShowCustomDialog(DiskSpace.BuildShortageMessage(path, requiredBytes, freeBytes));
            return false;
        }

        private string FindJava() => JavaRuntime.Find(_settings.GamePath);

        private void EnsureProfiles() { string p = Path.Combine(_settings.GamePath, "launcher_profiles.json"); if (!File.Exists(p)) File.WriteAllText(p, "{\"profiles\":{}}"); }

        // WHY: CmlLib качает ассеты в двенадцать потоков, и у игроков с проверкой HTTPS в
        // WHY: антивирусе соединение рвётся на середине; скачанное остаётся на диске, поэтому
        // WHY: повтор продолжает с места обрыва, а каждая неудача сужает число потоков
        private const int DownloadCheckers = 4;
        private const int DownloadQueueSize = 2048;
        private static readonly int[] DownloadLaneSteps = { 4, 2, 1 };
        private const int DownloadAttempts = 12;

        private async Task KeepDownloading(Func<Task> download)
        {
            await KeepDownloading(async () => { await download(); return true; });
        }

        private async Task<T> KeepDownloading<T>(Func<Task<T>> download)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    T result = await download();
                    RememberDownloadLanes();
                    return result;
                }
                catch (Exception error) when (attempt < DownloadAttempts && NetworkTrouble.Looks(error))
                {
                    LauncherLog.Write($"[WARN] Загрузка оборвалась на попытке {attempt}: {NetworkTrouble.Deepest(error).Message}");
                    NarrowDownloadLanes();
                    Log(Lang.F("Соединение оборвалось, продолжаю с места обрыва: попытка {0} из {1}, потоков загрузки {2}",
                        attempt + 1, DownloadAttempts, _settings.DownloadLanes));
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(8, attempt * 2)));
                }
            }
        }

        private void NarrowDownloadLanes()
        {
            int current = _settings.DownloadLanes;
            int next = DownloadLaneSteps.FirstOrDefault(lanes => current == 0 || lanes < current);
            _settings.DownloadLanes = next == 0 ? DownloadLaneSteps[^1] : next;
            _downloadLanesChanged = true;
            InitializeLauncher();
        }

        private void RememberDownloadLanes()
        {
            if (!_downloadLanesChanged) return;

            _downloadLanesChanged = false;
            AppSettings.Save(_settings);
            LauncherLog.Write($"[SYS] Число потоков загрузки закреплено: {_settings.DownloadLanes}");
        }

        private bool _downloadLanesChanged;

        private async Task<string?> FindLoaderProfile()
        {
            var known = await _launcher.GetAllVersionsAsync();
            var profile = known.FirstOrDefault(v => v.Name == FULL_ID)
                ?? known.FirstOrDefault(v => v.Name.Contains(MC) && v.Name.ToLower().Contains("forge"));
            return profile?.Name;
        }

        private async Task InstallLoaderSilent()
        {
            try
            {
                SetProgress(0);
                StatusText.Text = Lang.T("Загрузка файлов Minecraft...");
                await KeepDownloading(() => _launcher.InstallAsync(MC).AsTask());
                EnsureProfiles();

                StatusText.Text = Lang.T("Загрузка установщика Forge...");
                string jar = Path.Combine(Path.GetTempPath(), "forge_installer.jar");
                if (File.Exists(jar)) File.Delete(jar);
                var forgeDl = new FileDownloader();
                forgeDl.LogMessage += LogNet;
                forgeDl.ProgressChanged += p => Dispatcher.BeginInvoke(() => { GameProgressBar.IsIndeterminate = false; SetProgress(p); });
                await forgeDl.DownloadFileAsync(LOADER_JAR_URL, jar);

                StatusText.Text = Lang.T("Установка библиотек Forge...");
                Log(Lang.T("Этот этап займёт от 1 до 5 минут, не закрывайте лаунчер."));
                ShowLoaderWarning(true);
                await RunLoaderInstaller(jar);
                ShowLoaderWarning(false);

                await _launcher.GetAllVersionsAsync();
                try { File.Delete(jar); } catch { }
                CleanInstallerLog();
                Log(Lang.T("Forge установлен."));
            }
            finally { ShowLoaderWarning(false); GameProgressBar.IsIndeterminate = false; }
        }

        private async Task RunLoaderInstaller(string jar)
        {
            var psi = new ProcessStartInfo
            {
                FileName = FindJava(),
                Arguments = $"-jar \"{jar}\" --installClient \"{_settings.GamePath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };

            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            DataReceivedEventHandler onData = (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                LauncherLog.Write($"[FORGE] {e.Data.Trim()}");
            };
            proc.OutputDataReceived += onData;
            proc.ErrorDataReceived += onData;

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            var creep = StartCreepProgress(95, 200);
            try { await proc.WaitForExitAsync(); }
            finally { StopCreepProgress(creep); SetProgress(100); }

            if (proc.ExitCode != 0)
                throw new Exception(Lang.F("Установщик Forge завершился с кодом {0}", proc.ExitCode));
        }

        private System.Windows.Threading.DispatcherTimer StartCreepProgress(double to, double seconds)
        {
            double value = 0;
            SetProgress(0);
            double step = (to - value) / (seconds * 2);
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += (s, e) =>
            {
                value = Math.Min(to, value + step);
                GameProgressBar.IsIndeterminate = false;
                SetProgress(value);
            };
            timer.Start();
            return timer;
        }

        private static void StopCreepProgress(System.Windows.Threading.DispatcherTimer? timer)
        {
            try { timer?.Stop(); } catch { }
        }

        private void ShowLoaderWarning(bool show)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => ShowLoaderWarning(show)); return; }
            if (LoaderWarningPanel != null) LoaderWarningPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private void CleanInstallerLog()
        {
            try { foreach (var dir in new[] { Path.GetTempPath(), _settings.GamePath, AppDomain.CurrentDomain.BaseDirectory })
                foreach (var f in Directory.GetFiles(dir, "*.jar.log")) try { File.Delete(f); } catch { }
            } catch { }
        }

        private async Task HandleErrorAsync(Exception ex, string context)
        {
            string logPath = LauncherLog.WriteCrash(context, ex);

            bool isJavaError = IsJavaMissingError(ex);

            if (isJavaError)
            {
                if (await ShowCustomDialog(Lang.T("Похоже, что отсутствует Java. Скачать и установить Java 21 автоматически?"), "Ошибка Java", true))
                {
                    await DownloadAndInstallJava();
                    return;
                }
            }

            string body = NetworkTrouble.Looks(ex)
                ? context + ": " + NetworkTrouble.Explain(ex)
                : Lang.F("{0}: {1}", context, ex.Message);

            if (await ShowCustomDialog(body + "\n\n" + Lang.T("Открыть файл с логами?"), "Ошибка", true))
            {
                string toOpen = !string.IsNullOrEmpty(logPath) && File.Exists(logPath) ? logPath : AppSettings.GetConfigDir();
                OpenInShell(toOpen);
            }
        }

        private static bool IsJavaMissingError(Exception ex)
        {
            const int ERROR_FILE_NOT_FOUND = 2;
            const int ERROR_PATH_NOT_FOUND = 3;
            for (Exception? e = ex; e != null; e = e.InnerException)
            {
                if (e is System.ComponentModel.Win32Exception w &&
                    (w.NativeErrorCode == ERROR_FILE_NOT_FOUND || w.NativeErrorCode == ERROR_PATH_NOT_FOUND))
                    return true;
                if (e is FileNotFoundException || e is DirectoryNotFoundException)
                    return true;
            }
            return ex.Message.Contains("не удается найти") || ex.Message.Contains("cannot find the file");
        }

        private async Task DownloadAndInstallJava()
        {
            try
            {
                SetBusy(true);
                GameProgressBar.IsIndeterminate = true;
                StatusText.Text = Lang.T("Скачивание Java 21...");

                string tempZip = Path.Combine(Path.GetTempPath(), "jre21.zip");
                if (File.Exists(tempZip)) File.Delete(tempZip);

                var downloader = new FileDownloader();
                downloader.LogMessage += LogNet;
                downloader.ProgressChanged += (p) => Dispatcher.BeginInvoke(() => { GameProgressBar.IsIndeterminate = false; GameProgressBar.Value = p; });

                await downloader.DownloadFileAsync("https://api.adoptium.net/v3/binary/latest/" + GameVersions.JavaMajor + "/ga/windows/x64/jre/hotspot/normal/eclipse", tempZip);

                StatusText.Text = Lang.T("Установка Java 21...");
                GameProgressBar.IsIndeterminate = true;
                await Task.Run(() =>
                {
                    string targetDir = JavaRuntime.BundledFolder(_settings.GamePath);
                    if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
                    Directory.CreateDirectory(targetDir);

                    System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, targetDir);
                    File.Delete(tempZip);

                    var subDirs = Directory.GetDirectories(targetDir);
                    if (subDirs.Length == 1)
                    {
                        string extractedDir = subDirs[0];
                        foreach (var file in Directory.GetFiles(extractedDir)) File.Move(file, Path.Combine(targetDir, Path.GetFileName(file)));
                        foreach (var dir in Directory.GetDirectories(extractedDir)) Directory.Move(dir, Path.Combine(targetDir, Path.GetFileName(dir)));
                        Directory.Delete(extractedDir);
                    }
                });

                await ShowCustomDialog(Lang.T("Java 21 успешно установлена! Попробуйте запустить игру снова."), "Успех");
            }
            catch (Exception ex)
            {
                await ShowCustomDialog(Lang.F("Ошибка установки Java: {0}", ex.Message));
            }
            finally
            {
                SetBusy(false);
                GameProgressBar.IsIndeterminate = false;
                GameProgressBar.Value = 0;
                StatusText.Text = Lang.T("Готов");
            }
        }

        private static readonly string[] ModpackDirs = { "mods", "config", "scripts", "kubejs", "defaultconfigs", "tacz", "tacz_backup" };

        private static readonly string[] PlayerOptionFiles = { "options.txt", "optionsof.txt", "optionsshaders.txt" };

        // WHY: распаковка молча заканчивалась ничем, когда архив резал антивирус или
        // WHY: обрывался диск, и игрок узнавал об этом только по пустой игре без модов
        private async Task InstallModpack()
        {
            if (!await EnsureFreeSpace(_settings.GamePath, DiskSpace.ClientRequiredBytes))
                throw new IOException(Lang.T("Недостаточно места на диске"));

            ModpackInstall.PrepareCache(_settings.GamePath);
            string archive = ModpackInstall.ArchivePath(_settings.GamePath);

            while (true)
            {
                try
                {
                    if (!ModpackInstall.ArchiveReadable(archive)) await DownloadModpackArchive(archive);

                    StatusText.Text = Lang.T("Очистка старых файлов...");
                    var playerFiles = ModpackInstall.TakePlayerFiles(_settings.GamePath);
                    ModpackInstall.WipeReplacedDirs(_settings.GamePath, Log);

                    StatusText.Text = Lang.T("Распаковка...");
                    GameProgressBar.IsIndeterminate = true;
                    string target = _settings.GamePath;
                    await Task.Run(() => ZipFile.ExtractToDirectory(archive, target, true));
                    GameProgressBar.IsIndeterminate = false;

                    ModpackInstall.RestorePlayerFiles(_settings.GamePath, playerFiles, Log);
                    ModpackInstall.VerifyExtracted(_settings.GamePath);
                    ModpackInstall.DropArchive(archive);

                    Log(Lang.T("Распаковка завершена!"));
                    _settings.IsModpackInstalled = true;
                    _settings.ModpackVersion = _onlineModpackVer != "0.0" ? _onlineModpackVer : _settings.ModpackVersion;
                    AppSettings.Save(_settings);
                    ShowModpackVersion();
                    return;
                }
                catch (InvalidDataException error)
                {
                    LauncherLog.Write($"[ERROR] Архив сборки повреждён: {error.Message}");
                    ModpackInstall.DropArchive(archive);
                    if (!await AskToContinueDownload(error)) throw new OperationCanceledException(Lang.T("Установка отменена пользователем."));
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    GameProgressBar.IsIndeterminate = false;
                    if (!await AskToContinueDownload(error)) throw new OperationCanceledException(Lang.T("Установка отменена пользователем."));
                }
            }
        }

        // WHY: частичный архив остаётся на диске специально - следующая попытка продолжает
        // WHY: загрузку с оборванного места, а не качает 250 мегабайт заново
        private async Task DownloadModpackArchive(string archive)
        {
            StatusText.Text = Lang.T("Загрузка сборки...");

            var downloader = new FileDownloader { ResumeExisting = true };
            downloader.LogMessage += LogNet;
            downloader.ProgressChanged += value => Dispatcher.BeginInvoke(() =>
            {
                GameProgressBar.IsIndeterminate = false;
                SetProgress(value);
            });

            await downloader.DownloadFileAsync(MODPACK_URL, archive);
        }

        private async Task<bool> AskToContinueDownload(Exception error)
        {
            return await ShowCustomDialog(
                Lang.F("Загрузка сборки оборвалась.\nОшибка: {0}\nПродолжить с места обрыва?", error.Message),
                "Ошибка скачивания", true);
        }

        private async Task AnimateTerminalText(TextBlock tb, string targetText)
        {
            tb.Text = "";
            string chars = "$?#!*%@^&~";
            var rnd = _rnd;
            for (int i = 0; i < targetText.Length; i++)
            {
                tb.Text = targetText.Substring(0, i) + chars[rnd.Next(chars.Length)] + "_";
                await Task.Delay(25);
                tb.Text = targetText.Substring(0, i + 1) + "_";
                await Task.Delay(25);
            }
            tb.Text = targetText;
        }

        private static TranslateTransform EnsureTabTransform(FrameworkElement element)
        {
            if (element.RenderTransform is TranslateTransform existing) return existing;

            var created = new TranslateTransform();
            element.RenderTransform = created;
            return created;
        }

        private async void SwitchToMain()
        {
            SetupPanel.Visibility = Visibility.Hidden; LoginPanel.Visibility = Visibility.Hidden;
            MainPanel.Visibility = Visibility.Visible; TopButtons.Visibility = Visibility.Visible;

            VersionText.Text = "";

            TweenOpacity(MainPanel, 0, 1, 320, OutQuart);
            if (SideBarTransform != null) TweenX(SideBarTransform, -70, 0, 520, OutCubic);
            TweenOpacity(TopButtons, 0, 1, 600, OutQuart, 300);
            TweenOpacity(BtnPlay, 0, 1, 600, OutQuart, 500);
            TweenOpacity(BtnGitHub, 0, 0.7, 600, OutQuart, 400);

            InitializeLauncher();
            RunStartupChecks();
            await CheckUpdates();

            if (TopLeftTitleText.Text != "LARPLAND LAUNCHER")
                _ = AnimateTerminalText(TopLeftTitleText, "LARPLAND LAUNCHER");
            _ = AnimateTerminalText(VersionText, VerDisplay);
            SideVersionText.Text = "v" + VerDisplay;
            OsText.Text = GetWindowsVersionName();
            PlayerNameRun.Text = _settings.Username;
            GameVersionText.Text = GameVersions.Display;
            ShowModpackVersion();
        }

        private void LoginGridState()
        {
            MainPanel.Visibility = Visibility.Hidden;
            LoginPanel.Visibility = Visibility.Visible;
            if (_settings.UserType == "msa") UsernameBox.Text = "";
            else UsernameBox.Text = _settings.Username;

            TweenOpacity(LoginPanel, 0, 1, 450, OutQuart, 80);
            TweenY(EnsureTabTransform(LoginPanel), 22, 0, 520, OutQuart, 80);
        }

        private async void BtnLoginMicrosoft_Click(object s, RoutedEventArgs e)
        {
            ShowSpinnerOverlay(Lang.T("Вход через Microsoft"), Lang.T("Ожидание авторизации…"), false);
            try
            {
                var handler = JELoginHandlerBuilder.BuildDefault();
                var sessionObj = await handler.AuthenticateInteractively();

                if (sessionObj == null || string.IsNullOrEmpty(sessionObj.Username))
                {
                    await AuthOverlayFail(Lang.T("Вход не выполнен"), Lang.T("Попробуйте ещё раз"));
                    return;
                }

                _settings.Username = sessionObj.Username;
                _settings.UserType = "msa";
                AppSettings.Save(_settings);
                HideUpdateOverlay();
                SwitchToMain();
            }
            catch
            {
                await AuthOverlayFail(Lang.T("Ошибка авторизации"), Lang.T("Попробуйте ещё раз"));
            }
        }

        private async void BtnLoginOffline_Click(object s, RoutedEventArgs e)
        {
            var n = UsernameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(n)) { await ShowCustomDialog(Lang.T("Введите никнейм!")); return; }
            if (!Nickname.IsValid(n)) { await ShowCustomDialog(Lang.T(Nickname.RuleMessage)); return; }
            _settings.Username = n;
            _settings.UserType = "offline";
            AppSettings.Save(_settings);
            SwitchToMain();
        }

        private void RestrictNicknameFields()
        {
            Nickname.Restrict(UsernameBox);
            Nickname.Restrict(SetupUsernameBox);
        }

        private void BtnGitHub_Click(object s, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo(Endpoints.ProjectUrl) { UseShellExecute = true });
        }

        private void BtnClose_Click(object s, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }
        private void BtnMinimize_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void SetProgress(double v)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => SetProgress(v)); return; }
            TweenValue(GameProgressBar, v, 250, OutQuad);
        }

        private void BtnSettings_Click(object s, RoutedEventArgs e)
        {
            SettingsPanel.Visibility = Visibility.Visible; _scrollTarget = -1;
            if (_scrolling) { _scrolling = false; CompositionTarget.Rendering -= ScrollTick; }
            SettingsScrollViewer?.ScrollToVerticalOffset(0);

            try
            {
                int maxRam = (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1048576L);
                RamSlider.Maximum = Math.Max(2048, maxRam);
            }
            catch { RamSlider.Maximum = 8192; }

            RamSlider.Value = _settings.RamMb > 0 ? _settings.RamMb : 4096;
            PathBox.Text = _settings.GamePath;
            SettingsTitle.Opacity = 0;
            SettingsTitleTranslate.Y = 20;
            CacheWhileMoving(SettingsBox, true);
            TweenOpacity(SettingsPanel, 0, 1, 180, Linear);
            TweenScale(SettingsScale, 0.85, 1, 420, OutBackWide);
            TweenY(SettingsTranslate, 30, 0, 420, OutCubic, 0, () =>
            {
                CacheWhileMoving(SettingsBox, false);
                TweenOpacity(SettingsTitle, 0, 1, 650, OutQuart);
                TweenY(SettingsTitleTranslate, 20, 0, 650, OutQuart);
            });
        }

        private async void BtnCloseSettings_Click(object s, RoutedEventArgs e)
        {
            if (int.TryParse(RamBox.Text, out int ram)) _settings.RamMb = ram;
            string np = ResolveGamePath(PathBox.Text);
            if (!string.IsNullOrWhiteSpace(np) && !string.Equals(np, _settings.GamePath, StringComparison.OrdinalIgnoreCase))
            {
                bool wipe = true;
                bool hasFiles = false;
                try { hasFiles = Directory.Exists(np) && Directory.EnumerateFileSystemEntries(np).Any(); } catch { }
                if (hasFiles)
                    wipe = await ShowCustomDialog(
                        Lang.F("Папка {0} не пуста.\nУдалить её содержимое для чистой установки? Желательно удалить, иначе старые файлы могут конфликтовать с модпаком.", np),
                        "Смена папки", true);

                if (wipe) await PrepareGameFolderAsync(np);
                else { try { Directory.CreateDirectory(np); } catch { } }

                _settings.IsModpackInstalled = false;
                _settings.ModpackVersion = "0.0";
                _settings.GamePath = np;
                PathBox.Text = np;
            }
            AppSettings.Save(_settings);
            if (_settings.HasGamePath)
            {
                InitializeLauncher();
            }
            CloseSettingsPanel();
        }

        private void CloseSettingsPanel()
        {
            if (SettingsPanel.Visibility != Visibility.Visible) return;
            CacheWhileMoving(SettingsBox, true);
            TweenScale(SettingsScale, 1, 0.88, 200, InBackSoft);
            TweenY(SettingsTranslate, 0, 18, 200, InCubic);
            TweenOpacity(SettingsPanel, 1, 0, 200, Linear, 0, () =>
            {
                CacheWhileMoving(SettingsBox, false);
                SettingsPanel.Visibility = Visibility.Hidden;
                SettingsPanel.Opacity = 1;
                SettingsScale.ScaleX = 1; SettingsScale.ScaleY = 1;
                SettingsTranslate.Y = 0;
            });
        }

        private void BtnSaveSettings_Click(object s, RoutedEventArgs e) => BtnCloseSettings_Click(s, e);

        private sealed class CheckRow
        {
            public required RequirementCheck Source { get; init; }
            public required string Title { get; init; }
            public required string Detail { get; init; }
            public required string Badge { get; init; }
            public required Brush BadgeBrush { get; init; }
            public string ActionLabel { get; init; } = "";
            public Visibility ActionVisibility { get; init; } = Visibility.Collapsed;
        }

        private readonly System.Collections.ObjectModel.ObservableCollection<CheckRow> _checkRows = new();
        private bool _effectsSoftened;
        private bool _checkActionRunning;
        private bool _startupChecksDone;

        private static int RenderTier => RenderCapability.Tier >> 16;

        private List<RequirementCheck> InspectSystem() => SystemRequirements.Inspect(new RequirementContext
        {
            GamePath = _settings.GamePath ?? "",
            ModpackInstalled = _settings.IsModpackInstalled,
            RenderTier = RenderTier,
            RamMb = _settings.RamMb,
            LicensedAccount = _settings.UserType == "msa"
        });

        private void RunStartupChecks()
        {
            if (_startupChecksDone) return;
            _startupChecksDone = true;

            List<RequirementCheck> results = InspectSystem();
            WriteChecksToLog(results);
            SoftenEffectsWithoutAcceleration();

            if (SystemRequirements.AllGood(results)) return;

            FillChecks(results);
            OpenChecksPanel();
        }

        // WHY: без ускорения WPF рисует окно процессором, и свечение размазывается
        // WHY: чёрными прямоугольниками; гасим его в этом сеансе, не трогая выбор игрока
        private void SoftenEffectsWithoutAcceleration()
        {
            if (RenderTier >= 1 || _effectsSoftened) return;

            _effectsSoftened = true;
            ApplyBloom(false, _settings.BloomStrength ?? 60, false);
            Log(Lang.T("Аппаратного ускорения нет: свечение выключено, чтобы окно не мерцало"));
        }

        private static string _loggedChecks = "";

        // WHY: проверка гоняется после каждого действия в панели, и без этой отсечки лог
        // WHY: превращается в сотню одинаковых строк про одну и ту же незакрытую проблему
        private static void WriteChecksToLog(List<RequirementCheck> results)
        {
            var trouble = results.Where(check => check.State != RequirementState.Ok).ToList();
            string signature = string.Join("|", trouble.Select(check => check.Id + ":" + check.State));
            if (signature == _loggedChecks) return;

            _loggedChecks = signature;

            foreach (RequirementCheck check in trouble)
            {
                string level = check.State == RequirementState.Missing ? "ERROR" : "WARN";
                LauncherLog.Write($"[{level}] {check.Title}: {check.Detail.Replace("\n", " ")}");
            }
        }

        private void FillChecks(List<RequirementCheck> results)
        {
            _checkRows.Clear();

            foreach (RequirementCheck check in results)
            {
                bool hasAction = check.Fix != RequirementFix.None;
                _checkRows.Add(new CheckRow
                {
                    Source = check,
                    Title = check.Title,
                    Detail = check.Detail,
                    Badge = check.State switch
                    {
                        RequirementState.Ok => "[ ok ]",
                        RequirementState.Warning => "[ !! ]",
                        _ => "[ xx ]"
                    },
                    BadgeBrush = new SolidColorBrush(check.State switch
                    {
                        RequirementState.Ok => Color.FromRgb(0x7C, 0xDB, 0x6A),
                        RequirementState.Warning => Color.FromRgb(0xFE, 0xBC, 0x2E),
                        _ => Color.FromRgb(0xFF, 0x5F, 0x57)
                    }),
                    ActionLabel = check.FixLabel,
                    ActionVisibility = hasAction ? Visibility.Visible : Visibility.Collapsed
                });
            }

            if (ChecksList != null) ChecksList.ItemsSource = _checkRows;

            int broken = results.Count(check => check.State == RequirementState.Missing);
            int shaky = results.Count(check => check.State == RequirementState.Warning);

            if (ChecksSummary != null)
            {
                ChecksSummary.Text = broken == 0 && shaky == 0
                    ? Lang.T("Всё на месте, лаунчеру ничего не мешает.")
                    : Lang.F("Мешает работе: {0}, под вопросом: {1}. Лаунчер запустится в любом случае, но эти пункты стоит закрыть.", broken, shaky);
            }
        }

        private void BtnChecks_Click(object s, RoutedEventArgs e)
        {
            FillChecks(InspectSystem());
            OpenChecksPanel();
        }

        private void OpenChecksPanel()
        {
            if (ChecksPanel.Visibility == Visibility.Visible) return;

            ChecksPanel.Visibility = Visibility.Visible;
            Dispatcher.BeginInvoke(() => ChecksScrollViewer?.ScrollToVerticalOffset(0),
                System.Windows.Threading.DispatcherPriority.Loaded);

            ChecksTitle.Opacity = 0;
            ChecksTitleTranslate.Y = 20;
            CacheWhileMoving(ChecksBox, true);
            TweenOpacity(ChecksPanel, 0, 1, 180, Linear);
            TweenScale(ChecksScale, 0.85, 1, 420, OutBackWide);
            TweenY(ChecksTranslate, 30, 0, 420, OutCubic, 0, () =>
            {
                CacheWhileMoving(ChecksBox, false);
                TweenOpacity(ChecksTitle, 0, 1, 650, OutQuart);
                TweenY(ChecksTitleTranslate, 20, 0, 650, OutQuart);
            });
        }

        private void BtnCloseChecks_Click(object s, RoutedEventArgs e)
        {
            if (ChecksPanel.Visibility != Visibility.Visible) return;

            CacheWhileMoving(ChecksBox, true);
            TweenScale(ChecksScale, 1, 0.88, 200, InBackSoft);
            TweenY(ChecksTranslate, 0, 18, 200, InCubic);
            TweenOpacity(ChecksPanel, 1, 0, 200, Linear, 0, () =>
            {
                CacheWhileMoving(ChecksBox, false);
                ChecksPanel.Visibility = Visibility.Hidden;
                ChecksPanel.Opacity = 1;
                ChecksScale.ScaleX = 1; ChecksScale.ScaleY = 1;
                ChecksTranslate.Y = 0;
            });
        }

        private void BtnRecheck_Click(object s, RoutedEventArgs e)
        {
            List<RequirementCheck> results = InspectSystem();
            WriteChecksToLog(results);
            FillChecks(results);
        }

        private void BtnOpenLauncherLog_Click(object s, RoutedEventArgs e)
        {
            string log = Path.Combine(AppSettings.GetConfigDir(), "latest.log");

            if (!File.Exists(log))
            {
                _ = ShowCustomDialog(Lang.F("Лог ещё не создан: {0}", log), "Проверка системы");
                return;
            }

            OpenInShell(log);
        }

        private void OpenInShell(string target)
        {
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception error)
            {
                LauncherLog.Write($"[WARN] Не удалось открыть {target}: {error.Message}");
                _ = ShowCustomDialog(Lang.F("Не удалось открыть {0}: {1}", target, error.Message), "Проверка системы");
            }
        }

        private async void CheckAction_Click(object s, RoutedEventArgs e)
        {
            if (_checkActionRunning) return;
            if (s is not FrameworkElement element || element.DataContext is not CheckRow row) return;

            _checkActionRunning = true;
            try
            {
                switch (row.Source.Fix)
                {
                    case RequirementFix.InstallVcRedist:
                        await InstallVcRedist();
                        break;
                    case RequirementFix.InstallWebView2:
                        await InstallWebView2();
                        break;
                    case RequirementFix.OpenUrl:
                        OpenInShell(row.Source.FixTarget);
                        break;
                    case RequirementFix.OpenGameFolder:
                        OpenInShell(_settings.GamePath);
                        break;
                    case RequirementFix.InstallJava:
                        BtnCloseChecks_Click(s, e);
                        await DownloadAndInstallJava();
                        break;
                    case RequirementFix.ReinstallModpack:
                        BtnCloseChecks_Click(s, e);
                        BtnReinstall_Click(s, e);
                        break;
                }
            }
            finally
            {
                _checkActionRunning = false;
                BtnRecheck_Click(s, e);
            }
        }

        private async Task InstallWebView2()
        {
            ShowSpinnerOverlay(Lang.T("Установка компонента"), Lang.T("Загрузка WebView2 Runtime…"), false);
            bool installed = await WebView2Runtime.InstallAsync();
            HideUpdateOverlay();

            if (!installed) OpenInShell(WebView2Runtime.ManualPage);

            await ShowCustomDialog(
                installed
                    ? Lang.T("WebView2 установлен, вход по лицензии заработает.")
                    : Lang.T("Не удалось установить WebView2 Runtime автоматически. Открою страницу загрузки — установите его вручную, иначе вход через Microsoft работать не будет."),
                Lang.T("Проверка системы"));
        }

        private async Task InstallVcRedist()
        {
            ShowSpinnerOverlay(Lang.T("Установка компонента"), Lang.T("Visual C++ 2015-2022…"), true);

            var downloader = new FileDownloader();
            downloader.LogMessage += LogNet;
            downloader.ProgressChanged += p => Dispatcher.BeginInvoke(() => SetUpdateProgress(p));

            bool installed = await VcRedist.InstallAsync(downloader);
            HideUpdateOverlay();

            await ShowCustomDialog(
                installed
                    ? Lang.T("Visual C++ установлен. Если Windows попросит перезагрузку, перезагрузитесь.")
                    : Lang.T("Установить Visual C++ не вышло. Скачайте его вручную с сайта Microsoft: aka.ms/vs/17/release/vc_redist.x64.exe"),
                Lang.T("Проверка системы"));
        }

        // WHY: два знака после запятой округляли чувствительность 43 % в 0.22, и игра
        // WHY: показывала 44 % — шаг ползунка требует трёх

        private void DebugCheck_Changed(object s, RoutedEventArgs e) { if (IsLoaded) { _settings.DebugConsole = DebugCheck.IsChecked == true; AppSettings.Save(_settings); } }
        private void BtnSelectFolder_Click(object s, RoutedEventArgs e) { var d = new OpenFolderDialog(); if (d.ShowDialog() == true) PathBox.Text = ResolveGamePath(d.FolderName); }

        private void RamSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded || RamSlider == null) return;
            double[] snapPoints = { 2048, 4096, 6144, 8192, 10240, 12288, 14336, 16384, 24576, 32768, 49152, 65536 };
            double val = e.NewValue;
            foreach (var sp in snapPoints)
            {
                if (Math.Abs(val - sp) <= 50)
                {
                    if (Math.Abs(RamSlider.Value - sp) > 0.1) RamSlider.Value = sp;
                    break;
                }
            }
        }

        private async Task CheckUpdates()
        {
            try
            {
                string ts = "?t=" + DateTime.Now.Ticks;
                var results = await Task.WhenAll(
                    _httpClient.GetStringAsync(MODPACK_VER_URL + ts),
                    _httpClient.GetStringAsync(LAUNCHER_VER_URL + ts));

                string modpackVerStr = results[0].Trim();
                string launcherVerStr = results[1].Trim();

                if (ReleaseVersion.IsValid(modpackVerStr))
                {
                    _onlineModpackVer = modpackVerStr;
                    if (!_settings.IsModpackInstalled) MarkPlayButton("УСТАНОВИТЬ");
                    else if (ReleaseVersion.IsNewer(modpackVerStr, _settings.ModpackVersion)) { _needsModpackUpdate = true; MarkPlayButton("ОБНОВИТЬ"); }
                    else { _needsModpackUpdate = false; SetPlayState("idle"); }
                }

                ShowModpackVersion();
                StatusText.Text = Lang.F("Сборка {0}", InstalledModpackVersion());

                if (ReleaseVersion.IsNewer(launcherVerStr, VER)
                    && await ShowCustomDialog(Lang.F("Обновить лаунчер до {0}?", ReleaseVersion.Display(launcherVerStr)), "Обновление", true))
                    await UpdateLauncher();
            }
            catch (Exception error)
            {
                LauncherLog.Write($"[ERROR] Проверка версий не прошла: {error}");
                LogError(NetworkTrouble.Looks(error)
                    ? Lang.F("Сеть недоступна: {0}", NetworkTrouble.Deepest(error).Message)
                    : Lang.F("Ошибка сети: {0}", error.Message));
            }
        }

        private string InstalledModpackVersion() =>
            _settings.ModpackVersion == "0.0" ? "—" : ReleaseVersion.Display(_settings.ModpackVersion);

        private void ShowModpackVersion()
        {
            if (ModpackVerText != null) ModpackVerText.Text = InstalledModpackVersion();
        }

        private void MarkPlayButton(string caption)
        {
            BtnPlay.Content = Lang.T(caption);
            SetButtonIcon(BtnPlay, "IconDownload");
            BtnPlay.Background = new SolidColorBrush(Color.FromRgb(0xE0, 0xB4, 0x4C));
        }

        private async Task UpdateLauncher()
        {
            ShowUpdateOverlay();
            try
            {
                string cur = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;
                string dir = Path.GetDirectoryName(cur) ?? AppDomain.CurrentDomain.BaseDirectory;
                string tmp = Path.Combine(dir, UpdateResidue.StagedUpdateName);

                var dl = new FileDownloader();
                dl.LogMessage += LogNet;
                dl.ProgressChanged += p => Dispatcher.BeginInvoke(() => SetUpdateProgress(p));
                await dl.DownloadFileAsync(LAUNCHER_EXE_URL, tmp);

                SetUpdateProgress(100);
                UpdateSubText.Text = Lang.T("Перезапуск…");
                await Task.Delay(400);

                string old = UpdateResidue.ReserveBackupPath(cur);
                File.Move(cur, old);
                try
                {
                    File.Move(tmp, cur);
                }
                catch
                {
                    File.Move(old, cur);
                    throw;
                }

                Process.Start(new ProcessStartInfo(cur)
                {
                    UseShellExecute = true,
                    Arguments = UpdateResidue.RelaunchArguments(Environment.ProcessId)
                });
                Application.Current.Shutdown();
            }
            catch { HideUpdateOverlay(); LogError(Lang.T("Ошибка обновления")); }
        }

        private void ShowUpdateOverlay() => ShowSpinnerOverlay(Lang.T("Обновление лаунчера"), Lang.T("Скачивание новой версии…"), true);

        private void ShowSpinnerOverlay(string title, string sub, bool showProgress)
        {
            StopTween(UpdateProgressBar, SLOT_VALUE);
            UpdateProgressBar.Value = 0;
            UpdateTitleText.Text = title;
            UpdateSubText.Text = sub;
            UpdateSubText.Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF));
            UpdateProgressBar.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
            UpdatePercentText.Text = showProgress ? "0%" : "";
            UpdateSpinnerArc?.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentBrush");

            UpdateOverlay.Visibility = Visibility.Visible;
            TweenOpacity(UpdateOverlay, 0, 1, 220, Linear);
            TweenScale(UpdateCardScale, 0.92, 1, 260, OutCubic);
            TweenY(UpdateCardTranslate, 24, 0, 260, OutCubic);
            StartSpin(UpdateSpinnerRotate, 1100);
        }

        private async Task AuthOverlayFail(string title, string sub)
        {
            StopSpin();
            if (UpdateSpinnerArc != null) UpdateSpinnerArc.Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
            UpdateTitleText.Text = title;
            UpdateSubText.Text = sub;
            UpdateSubText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9B, 0x9B));
            await Task.Delay(2000);
            HideUpdateOverlay();
        }

        private void HideUpdateOverlay()
        {
            StopSpin();
            StopTween(UpdateOverlay, SLOT_OPACITY);
            UpdateOverlay.Visibility = Visibility.Hidden;
        }

        private void SetUpdateProgress(double percent)
        {
            TweenValue(UpdateProgressBar, percent, 200, OutQuad);
            UpdatePercentText.Text = $"{percent:F0}%";
        }

        private async void BtnReinstall_Click(object s, RoutedEventArgs e)
        {
            if (_isBusy) return;
            if (!_settings.HasGamePath) { await ShowCustomDialog(Lang.T("Сначала выберите папку!")); return; }
            if (await ShowCustomDialog(Lang.T("Перекачать сборку заново?"), "Подтверждение", true))
            {
                SetBusy(true);
                try { await InstallModpack(); Log(Lang.T("Готово!")); StatusText.Text = Lang.T("Сборка переустановлена"); SetPlayState("idle"); }
                catch (OperationCanceledException) { Log(Lang.T("Установка отменена.")); StatusText.Text = Lang.T("Отменено"); }
                catch (Exception ex) { await HandleErrorAsync(ex, Lang.T("Ошибка переустановки")); }
                finally { SetBusy(false); }
            }
        }

        private void BloomEnabledCheck_Changed(object s, RoutedEventArgs e) { if (IsLoaded) ApplyBloom(BloomEnabledCheck.IsChecked == true, BloomStrengthSlider.Value); }
        private void BloomStrengthSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (IsLoaded) ApplyBloom(BloomEnabledCheck.IsChecked == true, e.NewValue); }

        private void SettingsScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {

            if (ColorPresetCombo != null && ColorPresetCombo.IsDropDownOpen) return;
            e.Handled = true;
            var sv = SettingsScrollViewer;
            if (_scrollTarget < 0 || !_scrolling) _scrollTarget = sv.VerticalOffset;
            _scrollTarget = Math.Clamp(_scrollTarget - e.Delta * 0.5, 0, sv.ScrollableHeight);
            if (!_scrolling) { _scrolling = true; CompositionTarget.Rendering += ScrollTick; }
        }

        private void ScrollTick(object? s, EventArgs e)
        {
            var sv = SettingsScrollViewer;
            double cur = sv.VerticalOffset, diff = _scrollTarget - cur;
            if (Math.Abs(diff) < 0.5) { sv.ScrollToVerticalOffset(_scrollTarget); _scrolling = false; CompositionTarget.Rendering -= ScrollTick; return; }
            sv.ScrollToVerticalOffset(cur + diff * 0.25);
        }

        private void SettingsScroll_ScrollChanged(object s, ScrollChangedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, SettingsScrollViewer)) return;
            if (Math.Abs(e.VerticalChange) < 0.5) return;
            if (ColorPresetCombo != null && ColorPresetCombo.IsDropDownOpen) ColorPresetCombo.IsDropDownOpen = false;
        }

        private readonly Dictionary<ScrollViewer, double> _smoothTargets = new();
        private bool _smoothScrolling;

        public void SmoothWheel_Preview(object sender, MouseWheelEventArgs e)
        {
            if (sender is not ScrollViewer sv || sv.ScrollableHeight <= 0) return;
            e.Handled = true;
            double cur = _smoothTargets.TryGetValue(sv, out var t) ? t : sv.VerticalOffset;
            _smoothTargets[sv] = Math.Clamp(cur - e.Delta * 0.5, 0, sv.ScrollableHeight);
            if (!_smoothScrolling)
            {
                _smoothScrolling = true;
                CompositionTarget.Rendering += SmoothScrollTick;
            }
        }

        private void SmoothScrollTick(object? s, EventArgs e)
        {
            List<ScrollViewer>? done = null;
            foreach (var kv in _smoothTargets)
            {
                var sv = kv.Key;
                double target = kv.Value;
                double cur = sv.VerticalOffset, diff = target - cur;
                if (!sv.IsVisible || Math.Abs(diff) < 0.5)
                {
                    sv.ScrollToVerticalOffset(target);
                    (done ??= new List<ScrollViewer>()).Add(sv);
                    continue;
                }
                sv.ScrollToVerticalOffset(cur + diff * 0.25);
            }
            if (done != null) foreach (var sv in done) _smoothTargets.Remove(sv);
            if (_smoothTargets.Count == 0)
            {
                _smoothScrolling = false;
                CompositionTarget.Rendering -= SmoothScrollTick;
            }
        }

        public void Combo_Pop(object sender, SelectionChangedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.IsLoaded && fe.IsVisible) PopElement(fe, 0.95, 320);
        }

        private void PopElement(FrameworkElement fe, double from, int ms)
        {
            fe.RenderTransformOrigin = new Point(0.5, 0.5);
            if (fe.RenderTransform is not ScaleTransform sc)
            {
                sc = new ScaleTransform(1, 1);
                fe.RenderTransform = sc;
            }
            TweenScale(sc, from, 1, ms, OutBackWide);
        }

        public void Btn_MouseTrack(object sender, MouseEventArgs e)
        {
            var btn = (Button)sender;
            if (btn.Template.FindName("BtnTr", btn) is TranslateTransform t)
            {
                if (btn.ActualWidth <= 0 || btn.ActualHeight <= 0) return;
                double curX = t.X, curY = t.Y;
                if (double.IsInfinity(curX) || double.IsNaN(curX)) curX = 0;
                if (double.IsInfinity(curY) || double.IsNaN(curY)) curY = 0;
                StopTween(t, SLOT_X);
                StopTween(t, SLOT_Y);
                t.X = curX;
                t.Y = curY;
                var p = e.GetPosition(btn);
                double cx = btn.ActualWidth / 2, cy = btn.ActualHeight / 2;
                double tx = (cx - p.X) / cx * 5;
                double ty = (cy - p.Y) / cy * 3;
                t.X += (tx - t.X) * 0.3;
                t.Y += (ty - t.Y) * 0.3;
            }
        }

        public void Btn_MouseReset(object sender, MouseEventArgs e)
        {
            var btn = (Button)sender;
            if (btn.Template.FindName("BtnTr", btn) is TranslateTransform t)
            {
                double curX = t.X, curY = t.Y;
                if (double.IsInfinity(curX) || double.IsNaN(curX)) curX = 0;
                if (double.IsInfinity(curY) || double.IsNaN(curY)) curY = 0;
                TweenX(t, curX, 0, 300, OutCubic);
                TweenY(t, curY, 0, 300, OutCubic);
            }
        }

        public void Btn_Pop(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || FxCanvas == null) return;
            if (btn.ActualWidth <= 0 || btn.ActualHeight <= 0) return;
            Point origin;
            try
            {
                var p = Mouse.GetPosition(btn);
                if (p.X < 0 || p.Y < 0 || p.X > btn.ActualWidth || p.Y > btn.ActualHeight)
                    p = new Point(btn.ActualWidth / 2, btn.ActualHeight / 2);
                origin = btn.TransformToVisual(FxCanvas).Transform(p);
            }
            catch { return; }
            SpawnBurst(origin);
        }

        private sealed class FxLayer : FrameworkElement
        {
            private readonly VisualCollection _visuals;

            public DrawingVisual Surface { get; } = new();

            public FxLayer()
            {
                _visuals = new VisualCollection(this) { Surface };
                IsHitTestVisible = false;
            }

            protected override int VisualChildrenCount => _visuals?.Count ?? 0;

            protected override Visual GetVisualChild(int index) => _visuals[index];
        }

        private struct FxDot
        {
            public double X, Y, Dx, Dy, Radius, Start, Life;
            public Color Color;
        }

        private struct FxRing
        {
            public double X, Y, Start;
            public Color Color;
        }

        private sealed class Tween
        {
            public object Owner = null!;
            public int Slot;
            public double Start, Delay, Duration, From, To;
            public Func<double, double> Ease = null!;
            public Action<double> Apply = null!;
            public Action? Done;
        }

        private const int SLOT_OPACITY = 0;
        private const int SLOT_SCALE = 1;
        private const int SLOT_X = 2;
        private const int SLOT_Y = 3;
        private const int SLOT_VALUE = 4;

        private const double FX_RING_LIFE = 0.42;
        private const double FX_RING_RADIUS = 17;
        private const int FX_DOT_LIMIT = 96;

        private readonly List<FxDot> _fxDots = new();
        private readonly List<FxRing> _fxRings = new();
        private readonly List<Tween> _tweens = new();
        private readonly List<Tween> _finished = new();
        private RotateTransform? _spinTarget;
        private double _spinPeriod = 1.1;
        private DropShadowEffect? _pulseTarget;
        private double _pulseLo, _pulseHi, _pulsePeriod = 1, _pulseStart;
        private FxLayer? _fxLayer;
        private bool _fxRunning;
        private double _refreshHz = 60;
        private double _frameBudget = 1.0 / 60;
        private double _lastAnimFrame;
        private IntPtr _refreshMonitor;

        private void Animate(object owner, int slot, double from, double to, double ms, Func<double, double> ease, Action<double> apply, double delayMs = 0, Action? done = null)
        {
            StopTween(owner, slot);
            apply(from);
            if (ms <= 0)
            {
                apply(to);
                done?.Invoke();
                return;
            }
            _tweens.Add(new Tween
            {
                Owner = owner,
                Slot = slot,
                Start = _fxClock.Elapsed.TotalSeconds,
                Delay = delayMs / 1000.0,
                Duration = ms / 1000.0,
                From = from,
                To = to,
                Ease = ease,
                Apply = apply,
                Done = done
            });
            StartAnimLoop();
        }

        private void StopTween(object owner, int slot)
        {
            for (int i = _tweens.Count - 1; i >= 0; i--)
                if (ReferenceEquals(_tweens[i].Owner, owner) && _tweens[i].Slot == slot) _tweens.RemoveAt(i);
        }

        private void TweenOpacity(UIElement el, double from, double to, double ms, Func<double, double> ease, double delay = 0, Action? done = null)
            => Animate(el, SLOT_OPACITY, from, to, ms, ease, v => el.Opacity = v, delay, done);

        private void TweenEffectOpacity(DropShadowEffect fx, double from, double to, double ms, Func<double, double> ease, double delay = 0, Action? done = null)
            => Animate(fx, SLOT_OPACITY, from, to, ms, ease, v => fx.Opacity = v, delay, done);

        private void TweenScale(ScaleTransform st, double from, double to, double ms, Func<double, double> ease, double delay = 0, Action? done = null)
            => Animate(st, SLOT_SCALE, from, to, ms, ease, v => { st.ScaleX = v; st.ScaleY = v; }, delay, done);

        private void TweenX(TranslateTransform tr, double from, double to, double ms, Func<double, double> ease, double delay = 0, Action? done = null)
            => Animate(tr, SLOT_X, from, to, ms, ease, v => tr.X = v, delay, done);

        private void TweenY(TranslateTransform tr, double from, double to, double ms, Func<double, double> ease, double delay = 0, Action? done = null)
            => Animate(tr, SLOT_Y, from, to, ms, ease, v => tr.Y = v, delay, done);

        private void TweenValue(System.Windows.Controls.Primitives.RangeBase bar, double to, double ms, Func<double, double> ease)
            => Animate(bar, SLOT_VALUE, bar.Value, to, ms, ease, v => bar.Value = v);

        private void StartSpin(RotateTransform rt, double periodMs)
        {
            _spinPeriod = periodMs / 1000.0;
            _spinTarget = rt;
            StartAnimLoop();
        }

        private void StopSpin()
        {
            _spinTarget = null;
        }

        private void StartPulse(DropShadowEffect fx, double lo, double hi, double periodMs)
        {
            _pulseLo = lo;
            _pulseHi = hi;
            _pulsePeriod = periodMs / 1000.0;
            _pulseStart = _fxClock.Elapsed.TotalSeconds;
            _pulseTarget = fx;
            StartAnimLoop();
        }

        private void StopPulse()
        {
            _pulseTarget = null;
        }

        private static void CacheWhileMoving(UIElement el, bool on)
        {
            if (on) el.CacheMode ??= new BitmapCache();
            else if (el.CacheMode is BitmapCache) el.CacheMode = null;
        }

        private void StartAnimLoop()
        {
            if (_fxRunning) return;
            _fxRunning = true;
            CompositionTarget.Rendering += AnimTick;
        }

        private void AnimTick(object? sender, EventArgs e)
        {
            double now = _fxClock.Elapsed.TotalSeconds;
            if (now - _lastAnimFrame < _frameBudget) return;
            _lastAnimFrame = now;

            for (int i = _tweens.Count - 1; i >= 0; i--)
            {
                var tw = _tweens[i];
                double t = (now - tw.Start - tw.Delay) / tw.Duration;
                if (t < 0) continue;
                if (t >= 1)
                {
                    tw.Apply(tw.To);
                    _tweens.RemoveAt(i);
                    if (tw.Done != null) _finished.Add(tw);
                    continue;
                }
                tw.Apply(tw.From + (tw.To - tw.From) * tw.Ease(t));
            }

            if (_spinTarget != null) _spinTarget.Angle = now / _spinPeriod % 1 * 360;

            if (_pulseTarget != null)
                _pulseTarget.Opacity = _pulseLo + (_pulseHi - _pulseLo) * (0.5 - 0.5 * Math.Cos((now - _pulseStart) / _pulsePeriod * 2 * Math.PI));

            if (_fxDots.Count > 0 || _fxRings.Count > 0)
            {
                for (int i = _fxDots.Count - 1; i >= 0; i--)
                    if (now - _fxDots[i].Start >= _fxDots[i].Life) _fxDots.RemoveAt(i);
                for (int i = _fxRings.Count - 1; i >= 0; i--)
                    if (now - _fxRings[i].Start >= FX_RING_LIFE) _fxRings.RemoveAt(i);
                PaintFx(now);
            }

            if (_finished.Count > 0)
            {
                int n = _finished.Count;
                for (int i = 0; i < n; i++) _finished[i].Done?.Invoke();
                _finished.RemoveRange(0, n);
            }

            if (_tweens.Count == 0 && _fxDots.Count == 0 && _fxRings.Count == 0 && _spinTarget == null && _pulseTarget == null)
            {
                _fxRunning = false;
                CompositionTarget.Rendering -= AnimTick;
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFOEX info);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettingsW(string? deviceName, int modeNum, ref DEVMODE mode);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public uint dmFields;
            public int dmPositionX, dmPositionY;
            public uint dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        private const int ENUM_CURRENT_SETTINGS = -1;
        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const int WM_DISPLAYCHANGE = 0x007E;
        private const int WM_SETTINGCHANGE = 0x001A;

        private void RefreshDisplayRate(bool force = false)
        {
            IntPtr hwnd = IntPtr.Zero;
            try { hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle; }
            catch { }

            var monitor = hwnd != IntPtr.Zero ? MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) : IntPtr.Zero;
            if (!force && monitor == _refreshMonitor) return;
            _refreshMonitor = monitor;

            double hz = 60;
            try
            {
                string? device = null;
                if (monitor != IntPtr.Zero)
                {
                    var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = "" };
                    if (GetMonitorInfoW(monitor, ref info)) device = info.szDevice;
                }
                var mode = new DEVMODE { dmDeviceName = "", dmFormName = "", dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
                if (EnumDisplaySettingsW(device, ENUM_CURRENT_SETTINGS, ref mode) && mode.dmDisplayFrequency > 1)
                    hz = mode.dmDisplayFrequency;
            }
            catch { }

            _refreshHz = Math.Clamp(hz, 24, 480);
            _frameBudget = 1.0 / _refreshHz * 0.85;
        }

        private static double Linear(double t) => t;

        private static double OutQuad(double t) { double u = 1 - t; return 1 - u * u; }

        private static double OutCubic(double t) { double u = 1 - t; return 1 - u * u * u; }

        private static double InCubic(double t) => t * t * t;

        private static double OutQuart(double t) { double u = 1 - t; return 1 - u * u * u * u; }

        private static double BackIn(double t, double amplitude) => t * t * t - t * amplitude * Math.Sin(Math.PI * t);

        private static double OutBackSoft(double t) => 1 - BackIn(1 - t, 0.6);

        private static double OutBackWide(double t) => 1 - BackIn(1 - t, 1.4);

        private static double InBackSoft(double t) => BackIn(t, 0.6);

        private void SpawnBurst(Point center)
        {
            if (_fxLayer == null)
            {
                _fxLayer = new FxLayer();
                FxCanvas.Children.Add(_fxLayer);
            }

            var accent = (Color)FindResource("AccentColor");
            double now = _fxClock.Elapsed.TotalSeconds;

            if (_fxRings.Count >= 4) _fxRings.RemoveAt(0);
            _fxRings.Add(new FxRing
            {
                X = center.X,
                Y = center.Y,
                Start = now,
                Color = accent
            });

            int count = 11 + _rnd.Next(5);
            if (_fxDots.Count + count > FX_DOT_LIMIT)
                _fxDots.RemoveRange(0, Math.Min(_fxDots.Count, _fxDots.Count + count - FX_DOT_LIMIT));

            for (int i = 0; i < count; i++)
            {
                double ang = _rnd.NextDouble() * Math.PI * 2;
                double dist = 26 + _rnd.NextDouble() * 38;
                _fxDots.Add(new FxDot
                {
                    X = center.X,
                    Y = center.Y,
                    Dx = Math.Cos(ang) * dist,
                    Dy = Math.Sin(ang) * dist - 12,
                    Radius = (3 + _rnd.NextDouble() * 4) / 2,
                    Start = now,
                    Life = (380 + _rnd.Next(280)) / 1000.0,
                    Color = JitterColor(accent)
                });
            }

            StartAnimLoop();
            PaintFx(now);
        }

        private void PaintFx(double now)
        {
            if (_fxLayer == null) return;
            using var dc = _fxLayer.Surface.RenderOpen();

            for (int i = 0; i < _fxRings.Count; i++)
            {
                var ring = _fxRings[i];
                double p = OutCubic(Math.Clamp((now - ring.Start) / FX_RING_LIFE, 0, 1));
                double scale = 0.2 + 1.7 * p;
                double r = FX_RING_RADIUS * scale;
                dc.DrawEllipse(null, FrozenPen(ring.Color, 217 * (1 - p), 2 * scale), new Point(ring.X, ring.Y), r, r);
            }

            for (int i = 0; i < _fxDots.Count; i++)
            {
                var dot = _fxDots[i];
                double p = OutCubic(Math.Clamp((now - dot.Start) / dot.Life, 0, 1));
                double r = dot.Radius * (1 - p);
                if (r <= 0.05) continue;
                dc.DrawEllipse(FrozenFill(dot.Color, 255 * (1 - p)), null, new Point(dot.X + dot.Dx * p, dot.Y + dot.Dy * p), r, r);
            }
        }

        private static SolidColorBrush FrozenFill(Color c, double alpha)
        {
            var b = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(alpha, 0, 255), c.R, c.G, c.B));
            b.Freeze();
            return b;
        }

        private static Pen FrozenPen(Color c, double alpha, double thickness)
        {
            var p = new Pen(FrozenFill(c, alpha), thickness);
            p.Freeze();
            return p;
        }

        private Color JitterColor(Color c)
        {
            double f = 0.7 + _rnd.NextDouble() * 0.5;
            return Color.FromRgb(
                (byte)Math.Min(255, c.R * f),
                (byte)Math.Min(255, c.G * f),
                (byte)Math.Min(255, c.B * f));
        }

    }
}
