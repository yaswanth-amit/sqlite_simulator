using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
namespace sqlite_simulator;

public class ServiceStop
{
    public static volatile bool StopService = false;
}

public static class TimeZoneHelper
{
    public static TimeZoneInfo IndiaTimeZone { get; } = ResolveIndiaTimeZone();

    private static TimeZoneInfo ResolveIndiaTimeZone()
    {
        if (TimeZoneInfo.TryFindSystemTimeZoneById("India Standard Time", out var tz))
            return tz;
        if (TimeZoneInfo.TryFindSystemTimeZoneById("Asia/Kolkata", out tz))
            return tz;
        if (TimeZoneInfo.TryFindSystemTimeZoneById("Asia/Calcutta", out tz))
            return tz;
        return TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromMinutes(330), "India Standard Time", "India Standard Time");
    }
}
public class MachineInfo
{
    public int IotId { get; set; }           // mapped from InterfaceID
    public string MachineID { get; set; } = string.Empty;
    public string FocasIPAddress { get; set; } = string.Empty;
    public string PortNo { get; set; } = string.Empty;
    public int DownThreshold { get; set; } = 300; // seconds (default, not in table)
}
public class ShiftDetails
{
    public int ShiftId { get; set; }
    public TimeSpan ShiftStartTime { get; set; }
    public TimeSpan ShiftEndTime { get; set; }
}
public class ProgramDetails
{
    public int ProgramNumber { get; set; }
    public int StdCycleTime { get; set; }
    public int StdLoadUnload { get; set; }
    public int Target { get; set; }
    public int OperatorId { get; set; }
}
public class DownCodeInfo
{
    public int DownNo { get; set; }
    public string DownDesc { get; set; } = string.Empty;
}
public class AlarmInfo
{
    public int AlarmNo { get; set; }
    public string AlarmDesc { get; set; } = string.Empty;
}
// ── MVP SQLite DTOs (one per table) ──────────────────────────────────────────
// 1. MachineRunningStatus_MVP  ←→  bronze.raw_machine_status
public class MvpMachineStatus
{
    public int IOTID { get; set; }
    public string RunningProgramNo { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;   // Running | Idle | Stopped
    public string OperatorID { get; set; } = string.Empty;
    public int Target { get; set; }
    public string UpdatedTS { get; set; } = string.Empty; // UTC text
    public int SyncedStatus { get; set; } = 0;
}
// 2. MachineWiseAlarmDetails_MVP  ←→  bronze.raw_machine_alarms
public class MvpMachineAlarm
{
    public int IOTID { get; set; }
    public int AlarmNo { get; set; }
    public string AlarmDesc { get; set; } = string.Empty;
    public string AlarmTS { get; set; } = string.Empty;   // UTC text (= cnctimestamp)
    public string UpdatedTS { get; set; } = string.Empty;
    public int SyncedStatus { get; set; } = 0;
}
// 3. MachineWiseCycleDetails_MVP  ←→  bronze.raw_machine_cycles
public class MvpMachineCycle
{
    public int IOTID { get; set; }
    public string CycleStart { get; set; } = string.Empty;
    public string CycleEnd { get; set; } = string.Empty;
    public string ProgramNo { get; set; } = string.Empty;
    public int StdLoadUnload { get; set; }
    public int DownThreshold { get; set; }
    public int StdCycleTime { get; set; }
    public string UpdatedTS { get; set; } = string.Empty;
    public int SyncedStatus { get; set; } = 0;
}
// 4. MachineWiseDownDetails_MVP  ←→  bronze.raw_machine_downtime
public class MvpMachineDowntime
{
    public int IOTID { get; set; }
    public string ProgramNo { get; set; } = string.Empty;
    public string DownStart { get; set; } = string.Empty;
    public string DownEnd { get; set; } = string.Empty;
    public int DownThreshold { get; set; }
    public int DownID { get; set; }
    public string UpdatedTS { get; set; } = string.Empty;
    public int SyncedStatus { get; set; } = 0;
}
// 5. MachineWiseEnergyDetails_MVP  ←→  bronze.raw_machine_energy
public class MvpMachineEnergy
{
    public int IOTID { get; set; }
    public string Category { get; set; } = string.Empty;  // CONSP | REGEN | NET
    public decimal ServoEnergy { get; set; }
    public decimal SpindleEnergy { get; set; }
    public decimal TotalEnergy { get; set; }
    public string UpdatedTS { get; set; } = string.Empty;
    public int SyncedStatus { get; set; } = 0;
}
// 6. MachineWiseFocasDetails_MVP  ←→  bronze.raw_machine_focas
public class MvpMachineFocas
{
    public int IOTID { get; set; }
    public string Date { get; set; } = string.Empty;      // logical date IST text
    public string ShiftID { get; set; } = string.Empty;
    public int PartCount { get; set; }
    public int RejCount { get; set; }
    public int POT { get; set; }   // Power-On Time  (seconds)
    public int OT { get; set; }    // Operating Time (seconds)
    public int CT { get; set; }    // Cutting Time   (seconds)
    public string OperatorID { get; set; } = string.Empty;
    public string UpdatedTS { get; set; } = string.Empty;
    public int SyncedStatus { get; set; } = 0;
}
// 7. MachineWiseProgramDetails_MVP  ←→  bronze.raw_machine_focas_program_production
public class MvpMachineProgramProduction
{
    public int IOTID { get; set; }
    public string Date { get; set; } = string.Empty;
    public string ShiftID { get; set; } = string.Empty;
    public string ProgramNo { get; set; } = string.Empty;
    public int Actual { get; set; }
    public int Target { get; set; }
    public int StdCycleTime { get; set; }
    public int StdLoadUnload { get; set; }
    public string UpdatedTS { get; set; } = string.Empty;
    public int SyncedStatus { get; set; } = 0;
}

public class ProcessParameterDef
{
    public int Id { get; set; }
    public string ParameterName { get; set; } = string.Empty;
    public string DisplayText { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public decimal LowerValue { get; set; }
    public decimal HigherValue { get; set; }
    public string Category { get; set; } = string.Empty;
    public string? Axes { get; set; }
}

// 8. MachineWiseParameterDetails_MVP  ←→  bronze.raw_machine_parameter_telemetry
public class MvpMachineParameter
{
    public int IOTID { get; set; }
    public string ParameterID { get; set; } = string.Empty;
    public string ParameterValue { get; set; } = string.Empty;
    public string UpdatedTS { get; set; } = string.Empty;
    public int SyncedStatus { get; set; } = 0;
}
