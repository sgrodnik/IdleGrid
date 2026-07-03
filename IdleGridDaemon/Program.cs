using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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

        private static string _logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "IdleGrid", "logs");
        private static Dictionary<string, int> _windowActivity = new();
        private static int _activeSecondsInMinute = 0;
        private static DateTime _currentMinute;
        private static readonly object _lock = new();

        static void Main(string[] args)
        {
            if (!Directory.Exists(_logDir)) Directory.CreateDirectory(_logDir);

            Console.WriteLine($"IdleGrid Daemon started.");
            Console.WriteLine($"Logs directory: {_logDir}");
            Console.WriteLine("Monitoring...");

            _currentMinute = GetRoundedMinute(DateTime.Now);

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;

            var timer = new System.Timers.Timer(1000);
            timer.Elapsed += OnTick;
            timer.AutoReset = true;
            timer.Enabled = true;

            // Keep the app running without a console window (OutputType is WinExe)
            new System.Windows.Forms.ApplicationContext();
            System.Windows.Forms.Application.Run();
        }

        private static DateTime GetRoundedMinute(DateTime dt) => new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, 0);

        private static void OnTick(object? sender, ElapsedEventArgs e)
        {
            lock (_lock)
            {
                var now = DateTime.Now;
                var minute = GetRoundedMinute(now);

                if (minute > _currentMinute)
                {
                    Console.WriteLine($"[{now:HH:mm:ss}] Starting new minute: {minute:HH:mm}");
                    FlushData();
                    _currentMinute = minute;
                }

                if (IsUserActive())
                {
                    _activeSecondsInMinute++;
                    string activeWindow = GetActiveWindowTitle();
                    if (!string.IsNullOrEmpty(activeWindow))
                    {
                        _windowActivity[activeWindow] = _windowActivity.GetValueOrDefault(activeWindow) + 1;
                    }
                }
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
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Logged: {_activeSecondsInMinute}s active in {_currentMinute:HH:mm}. File: {fileName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error writing log: {ex.Message}");
                Debug.WriteLine($"Failed to write log: {ex.Message}");
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
}
