# IdleGrid

Productivity visualization system. Consists of a background tracker (Daemon) and a web interface for analysis.

## Components

1.  **IdleGridDaemon (.NET 8)**: 
    *   Runs in the Windows tray.
    *   Tracks active windows and idle time.
    *   Logs data in JSON format to `Documents/IdleGrid/logs`.

2.  **Web UI (HTML/JS)**:
    *   Displays activity grid (Active Time) by minutes.
    *   Calculates stats: total workday, net active time, streaks, and breaks.
    *   Uses File System Access API to access the logs folder.

## Usage

1.  Run `IdleGridDaemon.exe`.
2.  Open `web/index.html`.
3.  Click **Choose Folder** and select the logs directory (usually `Documents/IdleGrid/logs`).
