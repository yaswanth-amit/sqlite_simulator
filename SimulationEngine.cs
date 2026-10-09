using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace sqlite_simulator;

public class SimulationEngine
{
    private readonly MachineInfo _machineInfo;
    private readonly List<ShiftDetails> _shifts;
    private readonly List<ProgramDetails> _programs;
    private readonly List<DownCodeInfo> _downCodes;
    private readonly List<AlarmInfo> _alarms;
    private readonly List<ProcessParameterDef> _paramDefs;
    private readonly Dictionary<string, decimal> _currentParamValues = new Dictionary<string, decimal>();
    private readonly Random _random = new Random();
    private readonly int _downtimeThresholdMinutes;
    private readonly int _frequencyMs;
    private static readonly TimeZoneInfo IndiaTimeZone = TimeZoneHelper.IndiaTimeZone;
    private bool _isHistoricalMode;  // True = fast backfill, False = real-time simulation

    private DateTime _currentTime;
    private ShiftDetails _currentShift;
    private int _currentShiftIndex;
    private ProgramDetails _currentProgram;

    // ── State flags ────────────────────────────────────────────────────────────
    private bool _isPoweredOn = false;
    private bool _isRunning = false;
    private bool _isCutting = false;
    private bool _isMachineDown = false;

    // ── Per-shift epoch start times (reset on every shift boundary) ────────────
    private DateTime _shiftPowerOnEpoch;
    private DateTime _shiftRunEpoch;
    private DateTime _shiftCutEpoch;

    // Accumulated seconds *already completed* inside the current shift
    private double _shiftPot = 0;   // Power-On Time  (seconds)
    private double _shiftOt = 0;   // Operating Time (seconds)
    private double _shiftCt = 0;   // Cutting Time   (seconds)

    private bool _potRunning = false;
    private bool _otRunning = false;
    private bool _ctRunning = false;

    // ── Per-shift part counters (reset each shift change) ──────────────────────
    private int _shiftPartCount = 0;
    private int _shiftRejCount = 0;

    private Dictionary<string, int> _shiftProgramPartCounts = new Dictionary<string, int>();
    private Dictionary<string, int> _shiftProgramTargets = new Dictionary<string, int>();

    // ── Cycle bookkeeping ──────────────────────────────────────────────────────
    private DateTime? _cycleStart = null;
    private DateTime? _cycleEnd = null;
    private DateTime? _lastDownEnd = null;

    // ── Energy (cumulative) ─────────────
    private decimal _cumulativeServoEnergy = 0;
    private decimal _cumulativeSpindleEnergy = 0;
    private decimal _regenServoEnergy = 0;
    private decimal _regenSpindleEnergy = 0;

    // ── Helpers ────────────────────────────────────────────────────────────────
    private int CurrentPot => (int)Math.Round(
        _shiftPot + (_potRunning ? (_currentTime - _shiftPowerOnEpoch).TotalSeconds : 0));

    private int CurrentOt => (int)Math.Round(
        _shiftOt + (_otRunning ? (_currentTime - _shiftRunEpoch).TotalSeconds : 0));

    private int CurrentCt => (int)Math.Round(
        _shiftCt + (_ctRunning ? (_currentTime - _shiftCutEpoch).TotalSeconds : 0));

    private string FormatTime(DateTime dt)
    {
        DateTime utc = dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        DateTime ist = TimeZoneInfo.ConvertTimeFromUtc(utc, IndiaTimeZone);
        return ist.ToString("yyyy-MM-dd HH:mm:ss");
    }

