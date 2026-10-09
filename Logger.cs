using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace sqlite_simulator;

public static class Logger
{
    private static readonly object _lockObj = new object();
    private static bool _enableLog = true;
    private static string _logPath = string.Empty;

    static Logger()
    {
        string appPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory;
        _logPath = Path.Combine(appPath, "Logs");
        if (!Directory.Exists(_logPath))
        {
            Directory.CreateDirectory(_logPath);
        }
    }

    public static void SetLogSettings(bool enableLog)
    {
        _enableLog = enableLog;
    }

    public static void WriteDebugLog(string message)
    {
        if (!_enableLog) return;
        WriteLog("DEBUG", message);
    }

    public static void WriteErrorLog(string message)
    {
        if (!_enableLog) return;
        WriteLog("ERROR", message);
    }

    public static void WriteLog(string level, string message)
    {
        if (string.IsNullOrEmpty(level) || string.IsNullOrEmpty(message)) return;

        lock (_lockObj)
        {
            try
            {
                string threadName = Thread.CurrentThread.Name ?? "Unknown";

                DateTime utcNow = DateTime.UtcNow;
                DateTime indiaTime = TimeZoneInfo.ConvertTimeFromUtc(utcNow, TimeZoneHelper.IndiaTimeZone);

                string fileName = $"Log_{indiaTime:yyyyMMdd}.txt";
                string filePath = Path.Combine(_logPath, fileName);
                string logEntry = $"{indiaTime:yyyy-MM-dd HH:mm:ss} [{level}] [{threadName}] {message}";

                File.AppendAllText(filePath, logEntry + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to write log: {ex.Message}");
            }
        }
    }
}