using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace TeamsPresence
{
    public class TeamsLogService
    {
        public event EventHandler<TeamsStatus> StatusChanged;
        public event EventHandler<TeamsActivity> ActivityChanged;
        public event EventHandler<string> NotificationChanged;

        private TeamsStatus CurrentStatus;
        private TeamsActivity CurrentActivity;
        private string CurrentNotificationCount;
        private bool Started = false;

        private Stopwatch Stopwatch { get; set; }
        private string LogPath { get; set; }

        public TeamsLogService()
        {
            LogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", "MSTeams_8wekyb3d8bbwe", "LocalCache", "Microsoft", "MSTeams", "Logs");
            Stopwatch = new Stopwatch();
        }

        public TeamsLogService(string logPath)
        {
            LogPath = logPath;
            Stopwatch = new Stopwatch();
        }

        public void Start()
        {
            Stopwatch.Start();

            var lockMe = new object();

            FileInfo newestLogFile = null;

            DirectoryInfo logFolder = new DirectoryInfo(LogPath);
            if (logFolder.Exists) // else: Invalid folder!
            {
                FileInfo[] logFiles = logFolder.GetFiles("MSTeams_*.log");

                foreach (FileInfo file in logFiles)
                {
                    if (((newestLogFile != null) && (newestLogFile.LastWriteTimeUtc < file.LastWriteTimeUtc)) || newestLogFile == null)
                    {
                        newestLogFile = file;
                    }
                }
            }
            else
            {
                throw new Exception($"Log folder {logFolder} does not exist!");
            }

            using (var latch = new ManualResetEvent(true))
            using (var fs = new FileStream(newestLogFile.FullName, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite))
            using (var fsw = new FileSystemWatcher(LogPath))
            {
                fsw.Changed += (s, e) =>
                {
                    lock (lockMe)
                    {
                        if (e.FullPath != newestLogFile.FullName) return;
                        latch.Set();
                    }
                };

                using (var sr = new StreamReader(fs))
                {
                    while (true)
                    {
                        Thread.Sleep(100);

                        // Throttle based on the stopwatch so we're not sending
                        // tons of updates to Home Assistant.
                        if (Stopwatch.Elapsed.TotalSeconds > 2)
                        {
                            Stopwatch.Stop();

                            if (Started == false)
                            {
                                Started = true;
                                StatusChanged?.Invoke(this, CurrentStatus);
                                ActivityChanged?.Invoke(this, CurrentActivity);
                                NotificationChanged?.Invoke(this, CurrentNotificationCount);
                            }
                        }

                        latch.WaitOne();
                        lock (lockMe)
                        {
                            String line;
                            while ((line = sr.ReadLine()) != null)
                            {
                                LogFileChanged(line, Stopwatch.IsRunning);
                            }
                            latch.Set();
                        }
                    }
                }
            }
        }

        private void LogFileChanged(string line, bool throttled)
        {
            string statusPattern = @"availability: (\w+), unread notification count: (\d+)";
            string activityPatternStart = @".*HfpVoipCallCoordinatorImpl: NotifyCallActive.*";
            //@".*WebViewWindowWin:.*tags=Call.*Window previously was visible = false";
            string activityPatternEnd = @".*HfpVoipCallCoordinatorImpl: HfpVoipCallCoordinatorImpl:reportCallEnded.*";
            // @".*WebViewWindowWin:.*tags=Call.*SetTaskbarIconOverlay overlay description";
            //@".*WebViewWindow: WindowCloseActionDestroy, letting the window destroy";

            RegexOptions options = RegexOptions.Multiline;

            TeamsStatus status = CurrentStatus;
            TeamsActivity activity = CurrentActivity;

            foreach (Match m in Regex.Matches(line, statusPattern, options))
            {
                if (m.Groups[1].Value != "NewActivity")
                {
                    Enum.TryParse<TeamsStatus>(m.Groups[1].Value, out status);

                    CurrentStatus = status;

                    if (!throttled)
                        StatusChanged?.Invoke(this, status);
                }
                var notificationCount = m.Groups[2].Value;
                CurrentNotificationCount = notificationCount;

                if (!throttled)
                {
                    NotificationChanged?.Invoke(this, notificationCount);
                }
            }

            foreach (Match m in Regex.Matches(line, activityPatternStart, options))
            {
                activity = TeamsActivity.InACall;

                CurrentActivity = activity;
            }
            foreach (Match m in Regex.Matches(line, activityPatternEnd, options))
            {
                activity = TeamsActivity.NotInACall;

                CurrentActivity = activity;
            }

            if (!throttled) { 
                    ActivityChanged?.Invoke(this, activity);
            }
        }
    }
}