    // ──────────────────────────────────────────────────────────────────────────
    public SimulationEngine(MachineInfo machineInfo, DateTime startTime,
                            int downtimeThresholdMinutes, bool isHistoricalMode = false,
                            int frequencyMs = 1000)
    {
        _machineInfo = machineInfo;
        _currentTime = startTime;
        _downtimeThresholdMinutes = downtimeThresholdMinutes;
        _isHistoricalMode = isHistoricalMode;
        _frequencyMs = frequencyMs > 0 ? frequencyMs : 1000;

        _shifts = DatabaseRepository.GetHardcodedShifts();
        _programs = DatabaseRepository.GetHardcodedPrograms();
        _downCodes = DatabaseRepository.GetHardcodedDownCodes();
        _alarms = DatabaseRepository.GetHardcodedAlarms();
        _paramDefs = DatabaseRepository.GetHardcodedProcessParameters();

        foreach (var p in _paramDefs)
        {
            _currentParamValues[p.ParameterName] = p.LowerValue;
        }

        _currentProgram = _programs.First();

        var indiaTime = TimeZoneInfo.ConvertTimeFromUtc(_currentTime, IndiaTimeZone);
        _currentShift = DetermineCurrentShift(indiaTime);
        _currentShiftIndex = _shifts.IndexOf(_currentShift);

        InitialiseShiftCounters();

        string mode = _isHistoricalMode ? "HISTORICAL" : "LIVE";
        Logger.WriteDebugLog(
            $"SimulationEngine initialised for Machine {_machineInfo.IotId} in {mode} mode (Tick: {_frequencyMs}ms, 24 Process Parameters active)");
    }

    private void InitialiseShiftCounters()
    {
        _shiftPot = 0; _shiftOt = 0; _shiftCt = 0;
        _potRunning = false; _otRunning = false; _ctRunning = false;
        _shiftPowerOnEpoch = _currentTime;
        _shiftRunEpoch = _currentTime;
        _shiftCutEpoch = _currentTime;

        _shiftPartCount = 0;
        _shiftRejCount = 0;

        _shiftProgramPartCounts.Clear();
        _shiftProgramTargets.Clear();

        double shiftDurationSeconds = GetShiftDurationSeconds(_currentShift);

        foreach (var program in _programs)
        {
            string key = program.ProgramNumber.ToString();
            _shiftProgramPartCounts[key] = 0;
            int target = program.StdCycleTime > 0
                ? (int)Math.Floor(shiftDurationSeconds / program.StdCycleTime)
                : 0;
            _shiftProgramTargets[key] = target;
        }

        Logger.WriteDebugLog(
            $"Machine {_machineInfo.IotId}: Shift counters reset for {_currentShift.ShiftId}");
    }

    private double GetShiftDurationSeconds(ShiftDetails shift)
    {
        if (shift.ShiftStartTime < shift.ShiftEndTime)
        {
            return (shift.ShiftEndTime - shift.ShiftStartTime).TotalSeconds;
        }
        else
        {
            return (TimeSpan.FromHours(24) - shift.ShiftStartTime + shift.ShiftEndTime).TotalSeconds;
        }
    }

    public async Task StartSimulation(CancellationToken cancellationToken,
                                      DateTime? liveModeSwitchTime = null)
    {
        Thread.CurrentThread.Name = $"Machine_{_machineInfo.IotId}_Simulation";
        Logger.WriteDebugLog($"Starting simulation for Machine {_machineInfo.IotId}");

        _isPoweredOn = true;
        _potRunning = true;
        _shiftPowerOnEpoch = _currentTime;

        bool switchedToLive = false;

        while (!cancellationToken.IsCancellationRequested && !ServiceStop.StopService)
        {
            try
            {
                if (_isHistoricalMode && !switchedToLive
                    && liveModeSwitchTime.HasValue
                    && _currentTime >= liveModeSwitchTime.Value)
                {
                    Logger.WriteDebugLog(
                        $"Machine {_machineInfo.IotId}: Switching to LIVE mode at " +
                        $"{TimeZoneInfo.ConvertTimeFromUtc(_currentTime, IndiaTimeZone):yyyy-MM-dd HH:mm:ss}");
                    _isHistoricalMode = false;
                    switchedToLive = true;
                }

                _cycleStart = null;
                _isRunning = false;
                _isCutting = false;
                _isMachineDown = false;
                bool isAlarmCycle = false;

                CheckAndHandleShiftChange();

                _currentProgram = _programs[PickRandom(0, _programs.Count - 1)];

                int randomValue = PickRandom(1, 100);

                if (randomValue % 2 == 0 && randomValue % 5 == 0)   // ~10% downtime
                {
                    _isMachineDown = true;
                    InsertCurrentStatus();
                    await GenerateDowntimeCycle();
                    _isMachineDown = false;
                    InsertCurrentStatus();
                }
                else
                {
                    if (randomValue % 22 == 0)   // ~4.5% alarm
                        isAlarmCycle = true;

                    await GenerateProductionCycle(isAlarmCycle);
                }

                SimulateEnergyConsumption();
            }
            catch (Exception ex)
            {
                Logger.WriteErrorLog(
                    $"Simulation error for Machine {_machineInfo.IotId}: {ex.Message}");
            }
        }

        Logger.WriteDebugLog($"Simulation stopped for Machine {_machineInfo.IotId}");
    }

