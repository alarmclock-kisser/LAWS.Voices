using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;

namespace LAWS.Voices.Shared
{
    public static class StaticLogger
    {
        public static readonly ConcurrentDictionary<DateTime, string> LogEntries = new();
        public static readonly BindingList<string> LogEntriesBindingList = [];
        public static readonly BindingList<string> NativeRuntimeLogEntriesBindingList = [];

        public static event Action<string>? LogAdded;

        public static string LogDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
        public static string? LogFilePath { get; private set; } = null;

        // UI synchronization context (set from the UI at startup)
        private static SynchronizationContext? UiContext;

        public static void SetUiContext(SynchronizationContext context)
        {
            UiContext = context;
        }

        public static void InitializeLogFiles(string? logDirectory = null, bool createLogFile = false, int maxPreviousLogFiles = 3)
        {
            LogDirectory = logDirectory ?? LogDirectory;

            try
            {
                if (!Directory.Exists(LogDirectory))
                {
                    Directory.CreateDirectory(LogDirectory);
                }

                if (maxPreviousLogFiles == 0)
                {
                    // Clear all previous logs if exaclty 0 is specified
                    Directory.Delete(LogDirectory, true);
                    Directory.CreateDirectory(LogDirectory);
                }
                else if (maxPreviousLogFiles >= 1)
                {
                    var existingLogs = Directory.GetFiles(LogDirectory, "log_*.txt")
                        .Select(path => new FileInfo(path))
                        .OrderByDescending(fi => fi.CreationTime)
                        .ToList();
                    // Keep only the most recent 'maxPreviousLogFiles' logs
                    foreach (var oldLog in existingLogs.Skip(maxPreviousLogFiles))
                    {
                        try
                        {
                            oldLog.Delete();
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error deleting old log file '{oldLog.FullName}': {ex.Message}");
                        }
                    }
                }

                if (createLogFile)
                {
                    LogFilePath = Path.Combine(LogDirectory, $"log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                    File.Create(LogFilePath).Dispose();
                    Log($"Log file created at {LogFilePath}");
                }
            }
            catch (Exception ex)
            {
                Log($"Error with log files initialization: {ex.Message}");
            }
        }


        public static void Log(string message, string? filter = "[DEBUG]")
        {
            DateTime timestamp = DateTime.Now;
            string logEntry = $"[{timestamp:HH:mm:ss.fff}] {message}";
            LogEntries[timestamp] = logEntry;

            if (!logEntry.Contains(filter ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                if (UiContext != null)
                {
                    UiContext.Post(_ => LogEntriesBindingList.Add(logEntry), null);
                }
                else
                {
                    // Fallback: add on current thread
                    lock (LogEntriesBindingList)
                    {
                        LogEntriesBindingList.Add(logEntry);
                    }
                }

                LogAdded?.Invoke(logEntry);
            }
            else
            {
                if (UiContext != null)
                {
                    UiContext.Post(_ => NativeRuntimeLogEntriesBindingList.Add(logEntry), null);
                }
                else
                {
                    // Fallback: add on current thread
                    lock (NativeRuntimeLogEntriesBindingList)
                    {
                        NativeRuntimeLogEntriesBindingList.Add(logEntry);
                    }
                }
            }

            Console.WriteLine(logEntry);
            if (LogFilePath != null)
            {
                try
                {
                    File.AppendAllText(LogFilePath, logEntry + Environment.NewLine);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error writing to log file: {ex.Message}");
                }
            }
        }

        public static void Log(Exception ex, string? preText = null)
        {
            if (!string.IsNullOrEmpty(preText))
            {
                Log($"{preText}\nException: {ex.Message}\nStack Trace: {ex.StackTrace}");
            }
            else
            {
                Log($"Exception: {ex.Message}\nStack Trace: {ex.StackTrace}");
            }
        }

        public static async Task LogAsync(string message, bool configureAwait = false)
        {
            await Task.Run(() => Log(message)).ConfigureAwait(configureAwait);
        }

        public static async Task LogAsync(Exception ex, string? preText = null, bool configureAwait = false)
        {
            await Task.Run(() => Log(ex, preText)).ConfigureAwait(configureAwait);
        }



        public static void ClearLogs()
        {
            LogEntries.Clear();
            if (UiContext != null)
            {
                UiContext.Post(_ => LogEntriesBindingList.Clear(), null);
            }
            else
            {
                lock (LogEntriesBindingList)
                {
                    LogEntriesBindingList.Clear();
                }
            }
        }



    }
}