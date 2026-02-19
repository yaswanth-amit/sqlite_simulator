using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace sqlite_simulator;

/// <summary>
/// Queue-based batch processor with transaction safety
/// </summary>
public class GlobalBatchProcessor
{
    private static GlobalBatchProcessor? _instance;
    private static readonly object _lock = new object();

    private readonly int _batchSize;
    private readonly Timer _flushTimer;
    private readonly SemaphoreSlim _flushLock = new SemaphoreSlim(1, 1);
    private bool _isDisposed = false;

    // Separate queues for each table type using MVP DTOs
    private readonly ConcurrentQueue<MvpMachineStatus> _statusQueue = new ConcurrentQueue<MvpMachineStatus>();
    private readonly ConcurrentQueue<MvpMachineCycle> _cycleQueue = new ConcurrentQueue<MvpMachineCycle>();
    private readonly ConcurrentQueue<MvpMachineDowntime> _downtimeQueue = new ConcurrentQueue<MvpMachineDowntime>();
    private readonly ConcurrentQueue<MvpMachineEnergy> _energyQueue = new ConcurrentQueue<MvpMachineEnergy>();
    private readonly ConcurrentQueue<MvpMachineFocas> _focasQueue = new ConcurrentQueue<MvpMachineFocas>();
    private readonly ConcurrentQueue<MvpMachineProgramProduction> _programProductionQueue = new ConcurrentQueue<MvpMachineProgramProduction>();
    private readonly ConcurrentQueue<MvpMachineAlarm> _alarmQueue = new ConcurrentQueue<MvpMachineAlarm>();

    public static GlobalBatchProcessor Instance
    {
        get
        {
            if (_instance == null)
            {
                throw new InvalidOperationException("GlobalBatchProcessor must be initialized with Initialize() before accessing Instance");
            }
            return _instance;
        }
    }

    public static void Initialize(int batchSize)
    {
        if (_instance == null)
        {
            lock (_lock)
            {
                if (_instance == null)
                {
                    _instance = new GlobalBatchProcessor(batchSize);
                }
            }
        }
    }

