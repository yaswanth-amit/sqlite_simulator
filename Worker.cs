using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using sqlite_simulator;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace sqlite_simulator;

public class Worker : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private readonly string _appPath;
    private readonly int _logRetentionDays;
    private readonly List<Task> _simulationTasks = new List<Task>();
    private DateTime _nextLogCleanup;

    public Worker(IConfiguration configuration)
    {
        _configuration = configuration;
        _appPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory;
        _logRetentionDays = _configuration.GetSection("ApplicationConfiguration").GetValue<int>("LogRetentionDays", 10);
        _nextLogCleanup = DateTime.UtcNow.AddDays(_logRetentionDays);

        // Set up connection string for SQLite
        string connectionString = _configuration.GetSection("DatabaseInfo").GetValue<string>("SqliteConnectionString")!;
        SqliteConnectionManager.SetConnectionString(connectionString).GetAwaiter().GetResult();

        // Configure logging
        bool enableLog = _configuration.GetSection("ApplicationConfiguration").GetValue<bool>("EnableLog", true);
        Logger.SetLogSettings(enableLog);
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

        // Create Logs directory
        string logsPath = Path.Combine(_appPath, "Logs");
        if (!Directory.Exists(logsPath))
        {
            Directory.CreateDirectory(logsPath);
        }

        Logger.WriteDebugLog("CNC Bronze Simulator service starting...");
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Thread.CurrentThread.Name = "ServiceMain";
        Logger.WriteDebugLog("Service started successfully");

        try
        {
            // Get configuration with timezone-aware parsing
            string startDateString = _configuration.GetSection("SimulationSettings").GetValue<string>("StartDate")!;

            // Log system timezone for debugging
            Logger.WriteDebugLog($"System timezone: {TimeZoneInfo.Local.DisplayName}");
            Logger.WriteDebugLog($"Parsing StartDate string: '{startDateString}'");

            int numberOfMachines = _configuration.GetSection("SimulationSettings").GetValue<int>("NumberOfMachines", 2);
            int downtimeThresholdMinutes = _configuration.GetSection("SimulationSettings").GetValue<int>("DowntimeThresholdMinutes", 10);
            int batchSize = _configuration.GetSection("SimulationSettings").GetValue<int>("BatchSize", 1000);
            int frequencyMs = _configuration.GetSection("SimulationSettings").GetValue<int>("FrequencyMs", 1000);
            bool forceLiveMode = _configuration.GetSection("SimulationSettings").GetValue<bool>("IsLiveMode", true);

            DateTime now = DateTime.UtcNow;
            DateTime startDate;

            TimeZoneInfo indiaTimeZone = TimeZoneHelper.IndiaTimeZone;
            if (forceLiveMode)
            {
                startDate = now;
            }
            else
            {
                startDate = TimeZoneInfo.ConvertTimeToUtc(DateTime.Parse(startDateString), indiaTimeZone);
            }

            Logger.WriteDebugLog($"Parsed start date (UTC): {startDate:yyyy-MM-dd HH:mm:ss}");
            Logger.WriteDebugLog($"Number of machines to simulate: {numberOfMachines}");
            Logger.WriteDebugLog($"Downtime threshold: {downtimeThresholdMinutes} minutes");
            Logger.WriteDebugLog($"Batch size: {batchSize} records");
            Logger.WriteDebugLog($"Generation frequency: {frequencyMs} ms");

            // Initialize global batch processor with configured batch size
            GlobalBatchProcessor.Initialize(batchSize);

            // Seed machine info into MachineInformation_MVP (idempotent)
            Logger.WriteDebugLog($"Seeding {numberOfMachines} machine(s) into MachineInformation_MVP...");
            await DatabaseRepository.SeedMachines(numberOfMachines);

            // Load machines from MachineInformation_MVP
            var machines = await DatabaseRepository.GetMachines();

            if (machines.Count == 0)
            {
                Logger.WriteErrorLog("No machines found in MachineInformation_MVP. Aborting simulation.");
                return;
            }

            // Limit to configured number of machines
            machines = machines.Take(numberOfMachines).ToList();
            Logger.WriteDebugLog($"Starting simulation for {machines.Count} machine(s): {string.Join(", ", machines.Select(m => m.MachineID))}");


            // Create linked cancellation token
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _cts.Token);

            // Determine if we need historical mode (using UTC)
            bool isHistoricalMode = !forceLiveMode && startDate < now;
            DateTime? liveModeSwitchTime = isHistoricalMode ? now : null;

            TimeZoneInfo tz = TimeZoneHelper.IndiaTimeZone;
            if (isHistoricalMode)
            {
                Logger.WriteDebugLog($"Starting in HISTORICAL mode: {TimeZoneInfo.ConvertTimeFromUtc(startDate, tz):yyyy-MM-dd HH:mm:ss} → {TimeZoneInfo.ConvertTimeFromUtc(now, tz):yyyy-MM-dd HH:mm:ss}");
                Logger.WriteDebugLog($"Will switch to LIVE mode at: {TimeZoneInfo.ConvertTimeFromUtc(now, tz):yyyy-MM-dd HH:mm:ss}");
            }
            else
            {
                Logger.WriteDebugLog($"Starting in LIVE mode from: {TimeZoneInfo.ConvertTimeFromUtc(startDate, tz):yyyy-MM-dd HH:mm:ss} (Tick: {frequencyMs}ms)");
            }

            // Start simulation for each machine (using global queue)
            foreach (var machine in machines)
            {
                var engine = new SimulationEngine(machine, startDate, downtimeThresholdMinutes, isHistoricalMode, frequencyMs);
                var task = Task.Run(() => engine.StartSimulation(linkedCts.Token, liveModeSwitchTime), linkedCts.Token);
                _simulationTasks.Add(task);
            }

            Logger.WriteDebugLog($"All {machines.Count} machine simulations started");


            // Monitor and cleanup logs periodically
            while (!stoppingToken.IsCancellationRequested && !ServiceStop.StopService)
            {
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);

                if (DateTime.UtcNow >= _nextLogCleanup)
                {
                    CleanupOldLogs();
                    _nextLogCleanup = DateTime.UtcNow.AddDays(_logRetentionDays);
                }
            }

            // Wait for all simulation tasks to complete
            Logger.WriteDebugLog("Waiting for all simulations to complete...");
            await Task.WhenAll(_simulationTasks);

            // Flush all remaining records from global queues
            Logger.WriteDebugLog("Flushing global batch processor...");
            await GlobalBatchProcessor.Instance.FlushAllQueues();
            GlobalBatchProcessor.Instance.Dispose();
            Logger.WriteDebugLog("Global batch processor disposed");
        }
        catch (OperationCanceledException)
        {
            Logger.WriteDebugLog("Service execution cancelled");
        }
        catch (Exception ex)
        {
            Logger.WriteErrorLog($"Service execution error: {ex.Message}");
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Logger.WriteDebugLog("Service stop requested");

        ServiceStop.StopService = true;
        _cts.Cancel();

        try
        {
            await Task.WhenAny(Task.WhenAll(_simulationTasks), Task.Delay(TimeSpan.FromSeconds(30), cancellationToken));
        }
        catch (Exception ex)
        {
            Logger.WriteErrorLog($"Error during service stop: {ex.Message}");
        }

        Logger.WriteDebugLog("Service stopped");
        await base.StopAsync(cancellationToken);
    }

    private void CleanupOldLogs()
    {
        try
        {
            string logsPath = Path.Combine(_appPath, "Logs");
            if (!Directory.Exists(logsPath)) return;
            var cutoffDate = DateTime.UtcNow.AddDays(-_logRetentionDays);
            var logFiles = Directory.GetFiles(logsPath, "Log_*.txt");
            foreach (var logFile in logFiles)
            {
                var fileInfo = new FileInfo(logFile);
                if (fileInfo.LastWriteTime < cutoffDate)
                {
                    File.Delete(logFile);
                    Logger.WriteDebugLog($"Deleted old log file: {fileInfo.Name}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.WriteErrorLog($"Error cleaning up logs: {ex.Message}");
        }
    }


    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            Logger.WriteErrorLog($"Unhandled exception: {ex.Message}");
            Logger.WriteErrorLog($"Stack trace: {ex.StackTrace}");
        }
    }
}