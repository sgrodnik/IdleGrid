using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Timers;
using System.Windows.Forms;
using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;
using WpfInterop = System.Windows.Interop;
using WpfMedia = System.Windows.Media;
using Microsoft.Win32;

namespace IdleGridDaemon
{
    class Program
    {
        [StructLayout(LayoutKind.Sequential)]
        struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private static readonly string _dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "IdleGrid");
        private static readonly string _logDir = Path.Combine(_dataDir, "logs");
        private static readonly string _configPath = Path.Combine(_dataDir, "config.json");
        private static Dictionary<string, int> _windowActivity = new();
        private static int _activeSecondsInMinute = 0;
        private static DateTime _currentMinute;
        private static DateTime? _sessionStartMinute;
        private static DateTime? _lastSessionMinute;
        private static int? _lastBreakMinutes;
        private static DateTime? _lastBreakReminderAt;
        private static DateTime? _reminderSessionStart;
        private static AppConfig _config = new();
        private static FileSystemWatcher? _configWatcher;
        private static System.Threading.Timer? _configReloadTimer;
        private static readonly object _configReloadLock = new();
        private static readonly object _lock = new();
        private static NotifyIcon _trayIcon = null!;
        private static Icon _activeIcon = null!;
        private static Icon _idleIcon = null!;
        private static Icon? _sessionIcon;
        private static Control? _uiInvoker;
        private static int _breakChoiceDialogOpen;
        private static int _displayedSessionMinutes = -1;
        private static bool _displayedUserActive;
        private static bool _displayedOverBreakLimit;

        private sealed class AppConfig
        {
            public int GAP_LIMIT { get; set; } = 5;
            public int ACTIVE_THRESHOLD { get; set; } = 5;
            public int WORK_START { get; set; } = 7;
            public int WORK_END { get; set; } = 19;
            public string FOLDER { get; set; } = "IdleGrid";
            public int BREAK_REMINDER_TIMER { get; set; } = 50;
            public int BREAK_REMINDER_INTERVAL { get; set; } = 5;
        }

        static void Main(string[] args)
        {
            var current = Process.GetCurrentProcess();
            var duplicates = Process.GetProcessesByName(current.ProcessName)
                .Where(p => p.Id != current.Id)
                .ToList();

            if (duplicates.Any())
            {
                var result = MessageBox.Show(
                    "Another instance of IdleGrid Daemon is already running. Would you like to terminate it and start this one instead?",
                    "IdleGrid Daemon",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result == DialogResult.Yes)
                    foreach (var dup in duplicates)
                        try { dup.Kill(); dup.WaitForExit(1000); } catch { }
                else
                    return;
            }

            if (!Directory.Exists(_logDir)) Directory.CreateDirectory(_logDir);
            var configLoaded = LoadConfig();

            _activeIcon = CreateSquareIcon(Color.LimeGreen);
            _idleIcon = CreateSquareIcon(Color.Gray);

            _trayIcon = new NotifyIcon()
            {
                Icon = _idleIcon,
                Visible = true,
                Text = "Current Session: 0m | IdleGrid",
                ContextMenuStrip = new ContextMenuStrip()
            };
            _uiInvoker = new Control();
            _ = _uiInvoker.Handle;

            _trayIcon.MouseClick += (s, e) => {
                if (e.Button == MouseButtons.Left) OpenVisualizer();
            };
            _trayIcon.BalloonTipClicked += (s, e) => ShowBreakChoiceDialog();

            _trayIcon.ContextMenuStrip.Items.Add("Open Visualizer", null, (s, e) => OpenVisualizer());
            _trayIcon.ContextMenuStrip.Items.Add("Open folder", null, (s, e) => Process.Start("explorer.exe", _dataDir));
            _trayIcon.ContextMenuStrip.Items.Add("-");
            _trayIcon.ContextMenuStrip.Items.Add("Exit", null, (s, e) => {
                _trayIcon.Visible = false;
                Application.Exit();
            });

            Application.ApplicationExit += (s, e) =>
            {
                _trayIcon.Dispose();
                _sessionIcon?.Dispose();
                _activeIcon.Dispose();
                _idleIcon.Dispose();
                _uiInvoker.Dispose();
            };

            if (!configLoaded)
                ShowError("Could not read config.json. Default configuration is active.");
            StartConfigWatcher();

            Log.Info("IdleGrid Daemon started.");
            Log.Info($"Logs directory: {_logDir}");
            Log.Info("Monitoring...");

            _currentMinute = GetRoundedMinute(DateTime.Now);
            RestoreSessionFromLog();
            UpdateSession(DateTime.Now, IsUserActive());

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;

            var timer = new System.Timers.Timer(1000);
            timer.Elapsed += OnTick;
            timer.AutoReset = true;
            timer.Enabled = true;

            // Keep the app running without a console window (OutputType is WinExe)
            Application.Run();
        }