    private void CheckAndHandleShiftChange()
    {
        var indiaTime = TimeZoneInfo.ConvertTimeFromUtc(_currentTime, IndiaTimeZone);
        var newShift = DetermineCurrentShift(indiaTime);

        if (newShift.ShiftId == _currentShift.ShiftId) return;

        Logger.WriteDebugLog(
            $"Machine {_machineInfo.IotId}: Shift change " +
            $"{_currentShift.ShiftId} → {newShift.ShiftId} at " +
            $"{indiaTime:yyyy-MM-dd HH:mm:ss}");

        CloseOpenSegments();

        _currentShift = newShift;
        _currentShiftIndex = _shifts.IndexOf(newShift);

        InitialiseShiftCounters();

        _potRunning = true;
        _shiftPowerOnEpoch = _currentTime;
    }

    private void CloseOpenSegments()
    {
        if (_potRunning)
        {
            _shiftPot += (_currentTime - _shiftPowerOnEpoch).TotalSeconds;
            _potRunning = false;
        }
        if (_otRunning)
        {
            _shiftOt += (_currentTime - _shiftRunEpoch).TotalSeconds;
            _otRunning = false;
        }
        if (_ctRunning)
        {
            _shiftCt += (_currentTime - _shiftCutEpoch).TotalSeconds;
            _ctRunning = false;
        }
    }

