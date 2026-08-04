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

            _trayIcon.MouseClick += (s, e) => {
                if (e.Button == MouseButtons.Left) OpenVisualizer();
            };

            _trayIcon.ContextMenuStrip.Items.Add("Open Visualizer", null, (s, e) => OpenVisualizer());
            _trayIcon.ContextMenuStrip.Items.Add("Open Logs Folder", null, (s, e) => Process.Start("explorer.exe", _logDir));
            _trayIcon.ContextMenuStrip.Items.Add("-");
            _trayIcon.ContextMenuStrip.Items.Add("Exit", null, (s, e) => {
                _trayIcon.Visible = false;
                Application.Exit();
            });

            if (!configLoaded)
                ShowError("Could not read config.json. Default configuration is active.");
            StartConfigWatcher();

            Log.Info("IdleGrid Daemon started.");
            Log.Info($"Logs directory: {_logDir}");
            Log.Info("Monitoring...");

            _currentMinute = GetRoundedMinute(DateTime.Now);
            RestoreSessionFromLog();
            UpdateSession(DateTime.Now);

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
            return Icon.FromHandle(bmp.GetHicon());
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
            var filePath = Path.Combine(_logDir, $"{DateTime.Today:yyyy-MM-dd}.log");
            if (!File.Exists(filePath)) return;

            var activeMinutes = new List<DateTime>();

            try
            {
                foreach (var line in File.ReadLines(filePath))
                {
                    var parts = line.Split('|', 3);
                    if (parts.Length < 2 ||
                        !DateTime.TryParseExact(parts[0], "HH:mm", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out var time) ||
                        !int.TryParse(parts[1], out var activeSeconds) ||
                        activeSeconds < _config.ACTIVE_THRESHOLD)
                        continue;

                    activeMinutes.Add(DateTime.Today.AddHours(time.Hour).AddMinutes(time.Minute));
                }

                foreach (var minute in activeMinutes.OrderBy(x => x))
                {
                    if (!_sessionStartMinute.HasValue || !_lastSessionMinute.HasValue ||
                        (minute - _lastSessionMinute.Value).TotalMinutes > _config.GAP_LIMIT)
                    {
                        if (_lastSessionMinute.HasValue)
                            _lastBreakMinutes = (int)(minute - _lastSessionMinute.Value).TotalMinutes;
                        _sessionStartMinute = minute;
                    }

                    _lastSessionMinute = minute;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Error restoring session from log", ex);
                ShowError("Could not restore the current session from today's log.");
            }
        }

        private static void OnTick(object? sender, ElapsedEventArgs e)
        {
            lock (_lock)
            {
                var now = DateTime.Now;
                var minute = GetRoundedMinute(now);

                if (minute > _currentMinute)
                {
                    Console.WriteLine($"Starting new minute: {minute:HH:mm}");
                    FlushData();
                    _currentMinute = minute;
                }

                bool isActive = IsUserActive();
                _trayIcon.Icon = isActive ? _activeIcon : _idleIcon;

                if (isActive)
                {
                    _activeSecondsInMinute++;
                    string activeWindow = GetActiveWindowTitle();
                    if (!string.IsNullOrEmpty(activeWindow))
                    {
                        _windowActivity[activeWindow] = _windowActivity.GetValueOrDefault(activeWindow) + 1;
                    }
                }

                UpdateSession(now);
            }
        }

        private static void UpdateSession(DateTime now)
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

        private static void StartConfigWatcher()
        {
            _configWatcher = new FileSystemWatcher(_dataDir, Path.GetFileName(_configPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            _configWatcher.Changed += OnConfigFileChanged;
            _configWatcher.Created += OnConfigFileChanged;
            _configWatcher.Renamed += OnConfigFileRenamed;
        }

        private static void OnConfigFileChanged(object sender, FileSystemEventArgs e)
        {
            ScheduleConfigReload();
        }

        private static void OnConfigFileRenamed(object sender, RenamedEventArgs e)
        {
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
                    if (LoadConfig()) return;
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
                Console.WriteLine($"Logged: {_activeSecondsInMinute}s active in {_currentMinute:HH:mm}. File: {fileName}");
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