        private static Icon CreateSquareIcon(Color color)
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(color);
            }
            return CreateIconFromBitmap(bmp);
        }

        private static Icon CreateIconFromBitmap(Bitmap bitmap)
        {
            var iconHandle = bitmap.GetHicon();
            try
            {
                return (Icon)Icon.FromHandle(iconHandle).Clone();
            }
            finally
            {
                DestroyIcon(iconHandle);
            }
        }

        private static Icon CreateSessionIcon(int sessionMinutes, bool isUserActive)
        {
            using var bmp = new Bitmap(32, 32);
            using var g = Graphics.FromImage(bmp);
            using var font = new Font(
                "Segoe UI",
                sessionMinutes >= 10 ? 20 : 24,
                FontStyle.Bold,
                GraphicsUnit.Pixel);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };

            var backgroundColor = sessionMinutes > _config.BREAK_REMINDER_TIMER
                ? Color.Crimson
                : isUserActive ? Color.DimGray : Color.Transparent;
            g.Clear(backgroundColor);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            var label = sessionMinutes >= 100
                ? "99+"
                : sessionMinutes.ToString(CultureInfo.InvariantCulture);
            var textBounds = new RectangleF(0, 0, bmp.Width, bmp.Height);
            g.DrawString(label, font, Brushes.Black, new RectangleF(1, 1, 32, 32), format);
            g.DrawString(label, font, Brushes.White, textBounds, format);

            return CreateIconFromBitmap(bmp);
        }

        private static void OpenVisualizer()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] pathsToTry = {
                Path.Combine(baseDir, "web", "index.html"),
                Path.Combine(baseDir, "..", "..", "..", "..", "web", "index.html"), // From bin/Debug/net8.0-windows
                Path.Combine(Directory.GetParent(Directory.GetParent(_logDir)?.FullName ?? "")?.FullName ?? "", "web", "index.html")
            };

            foreach (var path in pathsToTry)
            {
                if (File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    return;
                }
            }
            MessageBox.Show("Could not find web/index.html", "IdleGrid", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private static DateTime GetRoundedMinute(DateTime dt) => new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, 0);

        private static void RestoreSessionFromLog()
        {
            try
            {
                DateTime? sessionStartMinute = null;
                DateTime? lastSessionMinute = null;
                int? lastBreakMinutes = null;

                foreach (var minute in ReadActiveMinutesToday())
                {
                    if (!sessionStartMinute.HasValue || !lastSessionMinute.HasValue ||
                        (minute - lastSessionMinute.Value).TotalMinutes > _config.GAP_LIMIT)
                    {
                        if (lastSessionMinute.HasValue)
                            lastBreakMinutes = (int)(minute - lastSessionMinute.Value).TotalMinutes;
                        sessionStartMinute = minute;
                    }

                    lastSessionMinute = minute;
                }

                _sessionStartMinute = sessionStartMinute;
                _lastSessionMinute = lastSessionMinute;
                _lastBreakMinutes = lastBreakMinutes;
            }
            catch (Exception ex)
            {
                Log.Error("Error restoring session from log", ex);
                ShowError("Could not restore the current session from today's log.");
            }
        }

        private static List<DateTime> ReadActiveMinutesToday()
        {
            var activeSecondsByMinute = new Dictionary<DateTime, int>();
            var filePath = Path.Combine(_logDir, $"{DateTime.Today:yyyy-MM-dd}.log");

            lock (_lock)
            {
                if (File.Exists(filePath))
                {
                    foreach (var line in File.ReadLines(filePath))
                    {
                        var parts = line.Split('|', 3);
                        if (parts.Length < 2 ||
                            !DateTime.TryParseExact(parts[0], "HH:mm", CultureInfo.InvariantCulture,
                                DateTimeStyles.None, out var time) ||
                            !int.TryParse(parts[1], out var activeSeconds))
                            continue;

                        activeSecondsByMinute[DateTime.Today.AddHours(time.Hour).AddMinutes(time.Minute)] = activeSeconds;
                    }
                }

                if (_activeSecondsInMinute > 0)
                    activeSecondsByMinute[_currentMinute] = _activeSecondsInMinute;

                return activeSecondsByMinute
                    .Where(entry => entry.Value >= _config.ACTIVE_THRESHOLD)
                    .Select(entry => entry.Key)
                    .OrderBy(minute => minute)
                    .ToList();
            }
        }

        private static void OnTick(object? sender, ElapsedEventArgs e)
        {
            try
            {
                lock (_lock)
                {
                    var now = DateTime.Now;
                    var minute = GetRoundedMinute(now);

                    if (minute > _currentMinute)
                    {
                        RecordHourlyActivity(_currentMinute, _activeSecondsInMinute, minute);
                        FlushData();
                        _currentMinute = minute;
                    }

                    bool isActive = IsUserActive();

                    if (isActive)
                    {
                        _activeSecondsInMinute++;
                        string activeWindow = GetActiveWindowTitle();
                        if (!string.IsNullOrEmpty(activeWindow))
                        {
                            _windowActivity[activeWindow] = _windowActivity.GetValueOrDefault(activeWindow) + 1;
                        }
                    }

                    UpdateSession(now, isActive);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Error during daemon tick", ex);
                ShowError("IdleGrid encountered an error while updating. See Daemon.log for details.");
            }
        }

        private static void RecordHourlyActivity(DateTime completedMinute, int activeSeconds, DateTime nextMinute)
        {
            Console.Write($"{activeSeconds} ");

            if (completedMinute.Date != nextMinute.Date || completedMinute.Hour != nextMinute.Hour)
                Console.WriteLine();

            Console.Out.Flush();
        }

        private static void UpdateSession(DateTime now, bool isUserActive)
        {
            if (_activeSecondsInMinute >= _config.ACTIVE_THRESHOLD && _lastSessionMinute != _currentMinute)
            {
                if (!_sessionStartMinute.HasValue || !_lastSessionMinute.HasValue ||
                    (_currentMinute - _lastSessionMinute.Value).TotalMinutes > _config.GAP_LIMIT)
                {
                    if (_lastSessionMinute.HasValue)
                        _lastBreakMinutes = (int)(_currentMinute - _lastSessionMinute.Value).TotalMinutes;
                    _sessionStartMinute = _currentMinute;
                }

                _lastSessionMinute = _currentMinute;
            }

            var sessionMinutes = _sessionStartMinute.HasValue && _lastSessionMinute.HasValue &&
                (GetRoundedMinute(now) - _lastSessionMinute.Value).TotalMinutes <= _config.GAP_LIMIT
                ? Math.Max(0, (int)(GetRoundedMinute(now) - _sessionStartMinute.Value).TotalMinutes)
                : 0;

            if (sessionMinutes == 0)
            {
                _lastBreakReminderAt = null;
                _reminderSessionStart = null;
                _lastBreakMinutes = null;
            }
            else
            {
                if (_reminderSessionStart != _sessionStartMinute)
                {
                    _reminderSessionStart = _sessionStartMinute;
                    _lastBreakReminderAt = null;
                }

                var reminderDue = sessionMinutes >= _config.BREAK_REMINDER_TIMER &&
                    (!_lastBreakReminderAt.HasValue ||
                     (now - _lastBreakReminderAt.Value).TotalMinutes >= _config.BREAK_REMINDER_INTERVAL);
                if (reminderDue && HasRecentUserInput(now))
                {
                    ShowBreakReminder(sessionMinutes);
                    _lastBreakReminderAt = now;
                }
            }

            var lastBreak = _lastBreakMinutes.HasValue ? $"{_lastBreakMinutes}m" : "-";
            _trayIcon.Text = $"Current Session: {sessionMinutes}m | Last Break: {lastBreak}";

            var overBreakLimit = sessionMinutes > _config.BREAK_REMINDER_TIMER;
            if (sessionMinutes != _displayedSessionMinutes ||
                isUserActive != _displayedUserActive ||
                overBreakLimit != _displayedOverBreakLimit)
            {
                var newIcon = CreateSessionIcon(sessionMinutes, isUserActive);
                var oldIcon = _sessionIcon;
                _sessionIcon = newIcon;
                _trayIcon.Icon = newIcon;
                _displayedSessionMinutes = sessionMinutes;
                _displayedUserActive = isUserActive;
                _displayedOverBreakLimit = overBreakLimit;
                oldIcon?.Dispose();
            }
        }

        private static bool HasRecentUserInput(DateTime now)
        {
            LASTINPUTINFO lii = new LASTINPUTINFO
            {
                cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>()
            };
            if (!GetLastInputInfo(ref lii)) return false;

            var idleSeconds = (uint)Environment.TickCount - lii.dwTime;
            return idleSeconds <= 30;
        }

        private static void ShowBreakReminder(int sessionMinutes)
        {
            Log.Info("Break reminder shown.");
            _trayIcon.ShowBalloonTip(
                5000,
                "IdleGrid",
                $"Time for a break. Current session: {sessionMinutes}m",
                ToolTipIcon.Info);
        }

        private static void ShowBreakChoiceDialog()
        {
            if (Interlocked.Exchange(ref _breakChoiceDialogOpen, 1) != 0) return;

            var lastBreak = FindLastBreakToday();
            var now = DateTime.Now;
            var minutesSinceBreakEnd = lastBreak.HasValue
                ? Math.Max(0, (int)(GetRoundedMinute(now) - lastBreak.Value.BreakEndMinute).TotalMinutes)
                : 0;

            var dialogThread = new Thread(() =>
            {
                try
                {
                    var justNowButton = new WpfControls.Button
                    {
                        Content = "Just now",
                        Padding = new Wpf.Thickness(5, 2.5, 5, 2.5),
                        MinWidth = 84,
                        Margin = new Wpf.Thickness(0, 0, 9, 0)
                    };
                    var minutesInput = new WpfControls.TextBox
                    {
                        Text = minutesSinceBreakEnd.ToString(CultureInfo.InvariantCulture),
                        Width = 21,
                        MaxLength = 6,
                        VerticalContentAlignment = Wpf.VerticalAlignment.Center,
                        TextAlignment = Wpf.TextAlignment.Center,
                        Margin = new Wpf.Thickness(0, 0, 9, 0)
                    };
                    var backdatedButton = new WpfControls.Button
                    {
                        Content = $"Ended {minutesSinceBreakEnd}m ago",
                        Padding = new Wpf.Thickness(5, 2.5, 5, 2.5)
                    };
                    minutesInput.PreviewTextInput += (s, e) => e.Handled = !e.Text.All(char.IsDigit);
                    minutesInput.TextChanged += (s, e) =>
                    {
                        backdatedButton.IsEnabled = int.TryParse(minutesInput.Text, out var minutes) && minutes >= 0;
                        backdatedButton.Content = backdatedButton.IsEnabled
                            ? $"Started at {GetRoundedMinute(DateTime.Now).AddMinutes(-minutes):HH:mm}"
                            : "Enter minutes";
                    };

                    var buttons = new WpfControls.StackPanel { Orientation = WpfControls.Orientation.Horizontal };
                    buttons.Children.Add(justNowButton);
                    buttons.Children.Add(minutesInput);
                    buttons.Children.Add(backdatedButton);

                    var content = new WpfControls.StackPanel();
                    content.Children.Add(new WpfControls.TextBlock
                    {
                        Text = "Break check-in",
                        FontSize = 16,
                        FontWeight = Wpf.FontWeights.Normal
                    });
                    content.Children.Add(new WpfControls.TextBlock
                    {
                        Text = "Minutes since your break ended:",
                        FontSize = 12,
                        Margin = new Wpf.Thickness(0, 2, 0, 6),
                        Foreground = WpfMedia.Brushes.DimGray
                    });
                    content.Children.Add(buttons);

                    var paddedContent = new WpfControls.Border
                    {
                        Padding = new Wpf.Thickness(9),
                        Child = content
                    };

                    var window = new Wpf.Window
                    {
                        Title = "IdleGrid",
                        Content = paddedContent,
                        SizeToContent = Wpf.SizeToContent.WidthAndHeight,
                        ResizeMode = Wpf.ResizeMode.NoResize,
                        WindowStartupLocation = Wpf.WindowStartupLocation.Manual,
                        ShowInTaskbar = false,
                        Topmost = true,
                        Background = WpfMedia.Brushes.White,
                        FontFamily = new WpfMedia.FontFamily("Segoe UI"),
                        FontSize = 13
                    };

                    justNowButton.Click += (s, e) =>
                    {
                        var resetAt = GetRoundedMinute(DateTime.Now);
                        DateTime? previousActiveMinute;
                        lock (_lock)
                            previousActiveMinute = _lastSessionMinute;

                        var breakMinutes = previousActiveMinute.HasValue
                            ? Math.Max(0, (int)(resetAt - previousActiveMinute.Value).TotalMinutes)
                            : (int?)null;
                        QueueManualSessionStart(resetAt, resetAt, breakMinutes, "now");
                        window.Close();
                    };

                    backdatedButton.Click += (s, e) =>
                    {
                        if (!int.TryParse(minutesInput.Text, out var minutesAgo) || minutesAgo < 0)
                            return;

                        var nowMinute = GetRoundedMinute(DateTime.Now);
                        var sessionStart = nowMinute.AddMinutes(-minutesAgo);
                        backdatedButton.Content = $"Started at {sessionStart:HH:mm}";
                        var breakMinutes = lastBreak.HasValue
                            ? (int?)(lastBreak.Value.BreakEndMinute - lastBreak.Value.PreviousActiveMinute).TotalMinutes
                            : null;
                        QueueManualSessionStart(
                            sessionStart,
                            nowMinute,
                            breakMinutes,
                            $"{minutesAgo} minutes ago");
                        window.Close();
                    };

                    window.Loaded += (s, e) => PositionBreakChoiceWindow(window);
                    window.Closed += (s, e) => Interlocked.Exchange(ref _breakChoiceDialogOpen, 0);
                    window.ShowDialog();
                }
                catch (Exception ex)
                {
                    Log.Error("Could not show break choice window", ex);
                    Interlocked.Exchange(ref _breakChoiceDialogOpen, 0);
                }
            })
            {
                IsBackground = true,
                Name = "IdleGrid break choice window"
            };
            dialogThread.SetApartmentState(ApartmentState.STA);
            dialogThread.Start();
        }

        private static void PositionBreakChoiceWindow(Wpf.Window window)
        {
            var cursor = Cursor.Position;
            var workArea = Screen.FromPoint(cursor).WorkingArea;
            var targetX = (cursor.X + workArea.Left + workArea.Width / 2.0) / 2.0;
            var targetY = (cursor.Y + workArea.Top + workArea.Height / 2.0) / 2.0;
            var dpi = GetDpiForWindow(new WpfInterop.WindowInteropHelper(window).Handle);
            var scale = dpi == 0 ? 1.0 : dpi / 96.0;

            window.Left = targetX / scale - window.ActualWidth / 2;
            window.Top = targetY / scale - window.ActualHeight / 2;
        }

        private static void QueueManualSessionStart(
            DateTime sessionStartMinute,
            DateTime lastActiveMinute,
            int? breakMinutes,
            string selection)
        {
            _uiInvoker?.BeginInvoke(new Action(() =>
                ApplyManualSessionStart(sessionStartMinute, lastActiveMinute, breakMinutes, selection)));
        }

        private static (DateTime PreviousActiveMinute, DateTime BreakEndMinute, DateTime LastActiveMinute)? FindLastBreakToday()
        {
            try
            {
                var activeMinutes = ReadActiveMinutesToday();

                for (var index = activeMinutes.Count - 1; index > 0; index--)
                {
                    if ((activeMinutes[index] - activeMinutes[index - 1]).TotalMinutes > 1)
                    {
                        return (
                            activeMinutes[index - 1],
                            activeMinutes[index],
                            activeMinutes[^1]);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not find the latest break in today's activity log", ex);
            }

            return null;
        }

        private static void ApplyManualSessionStart(
            DateTime sessionStartMinute,
            DateTime lastActiveMinute,
            int? breakMinutes,
            string selection)
        {
            lock (_lock)
            {
                _sessionStartMinute = sessionStartMinute;
                _lastSessionMinute = lastActiveMinute;
                _lastBreakMinutes = breakMinutes;
                _lastBreakReminderAt = null;
                _reminderSessionStart = sessionStartMinute;
                UpdateSession(DateTime.Now, IsUserActive());
            }

            Log.Info($"Session reset manually: break ended {selection}; session starts at {sessionStartMinute:HH:mm}.");
        }

        private static void StartConfigWatcher()
        {
            _configWatcher = new FileSystemWatcher(_dataDir, Path.GetFileName(_configPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
            };
            _configWatcher.Changed += OnConfigFileChanged;
            _configWatcher.Created += OnConfigFileChanged;
            _configWatcher.Renamed += OnConfigFileRenamed;
            _configWatcher.Error += OnConfigWatcherError;
            _configWatcher.EnableRaisingEvents = true;
            Log.Info($"Watching configuration file: {_configPath}");
        }

        private static void OnConfigFileChanged(object sender, FileSystemEventArgs e)
        {
            ScheduleConfigReload();
        }

        private static void OnConfigFileRenamed(object sender, RenamedEventArgs e)
        {
            ScheduleConfigReload();
        }

        private static void OnConfigWatcherError(object sender, ErrorEventArgs e)
        {
            Log.Error("Configuration file watcher reported an error", e.GetException());
            ScheduleConfigReload();
        }

        private static void ScheduleConfigReload()
        {
            lock (_configReloadLock)
            {
                _configReloadTimer?.Dispose();
                _configReloadTimer = new System.Threading.Timer(
                    _ => ReloadConfig(),
                    null,
                    TimeSpan.FromMilliseconds(250),
                    Timeout.InfiniteTimeSpan);
            }
        }

        private static void ReloadConfig()
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                lock (_lock)
                {
                    var previousConfig = _config;
                    if (LoadConfig())
                    {
                        var changes = new List<string>();
                        if (previousConfig.GAP_LIMIT != _config.GAP_LIMIT)
                            changes.Add($"GAP_LIMIT: {previousConfig.GAP_LIMIT} -> {_config.GAP_LIMIT}");
                        if (previousConfig.ACTIVE_THRESHOLD != _config.ACTIVE_THRESHOLD)
                            changes.Add($"ACTIVE_THRESHOLD: {previousConfig.ACTIVE_THRESHOLD} -> {_config.ACTIVE_THRESHOLD}");
                        if (previousConfig.WORK_START != _config.WORK_START)
                            changes.Add($"WORK_START: {previousConfig.WORK_START} -> {_config.WORK_START}");
                        if (previousConfig.WORK_END != _config.WORK_END)
                            changes.Add($"WORK_END: {previousConfig.WORK_END} -> {_config.WORK_END}");
                        if (previousConfig.FOLDER != _config.FOLDER)
                            changes.Add($"FOLDER: {previousConfig.FOLDER} -> {_config.FOLDER}");
                        if (previousConfig.BREAK_REMINDER_TIMER != _config.BREAK_REMINDER_TIMER)
                            changes.Add($"BREAK_REMINDER_TIMER: {previousConfig.BREAK_REMINDER_TIMER} -> {_config.BREAK_REMINDER_TIMER}");
                        if (previousConfig.BREAK_REMINDER_INTERVAL != _config.BREAK_REMINDER_INTERVAL)
                            changes.Add($"BREAK_REMINDER_INTERVAL: {previousConfig.BREAK_REMINDER_INTERVAL} -> {_config.BREAK_REMINDER_INTERVAL}");

                        if (changes.Count > 0)
                        {
                            Log.Info($"Configuration changed: {string.Join("; ", changes)}");
                            if (previousConfig.GAP_LIMIT != _config.GAP_LIMIT ||
                                previousConfig.ACTIVE_THRESHOLD != _config.ACTIVE_THRESHOLD)
                            {
                                RestoreSessionFromLog();
                                _lastBreakReminderAt = null;
                                _reminderSessionStart = null;
                            }
                            UpdateSession(DateTime.Now, IsUserActive());
                        }
                        return;
                    }
                }

                if (attempt < 2)
                    Thread.Sleep(250);
            }

            Log.Error("Could not read config.json after three attempts.");
            ShowError("Could not read config.json. The previous configuration is still active.");
        }

        private static void ShowError(string message)
        {
            if (_trayIcon == null) return;

            _trayIcon.ShowBalloonTip(
                5000,
                "IdleGrid error",
                message,
                ToolTipIcon.Warning);
        }

        private static bool LoadConfig()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    var loaded = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_configPath));
                    if (loaded != null && loaded.ACTIVE_THRESHOLD >= 0 && loaded.GAP_LIMIT >= 0)
                        _config = loaded;
                    else
                    {
                        Log.Error("config.json is empty or contains invalid values.");
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Error reading config.json", ex);
                return false;
            }
        }

        private static bool IsUserActive()
        {
            LASTINPUTINFO lii = new LASTINPUTINFO();
            lii.cbSize = (uint)Marshal.SizeOf(lii);
            if (GetLastInputInfo(ref lii))
            {
                uint idleTime = (uint)Environment.TickCount - lii.dwTime;
                return idleTime < 2000; // Idle if more than 2 seconds of no input
            }
            return false;
        }

        private static string GetActiveWindowTitle()
        {
            IntPtr handle = GetForegroundWindow();
            if (handle == IntPtr.Zero) return "Idle";

            GetWindowThreadProcessId(handle, out uint pid);
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                return proc.ProcessName;
            }
            catch
            {
                return "Unknown";
            }
        }

        private static void FlushData()
        {
            if (_activeSecondsInMinute == 0 && _windowActivity.Count == 0) return;

            string fileName = $"{_currentMinute:yyyy-MM-dd}.log";
            string filePath = Path.Combine(_logDir, fileName);

            var summary = _windowActivity
                .OrderByDescending(x => x.Value)
                .ToDictionary(x => x.Key, x => x.Value);

            string logLine = $"{_currentMinute:HH:mm}|{_activeSecondsInMinute}|{JsonSerializer.Serialize(summary)}";

            try
            {
                File.AppendAllLines(filePath, new[] { logLine });
            }
            catch (Exception ex)
            {
                Log.Error($"Error writing activity log {fileName}", ex);
                ShowError("Could not write today's activity log.");
            }

            _activeSecondsInMinute = 0;
            _windowActivity.Clear();
        }

        private static void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend) FlushData();
        }

        private static void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.SessionLogoff) FlushData();
        }
    }

    static class Log
    {
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "IdleGrid",
            "Daemon.log");
        private const long MaxLogSizeBytes = 1024 * 1024;
        private static readonly object Sync = new();

        public static void Info(string message) => Write("INFO", message);

        public static void Error(string message, Exception? exception = null)
        {
            var details = exception == null ? message : $"{message}: {exception}";
            Write("ERROR", details);
        }

        public static void Debug(string message, [CallerLineNumber] int line = 0)
        {
            Write("DEBUG", $"L{line}: {message}");
        }

        private static void Write(string level, string message)
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";

            try
            {
                lock (Sync)
                {
                    var logDirectory = Path.GetDirectoryName(LogPath)!;
                    Directory.CreateDirectory(logDirectory);

                    var encodedLine = line + Environment.NewLine;
                    var currentSize = File.Exists(LogPath) ? new FileInfo(LogPath).Length : 0;
                    if (currentSize + System.Text.Encoding.UTF8.GetByteCount(encodedLine) > MaxLogSizeBytes)
                    {
                        var archivePath = Path.Combine(
                            logDirectory,
                            $"Daemon-{DateTime.Now:yyyy-MM-dd}.log");
                        File.Move(LogPath, archivePath, true);
                    }

                    File.AppendAllText(LogPath, encodedLine);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Could not write daemon log: {ex}");
            }

            Console.WriteLine(line);
        }
    }
}