    private GlobalBatchProcessor(int batchSize)
    {
        _batchSize = batchSize;

        // Auto-flush every 10 seconds as backup
        _flushTimer = new Timer(async _ => await FlushAllQueues(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

        Logger.WriteDebugLog($"GlobalBatchProcessor initialized with batch size: {_batchSize}");
    }

    #region Enqueue Methods
    public void EnqueueStatus(MvpMachineStatus record)
    {
        _statusQueue.Enqueue(record);
        if (_statusQueue.Count >= _batchSize)
        {
            _ = Task.Run(() => FlushStatusQueue());
        }
    }
    public void EnqueueCycle(MvpMachineCycle record)
    {
        _cycleQueue.Enqueue(record);
        if (_cycleQueue.Count >= _batchSize)
        {
            _ = Task.Run(() => FlushCycleQueue());
        }
    }
    public void EnqueueDowntime(MvpMachineDowntime record)
    {
        _downtimeQueue.Enqueue(record);
        if (_downtimeQueue.Count >= _batchSize)
        {
            _ = Task.Run(() => FlushDowntimeQueue());
        }
    }
    public void EnqueueEnergy(MvpMachineEnergy record)
    {
        _energyQueue.Enqueue(record);
        if (_energyQueue.Count >= _batchSize)
        {
            _ = Task.Run(() => FlushEnergyQueue());
        }
    }
    public void EnqueueFocas(MvpMachineFocas record)
    {
        _focasQueue.Enqueue(record);
        if (_focasQueue.Count >= _batchSize)
        {
            _ = Task.Run(() => FlushFocasQueue());
        }
    }
    public void EnqueueProgramProduction(MvpMachineProgramProduction record)
    {
        _programProductionQueue.Enqueue(record);
        if (_programProductionQueue.Count >= _batchSize)
        {
            _ = Task.Run(() => FlushProgramProductionQueue());
        }
    }
    public void EnqueueAlarm(MvpMachineAlarm record)
    {
        _alarmQueue.Enqueue(record);
        if (_alarmQueue.Count >= _batchSize)
        {
            _ = Task.Run(() => FlushAlarmQueue());
        }
    }
    #endregion

    #region Flush Methods
    private async Task FlushStatusQueue()
    {
        await FlushQueue(_statusQueue, DatabaseRepository.BatchInsertMvpMachineStatus, "Status");
    }
    private async Task FlushCycleQueue()
    {
        await FlushQueue(_cycleQueue, DatabaseRepository.BatchInsertMvpMachineCycle, "Cycle");
    }
    private async Task FlushDowntimeQueue()
    {
        await FlushQueue(_downtimeQueue, DatabaseRepository.BatchInsertMvpMachineDowntime, "Downtime");
    }
    private async Task FlushEnergyQueue()
    {
        await FlushQueue(_energyQueue, DatabaseRepository.BatchInsertMvpMachineEnergy, "Energy");
    }
    private async Task FlushFocasQueue()
    {
        await FlushQueue(_focasQueue, DatabaseRepository.BatchInsertMvpMachineFocas, "FOCAS");
    }
    private async Task FlushProgramProductionQueue()
    {
        await FlushQueue(_programProductionQueue, DatabaseRepository.BatchInsertMvpMachineProgramProduction, "ProgramProduction");
    }
    private async Task FlushAlarmQueue()
    {
        await FlushQueue(_alarmQueue, DatabaseRepository.BatchInsertMvpMachineAlarm, "Alarm");
    }

    private async Task FlushQueue<T>(ConcurrentQueue<T> queue, Func<List<T>, Task> batchInsertMethod, string queueName)
    {
        if (queue.IsEmpty) return;
        await _flushLock.WaitAsync();
        try
        {
            var batch = new List<T>();
            while (queue.TryDequeue(out var record) && batch.Count < _batchSize)
            {
                batch.Add(record);
            }
            if (batch.Count > 0)
            {
                await batchInsertMethod(batch);
                Logger.WriteDebugLog($"Flushed {batch.Count} {queueName} records");
            }
        }
        catch (Exception ex)
        {
            Logger.WriteErrorLog($"Error flushing {queueName} queue: {ex.Message}");
        }
        finally
        {
            _flushLock.Release();
        }
    }

    public async Task FlushAllQueues()
    {
        if (_isDisposed) return;
        Logger.WriteDebugLog("Flushing all queues...");

        // Flush all queues in parallel for better performance
        await Task.WhenAll(
            FlushStatusQueue(),
            FlushCycleQueue(),
            FlushDowntimeQueue(),
            FlushEnergyQueue(),
            FlushFocasQueue(),
            FlushProgramProductionQueue(),
            FlushAlarmQueue()
        );

        Logger.WriteDebugLog("All queues flushed");
    }
    #endregion

    private void FlushAllQueuesSync()
    {
        Logger.WriteDebugLog("Flushing all queues synchronously...");

        // Flush all queues synchronously
        FlushQueueSync(_statusQueue, DatabaseRepository.BatchInsertMvpMachineStatus, "Status");
        FlushQueueSync(_cycleQueue, DatabaseRepository.BatchInsertMvpMachineCycle, "Cycle");
        FlushQueueSync(_downtimeQueue, DatabaseRepository.BatchInsertMvpMachineDowntime, "Downtime");
        FlushQueueSync(_energyQueue, DatabaseRepository.BatchInsertMvpMachineEnergy, "Energy");
        FlushQueueSync(_focasQueue, DatabaseRepository.BatchInsertMvpMachineFocas, "FOCAS");
        FlushQueueSync(_programProductionQueue, DatabaseRepository.BatchInsertMvpMachineProgramProduction, "ProgramProduction");
        FlushQueueSync(_alarmQueue, DatabaseRepository.BatchInsertMvpMachineAlarm, "Alarm");

        Logger.WriteDebugLog("All queues flushed synchronously");
    }

    private void FlushQueueSync<T>(ConcurrentQueue<T> queue, Func<List<T>, Task> batchInsertMethod, string queueName)
    {
        if (queue.IsEmpty) return;

        var batch = new List<T>();
        while (queue.TryDequeue(out var record) && batch.Count < _batchSize)
        {
            batch.Add(record);
        }

        if (batch.Count > 0)
        {
            try
            {
                batchInsertMethod(batch).Wait();
                Logger.WriteDebugLog($"Flushed {batch.Count} {queueName} records (sync)");
            }
            catch (Exception ex)
            {
                Logger.WriteErrorLog($"Error flushing {queueName} queue (sync): {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        _isDisposed = true;

        // Stop the timer first
        _flushTimer?.Dispose();

        // Wait for any in-flight flushes to complete
        try
        {
            _flushLock.Wait();
            try
            {
                // Final synchronous flush
                FlushAllQueuesSync();
            }
            finally
            {
                _flushLock.Release();
                _flushLock?.Dispose();
            }
        }
        catch (Exception ex)
        {
            Logger.WriteErrorLog($"Error during GlobalBatchProcessor disposal: {ex.Message}");
        }
    }
}