    private async Task GenerateProductionCycle(bool isAlarmCycle)
    {
        try
        {
            int spindleDelay = 1;       // 1. Spindle Spin-Up: 1s (Running)
            int machiningTime = 6;      // 2. Machining / Cutting: 6s (Cutting)
            int loadUnloadTime = 2;     // 4. Load / Unload: 2s (Idle)
            // 3. Spindle Spin-Down: 1s (Running -> Idle)
            // Total = 1 + 6 + 1 + 2 = 10 seconds total cycle time

            ShiftDetails cycleStartShift = _currentShift;

            _isRunning = true;
            _cycleStart = _currentTime;

            if (!_otRunning)
            {
                _shiftRunEpoch = _currentTime;
                _otRunning = true;
            }

            InsertCurrentStatus();

            await AdvanceTime(spindleDelay);
            _isCutting = true;

            if (!_ctRunning)
            {
                _shiftCutEpoch = _currentTime;
                _ctRunning = true;
            }

            InsertCurrentStatus();

            if (isAlarmCycle)
            {
                int machiningTimeFactor = PickRandom(1, 3);
                int machiningBeforeAlarm = Convert.ToInt32(machiningTime / machiningTimeFactor);

                await AdvanceTime(machiningBeforeAlarm);

                if (_ctRunning)
                {
                    _shiftCt += (_currentTime - _shiftCutEpoch).TotalSeconds;
                    _ctRunning = false;
                }
                _isCutting = false;
                _isRunning = false;

                if (_otRunning)
                {
                    _shiftOt += (_currentTime - _shiftRunEpoch).TotalSeconds;
                    _otRunning = false;
                }

                _isMachineDown = true;
                _cycleEnd = _currentTime;

                InsertCycleRecord();
                InsertCurrentStatus();

                DateTime alarmRaisedAt = _currentTime;

                await GenerateDowntimeCycle(isAlarm: true);

                InsertAlarmRecord(alarmRaisedAt);

                _cycleStart = _currentTime;
                _isRunning = true;
                _isCutting = true;

                _shiftRunEpoch = _currentTime;
                _otRunning = true;
                _shiftCutEpoch = _currentTime;
                _ctRunning = true;

                InsertCurrentStatus();

                await AdvanceTime(machiningTime - machiningBeforeAlarm);
            }
            else
            {
                await AdvanceTime(machiningTime);
            }

            if (_ctRunning)
            {
                _shiftCt += (_currentTime - _shiftCutEpoch).TotalSeconds;
                _ctRunning = false;
            }
            _isCutting = false;

            await AdvanceTime(spindleDelay);
            _cycleEnd = _currentTime;

            if (_otRunning)
            {
                _shiftOt += (_currentTime - _shiftRunEpoch).TotalSeconds;
                _otRunning = false;
            }
            _isRunning = false;

            InsertCurrentStatus();
            InsertCycleRecord();

            var cycleEndIndiaTime = TimeZoneInfo.ConvertTimeFromUtc(_cycleEnd.Value, IndiaTimeZone);
            ShiftDetails cycleEndShift = DetermineCurrentShift(cycleEndIndiaTime);

            if (cycleEndShift.ShiftId != cycleStartShift.ShiftId)
            {
                DateTime newShiftStartUtc = GetShiftStartUtc(cycleEndShift, cycleEndIndiaTime);
                double newShiftElapsedSeconds = (_currentTime - newShiftStartUtc).TotalSeconds;
                if (newShiftElapsedSeconds < 0) newShiftElapsedSeconds = 0;

                CloseOpenSegments();
                _currentShift = cycleEndShift;
                _currentShiftIndex = _shifts.IndexOf(cycleEndShift);
                InitialiseShiftCounters();

                _shiftPot = newShiftElapsedSeconds;
                _shiftOt = newShiftElapsedSeconds;
                _shiftCt = newShiftElapsedSeconds;

                _potRunning = true;
                _shiftPowerOnEpoch = _currentTime;

                string programKey = _currentProgram.ProgramNumber.ToString();
                _shiftPartCount = 1;
                _shiftProgramPartCounts[programKey] = 1;

                InsertFocasData();
                InsertProgramProduction();
            }
            else
            {
                string programKey = _currentProgram.ProgramNumber.ToString();
                _shiftPartCount++;
                _shiftProgramPartCounts[programKey]++;

                InsertFocasData();
                InsertProgramProduction();
            }

            await AdvanceTime(loadUnloadTime);

            if (!_potRunning)
            {
                _shiftPowerOnEpoch = _currentTime;
                _potRunning = true;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteErrorLog($"GenerateProductionCycle error: {ex.Message}");
        }
    }

    private async Task GenerateDowntimeCycle(bool isAlarm = false)
    {
        try
        {
            int downtimeSeconds = PickRandom(1, _downtimeThresholdMinutes) * 60;

            DownCodeInfo downCode = isAlarm
                ? _downCodes.First(d => d.DownDesc.Contains("Breakdown"))
                : _downCodes[PickRandom(0, _downCodes.Count - 1)];

            DateTime downStart = _currentTime;

            bool isPowerFailure = downCode.DownDesc.Replace(" ", "").ToLower().Equals("powerfailure");
            if (isPowerFailure)
            {
                if (_potRunning)
                {
                    _shiftPot += (_currentTime - _shiftPowerOnEpoch).TotalSeconds;
                    _potRunning = false;
                }
            }

            await AdvanceTime(downtimeSeconds);

            DateTime downEnd = _currentTime;

            if (isPowerFailure)
            {
                _shiftPowerOnEpoch = _currentTime;
                _potRunning = true;
            }

            if (_lastDownEnd == null || downEnd > _lastDownEnd)
            {
                GlobalBatchProcessor.Instance.EnqueueDowntime(new MvpMachineDowntime
                {
                    IOTID = _machineInfo.IotId,
                    DownStart = FormatTime(downStart),
                    DownEnd = FormatTime(downEnd),
                    DownID = downCode.DownNo,
                    UpdatedTS = FormatTime(_currentTime),
                    SyncedStatus = 0
                });

                _lastDownEnd = downEnd;
                Logger.WriteDebugLog($"Downtime recorded: {downCode.DownDesc} ({downtimeSeconds}s)");
            }

            _isMachineDown = false;
        }
        catch (Exception ex)
        {
            Logger.WriteErrorLog($"GenerateDowntimeCycle error: {ex.Message}");
        }
    }

    private void InsertCycleRecord()
    {
        if (_cycleStart == null || _cycleEnd == null) return;

        GlobalBatchProcessor.Instance.EnqueueCycle(new MvpMachineCycle
        {
            IOTID = _machineInfo.IotId,
            CycleStart = FormatTime(_cycleStart.Value),
            CycleEnd = FormatTime(_cycleEnd.Value),
            ProgramNo = _currentProgram.ProgramNumber.ToString(),
            StdLoadUnload = _currentProgram.StdLoadUnload,
            StdCycleTime = _currentProgram.StdCycleTime,
            DownThreshold = _machineInfo.DownThreshold,
            UpdatedTS = FormatTime(_currentTime),
            SyncedStatus = 0
        });
    }

    private void InsertCurrentStatus()
    {
        string status = _isCutting ? "Running"
                      : _isMachineDown ? "Stopped"
                      : "Idle";

        GlobalBatchProcessor.Instance.EnqueueStatus(new MvpMachineStatus
        {
            IOTID = _machineInfo.IotId,
            RunningProgramNo = _currentProgram.ProgramNumber.ToString(),
            Status = status,
            OperatorID = _currentProgram.OperatorId.ToString(),
            Target = _currentProgram.Target,
            UpdatedTS = FormatTime(_currentTime),
            SyncedStatus = 0
        });
    }

    private void InsertFocasData()
    {
        DateTime indiaTime = TimeZoneInfo.ConvertTimeFromUtc(_currentTime, IndiaTimeZone);
        DateTime logicalDate = GetShiftLogicalDate(indiaTime, _currentShift);

        GlobalBatchProcessor.Instance.EnqueueFocas(new MvpMachineFocas
        {
            IOTID = _machineInfo.IotId,
            Date = logicalDate.ToString("yyyy-MM-dd"), // Assuming Date only for LogicalDate
            ShiftID = _currentShift.ShiftId.ToString(),
            PartCount = _shiftPartCount,
            RejCount = _shiftRejCount,
            POT = CurrentPot,
            OT = CurrentOt,
            CT = CurrentCt,
            OperatorID = _currentProgram.OperatorId.ToString(),
            UpdatedTS = FormatTime(_currentTime),
            SyncedStatus = 0
        });
    }

    private void InsertProgramProduction()
    {
        DateTime indiaTime = TimeZoneInfo.ConvertTimeFromUtc(_currentTime, IndiaTimeZone);
        DateTime logicalDate = GetShiftLogicalDate(indiaTime, _currentShift);

        string programKey = _currentProgram.ProgramNumber.ToString();
        int programTarget = _shiftProgramTargets.TryGetValue(programKey, out int t) ? t : 0;

        GlobalBatchProcessor.Instance.EnqueueProgramProduction(new MvpMachineProgramProduction
        {
            IOTID = _machineInfo.IotId,
            Date = logicalDate.ToString("yyyy-MM-dd"),
            ShiftID = _currentShift.ShiftId.ToString(),
            ProgramNo = programKey,
            Actual = _shiftProgramPartCounts.TryGetValue(programKey, out int c) ? c : 0,
            Target = programTarget,
            StdLoadUnload = _currentProgram.StdLoadUnload,
            StdCycleTime = _currentProgram.StdCycleTime,
            UpdatedTS = FormatTime(_currentTime),
            SyncedStatus = 0
        });
    }

    private void InsertAlarmRecord(DateTime alarmRaisedAt)
    {
        AlarmInfo alarm = _alarms[PickRandom(0, _alarms.Count - 1)];

        GlobalBatchProcessor.Instance.EnqueueAlarm(new MvpMachineAlarm
        {
            IOTID = _machineInfo.IotId,
            AlarmNo = alarm.AlarmNo,
            AlarmDesc = alarm.AlarmDesc,
            AlarmTS = FormatTime(alarmRaisedAt),
            UpdatedTS = FormatTime(alarmRaisedAt),
            SyncedStatus = 0
        });
    }

    private void SimulateEnergyConsumption()
    {
        decimal servoConsp = 0;
        decimal spindleConsp = 0;

        if (_isCutting)
        {
            servoConsp = Math.Round((decimal)(PickRandom(50, 100) / 1000.0), 3);
            spindleConsp = Math.Round((decimal)(PickRandom(100, 200) / 1000.0), 3);
        }
        else if (_isRunning)
        {
            servoConsp = Math.Round((decimal)(PickRandom(10, 30) / 1000.0), 3);
            spindleConsp = Math.Round((decimal)(PickRandom(20, 50) / 1000.0), 3);
        }
        else if (_isPoweredOn)
        {
            servoConsp = Math.Round((decimal)(PickRandom(5, 10) / 1000.0), 3);
            spindleConsp = Math.Round((decimal)(PickRandom(5, 10) / 1000.0), 3);
        }

        _cumulativeServoEnergy += servoConsp;
        _cumulativeSpindleEnergy += spindleConsp;

        decimal regenServoDelta = Math.Round(servoConsp * (decimal)PickRandom(10, 20) / 100, 3);
        decimal regenSpindleDelta = Math.Round(spindleConsp * (decimal)PickRandom(10, 20) / 100, 3);

        _regenServoEnergy -= regenServoDelta;
        _regenSpindleEnergy -= regenSpindleDelta;

        decimal netServo = _cumulativeServoEnergy + _regenServoEnergy;
        decimal netSpindle = _cumulativeSpindleEnergy + _regenSpindleEnergy;

        GlobalBatchProcessor.Instance.EnqueueEnergy(new MvpMachineEnergy
        {
            IOTID = _machineInfo.IotId,
            Category = "CONSP",
            ServoEnergy = _cumulativeServoEnergy,
            SpindleEnergy = _cumulativeSpindleEnergy,
            TotalEnergy = Math.Round(_cumulativeServoEnergy + _cumulativeSpindleEnergy, 3),
            UpdatedTS = FormatTime(_currentTime),
            SyncedStatus = 0
        });

        GlobalBatchProcessor.Instance.EnqueueEnergy(new MvpMachineEnergy
        {
            IOTID = _machineInfo.IotId,
            Category = "REGEN",
            ServoEnergy = _regenServoEnergy,
            SpindleEnergy = _regenSpindleEnergy,
            TotalEnergy = Math.Round(_regenServoEnergy + _regenSpindleEnergy, 3),
            UpdatedTS = FormatTime(_currentTime),
            SyncedStatus = 0
        });

        GlobalBatchProcessor.Instance.EnqueueEnergy(new MvpMachineEnergy
        {
            IOTID = _machineInfo.IotId,
            Category = "NET",
            ServoEnergy = Math.Round(netServo, 3),
            SpindleEnergy = Math.Round(netSpindle, 3),
            TotalEnergy = Math.Round(netServo + netSpindle, 3),
            UpdatedTS = FormatTime(_currentTime),
            SyncedStatus = 0
        });
    }

    private async Task AdvanceTime(int totalSeconds)
    {
        int stepSeconds = 1;
        int remaining = totalSeconds;
        while (remaining > 0)
        {
            int step = Math.Min(remaining, stepSeconds);
            remaining -= step;

            if (!_isHistoricalMode)
            {
                // Live mode: tick at the configured frequency (e.g. 1000ms = 1s, or 100ms)
                await Task.Delay(_frequencyMs);
                _currentTime = DateTime.UtcNow;
            }
            else
            {
                // Historical fast backfill mode
                _currentTime = _currentTime.AddSeconds(step);
            }

            // High-frequency continuous stream: emit parameters, energy, status, focas & program production on every step
            UpdateParameters();
            EmitParameterTelemetry();
            SimulateEnergyConsumption();
            InsertCurrentStatus();
            InsertFocasData();
            InsertProgramProduction();
        }
    }

    private void UpdateParameters()
    {
        foreach (var def in _paramDefs)
        {
            decimal target = def.LowerValue;
            decimal changeRate = (def.HigherValue - def.LowerValue) * 0.05m; // 5% responsive fluctuation per step

            // State-based parameter target values
            if (def.ParameterName.Contains("Load"))
            {
                if (_isCutting) target = def.HigherValue * (decimal)(0.70 + _random.NextDouble() * 0.20); // 70-90%
                else if (_isRunning) target = def.HigherValue * (decimal)(0.10 + _random.NextDouble() * 0.20); // 10-30%
                else target = def.LowerValue + (def.HigherValue * 0.02m); // Idle load 2%
            }
            else if (def.ParameterName.Contains("Speed"))
            {
                if (_isCutting || _isRunning) target = def.HigherValue * (decimal)(0.85 + _random.NextDouble() * 0.10); // 85-95%
                else target = def.LowerValue;
            }
            else if (def.ParameterName.Contains("Temp"))
            {
                string baseName = def.ParameterName.Replace("Temp", "").Replace("Temperature", "").Split('-')[0];
                string loadParam = _paramDefs.FirstOrDefault(p => p.ParameterName.Contains(baseName) && p.ParameterName.Contains("Load"))?.ParameterName ?? "";

                decimal load = !string.IsNullOrEmpty(loadParam) && _currentParamValues.ContainsKey(loadParam) ? _currentParamValues[loadParam] : 0;

                // Heating/cooling thermodynamic curves
                if (load > 50) target = _currentParamValues[def.ParameterName] + 0.8m;
                else target = _currentParamValues[def.ParameterName] - 0.4m;
            }

            decimal current = _currentParamValues.ContainsKey(def.ParameterName) ? _currentParamValues[def.ParameterName] : def.LowerValue;

            if (current < target) current += changeRate;
            else if (current > target) current -= changeRate;

            // Micro-noise jitter
            current += (decimal)(_random.NextDouble() - 0.5) * (changeRate * 0.2m);

            // Clamp to master definition bounds
            if (current < def.LowerValue) current = def.LowerValue;
            if (current > def.HigherValue) current = def.HigherValue;

            _currentParamValues[def.ParameterName] = Math.Round(current, 2);
        }
    }

    private void EmitParameterTelemetry()
    {
        string ts = FormatTime(_currentTime);
        foreach (var def in _paramDefs)
        {
            decimal val = _currentParamValues.TryGetValue(def.ParameterName, out var v) ? v : def.LowerValue;
            GlobalBatchProcessor.Instance.EnqueueParameter(new MvpMachineParameter
            {
                IOTID = _machineInfo.IotId,
                ParameterID = def.ParameterName,
                ParameterValue = val.ToString("0.##"),
                UpdatedTS = ts,
                SyncedStatus = 0
            });
        }
    }

    private ShiftDetails DetermineCurrentShift(DateTime time)
    {
        TimeSpan currentTime = time.TimeOfDay;

        foreach (var shift in _shifts)
        {
            if (shift.ShiftStartTime < shift.ShiftEndTime)
            {
                if (currentTime >= shift.ShiftStartTime && currentTime < shift.ShiftEndTime)
                    return shift;
            }
            else
            {
                if (currentTime >= shift.ShiftStartTime || currentTime < shift.ShiftEndTime)
                    return shift;
            }
        }

        return _shifts[0];
    }

    private DateTime GetShiftLogicalDate(DateTime currentTime, ShiftDetails shift)
    {
        if (shift.ShiftStartTime > shift.ShiftEndTime
            && currentTime.TimeOfDay < shift.ShiftEndTime)
        {
            return currentTime.Date.AddDays(-1);
        }
        return currentTime.Date;
    }

    private DateTime GetShiftStartUtc(ShiftDetails shift, DateTime referenceIndiaLocal)
    {
        DateTime startIndiaLocal;

        if (shift.ShiftStartTime > shift.ShiftEndTime
            && referenceIndiaLocal.TimeOfDay < shift.ShiftEndTime)
        {
            startIndiaLocal = referenceIndiaLocal.Date.AddDays(-1).Add(shift.ShiftStartTime);
        }
        else
        {
            startIndiaLocal = referenceIndiaLocal.Date.Add(shift.ShiftStartTime);
        }

        return TimeZoneInfo.ConvertTimeToUtc(startIndiaLocal, IndiaTimeZone);
    }

    private int ComputeSeed(int value)
    {
        if (value <= 10) return 0;
        if (value < 100) return 10;
        if (value < 1000) return 30;
        return 100;
    }

    private int PickRandom(int min, int max) => _random.Next(min, max + 1);
}
