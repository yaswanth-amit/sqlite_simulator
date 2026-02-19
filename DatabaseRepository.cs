using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
namespace sqlite_simulator;

public static class DatabaseRepository
{
    private static readonly Random Rand = new Random();
    #region Master Data Retrieval
    public static async Task<List<MachineInfo>> GetMachines()
    {
        // Hardcoded machines for MVP
        var machines = new List<MachineInfo>
        {
            new MachineInfo
            {
                IotId = 1,
                DownThreshold = 300 // default or hardcoded
            },
            new MachineInfo
            {
                IotId = 2,
                DownThreshold = 300
            }
        };
        Logger.WriteDebugLog($"Loaded {machines.Count} machines from hardcoded list (MVP)");
        return await Task.FromResult(machines);
    }
    public static List<ShiftDetails> GetHardcodedShifts()
    {
        return new List<ShiftDetails>
        {
            new ShiftDetails
            {
                ShiftId = 1,
                ShiftStartTime = new TimeSpan(6, 0, 0),
                ShiftEndTime = new TimeSpan(14, 0, 0)
            },
            new ShiftDetails
            {
                ShiftId = 2,
                ShiftStartTime = new TimeSpan(14, 0, 0),
                ShiftEndTime = new TimeSpan(22, 0, 0)
            },
            new ShiftDetails
            {
                ShiftId = 3,
                ShiftStartTime = new TimeSpan(22, 0, 0),
                ShiftEndTime = new TimeSpan(6, 0, 0)
            }
        };
    }
    public static List<ProgramDetails> GetHardcodedPrograms()
    {
        var programs = new List<ProgramDetails>();
        int numPrograms = Rand.Next(1, 5);
        for (int i = 0; i < numPrograms; i++)
        {
            int stdCycleTime = Rand.Next(30, 300);
            programs.Add(new ProgramDetails
            {
                ProgramNumber = Rand.Next(500, 1000),
                StdCycleTime = stdCycleTime,
                StdLoadUnload = Rand.Next(5, Math.Max(6, stdCycleTime / 10)),
                Target = 28800 / stdCycleTime,
                OperatorId = Rand.Next(60, 350)
            });
        }
        return programs;
    }
    public static List<DownCodeInfo> GetHardcodedDownCodes()
    {
        return new List<DownCodeInfo>
        {
            new DownCodeInfo { DownNo = 1, DownDesc = "No load" },
            new DownCodeInfo { DownNo = 2, DownDesc = "Programming correction/Transfer" },
            new DownCodeInfo { DownNo = 3, DownDesc = "Insert index/Replacement" },
            new DownCodeInfo { DownNo = 4, DownDesc = "Machine Breakdown" },
            new DownCodeInfo { DownNo = 5, DownDesc = "No operator" },
            new DownCodeInfo { DownNo = 6, DownDesc = "Tool/Insert related problems" },
            new DownCodeInfo { DownNo = 7, DownDesc = "Lunch/Breakfast" },
            new DownCodeInfo { DownNo = 8, DownDesc = "Spare & Consumables" },
            new DownCodeInfo { DownNo = 9, DownDesc = "Power failure" },
            new DownCodeInfo { DownNo = 10, DownDesc = "Development" },
            new DownCodeInfo { DownNo = 11, DownDesc = "2 Machine operating" },
            new DownCodeInfo { DownNo = 12, DownDesc = "Autonomous" },
            new DownCodeInfo { DownNo = 13, DownDesc = "Planned idle" },
            new DownCodeInfo { DownNo = 14, DownDesc = "Inspection" },
            new DownCodeInfo { DownNo = 15, DownDesc = "Part cleaning/Other" },
            new DownCodeInfo { DownNo = 16, DownDesc = "Preventive" }
        };
    }
    public static List<AlarmInfo> GetHardcodedAlarms()
    {
        return new List<AlarmInfo>
        {
            new AlarmInfo { AlarmNo = 100, AlarmDesc = "Emergency stop activated" },
            new AlarmInfo { AlarmNo = 101, AlarmDesc = "Servo alarm - X axis" },
            new AlarmInfo { AlarmNo = 102, AlarmDesc = "Servo alarm - Y axis" },
            new AlarmInfo { AlarmNo = 103, AlarmDesc = "Servo alarm - Z axis" },
            new AlarmInfo { AlarmNo = 200, AlarmDesc = "Spindle overload" },
            new AlarmInfo { AlarmNo = 201, AlarmDesc = "Spindle overheat" },
            new AlarmInfo { AlarmNo = 300, AlarmDesc = "Tool breakage detected" },
            new AlarmInfo { AlarmNo = 301, AlarmDesc = "Tool life expired" },
            new AlarmInfo { AlarmNo = 400, AlarmDesc = "Hydraulic pressure low" },
            new AlarmInfo { AlarmNo = 401, AlarmDesc = "Coolant level low" },
            new AlarmInfo { AlarmNo = 500, AlarmDesc = "Program error" },
            new AlarmInfo { AlarmNo = 501, AlarmDesc = "Memory overflow" }
        };
    }
    #endregion
    #region Batch Inserts (MVP SQLite)
    // 1. MachineRunningStatus_MVP
    public static async Task BatchInsertMvpMachineStatus(List<MvpMachineStatus> records)
    {
        if (records == null || records.Count == 0) return;
        try
        {
            using var conn = await SqliteConnectionManager.GetConnectionAsync();
            using var transaction = conn.BeginTransaction();
            try
            {
                var command = conn.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
                    UPDATE MachineRunningStatus_MVP 
                    SET RunningProgramNo = $prog, Status = $status, OperatorID = $opid, Target = $target, UpdatedTS = $updatedts, SyncedStatus = $synced
                    WHERE IOTID = $iotid;

                    INSERT INTO MachineRunningStatus_MVP (IOTID, RunningProgramNo, Status, OperatorID, Target, UpdatedTS, SyncedStatus)
                    SELECT $iotid, $prog, $status, $opid, $target, $updatedts, $synced
                    WHERE (SELECT Changes() = 0);";

                var pIOTID = command.CreateParameter(); pIOTID.ParameterName = "$iotid"; command.Parameters.Add(pIOTID);
                var pProg = command.CreateParameter(); pProg.ParameterName = "$prog"; command.Parameters.Add(pProg);
                var pStatus = command.CreateParameter(); pStatus.ParameterName = "$status"; command.Parameters.Add(pStatus);
                var pOpID = command.CreateParameter(); pOpID.ParameterName = "$opid"; command.Parameters.Add(pOpID);
                var pTarget = command.CreateParameter(); pTarget.ParameterName = "$target"; command.Parameters.Add(pTarget);
                var pUpdatedTS = command.CreateParameter(); pUpdatedTS.ParameterName = "$updatedts"; command.Parameters.Add(pUpdatedTS);
                var pSynced = command.CreateParameter(); pSynced.ParameterName = "$synced"; command.Parameters.Add(pSynced);
                foreach (var r in records)
                {
                    pIOTID.Value = r.IOTID;
                    pProg.Value = r.RunningProgramNo ?? (object)DBNull.Value;
                    pStatus.Value = r.Status ?? (object)DBNull.Value;
                    pOpID.Value = r.OperatorID ?? (object)DBNull.Value;
                    pTarget.Value = r.Target;
                    pUpdatedTS.Value = r.UpdatedTS ?? (object)DBNull.Value;
                    pSynced.Value = 0;
                    await command.ExecuteNonQueryAsync();
                }
                await transaction.CommitAsync();
                Logger.WriteDebugLog($"Batch upserted {records.Count} status records into MachineRunningStatus_MVP");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Logger.WriteErrorLog($"BatchInsertMvpMachineStatus transaction error: {ex.Message}");
                throw;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteErrorLog($"BatchInsertMvpMachineStatus error: {ex.Message}");
        }
    }
    // 2. MachineWiseAlarmDetails_MVP
    public static async Task BatchInsertMvpMachineAlarm(List<MvpMachineAlarm> records)
    {
        if (records == null || records.Count == 0) return;
        try
        {
            using var conn = await SqliteConnectionManager.GetConnectionAsync();
            using var transaction = conn.BeginTransaction();
            try
            {
                var command = conn.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
                    INSERT INTO MachineWiseAlarmDetails_MVP (IOTID, AlarmNo, AlarmDesc, AlarmTS, UpdatedTS, SyncedStatus)
                    VALUES ($iotid, $ano, $adesc, $ats, $uts, $synced)";
                var pIOTID = command.CreateParameter(); pIOTID.ParameterName = "$iotid"; command.Parameters.Add(pIOTID);
                var pAlarmNo = command.CreateParameter(); pAlarmNo.ParameterName = "$ano"; command.Parameters.Add(pAlarmNo);
                var pAlarmDesc = command.CreateParameter(); pAlarmDesc.ParameterName = "$adesc"; command.Parameters.Add(pAlarmDesc);
                var pAlarmTS = command.CreateParameter(); pAlarmTS.ParameterName = "$ats"; command.Parameters.Add(pAlarmTS);
                var pUpdatedTS = command.CreateParameter(); pUpdatedTS.ParameterName = "$uts"; command.Parameters.Add(pUpdatedTS);
                var pSynced = command.CreateParameter(); pSynced.ParameterName = "$synced"; command.Parameters.Add(pSynced);
                foreach (var r in records)
                {
                    pIOTID.Value = r.IOTID;
                    pAlarmNo.Value = r.AlarmNo;
                    pAlarmDesc.Value = r.AlarmDesc ?? (object)DBNull.Value;
                    pAlarmTS.Value = r.AlarmTS ?? (object)DBNull.Value;
                    pUpdatedTS.Value = r.UpdatedTS ?? (object)DBNull.Value;
                    pSynced.Value = 0;
                    await command.ExecuteNonQueryAsync();
                }
                await transaction.CommitAsync();
                Logger.WriteDebugLog($"Batch inserted {records.Count} alarm records into MachineWiseAlarmDetails_MVP");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Logger.WriteErrorLog($"BatchInsertMvpMachineAlarm transaction error: {ex.Message}");
                throw;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteErrorLog($"BatchInsertMvpMachineAlarm error: {ex.Message}");
        }
    }
    // 3. MachineWiseCycleDetails_MVP
    public static async Task BatchInsertMvpMachineCycle(List<MvpMachineCycle> records)
    {
        if (records == null || records.Count == 0) return;
        try
        {
            using var conn = await SqliteConnectionManager.GetConnectionAsync();
            using var transaction = conn.BeginTransaction();
            try
            {
                var command = conn.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
                    INSERT INTO MachineWiseCycleDetails_MVP (IOTID, CycleStart, CycleEnd, ProgramNo, StdLoadUnload, DownThreshold, StdCycleTime, UpdatedTS, SyncedStatus)
                    VALUES ($iotid, $cs, $ce, $pno, $slu, $dth, $sct, $uts, $synced)";
                var pIOTID = command.CreateParameter(); pIOTID.ParameterName = "$iotid"; command.Parameters.Add(pIOTID);
                var pCS = command.CreateParameter(); pCS.ParameterName = "$cs"; command.Parameters.Add(pCS);
                var pCE = command.CreateParameter(); pCE.ParameterName = "$ce"; command.Parameters.Add(pCE);
                var pPno = command.CreateParameter(); pPno.ParameterName = "$pno"; command.Parameters.Add(pPno);
                var pSLU = command.CreateParameter(); pSLU.ParameterName = "$slu"; command.Parameters.Add(pSLU);
                var pDth = command.CreateParameter(); pDth.ParameterName = "$dth"; command.Parameters.Add(pDth);
                var pSCT = command.CreateParameter(); pSCT.ParameterName = "$sct"; command.Parameters.Add(pSCT);
                var pUTS = command.CreateParameter(); pUTS.ParameterName = "$uts"; command.Parameters.Add(pUTS);
                var pSynced = command.CreateParameter(); pSynced.ParameterName = "$synced"; command.Parameters.Add(pSynced);
                foreach (var r in records)
                {
                    pIOTID.Value = r.IOTID;
                    pCS.Value = r.CycleStart ?? (object)DBNull.Value;
                    pCE.Value = r.CycleEnd ?? (object)DBNull.Value;
                    pPno.Value = r.ProgramNo ?? (object)DBNull.Value;
                    pSLU.Value = r.StdLoadUnload;
                    pDth.Value = r.DownThreshold;
                    pSCT.Value = r.StdCycleTime;
                    pUTS.Value = r.UpdatedTS ?? (object)DBNull.Value;
                    pSynced.Value = 0;
                    await command.ExecuteNonQueryAsync();
                }
                await transaction.CommitAsync();
                Logger.WriteDebugLog($"Batch inserted {records.Count} cycle records into MachineWiseCycleDetails_MVP");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Logger.WriteErrorLog($"BatchInsertMvpMachineCycle transaction error: {ex.Message}");
                throw;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteErrorLog($"BatchInsertMvpMachineCycle error: {ex.Message}");
        }
    }
    // 4. MachineWiseDownDetails_MVP
    public static async Task BatchInsertMvpMachineDowntime(List<MvpMachineDowntime> records)
    {
        if (records == null || records.Count == 0) return;
        try
        {
            using var conn = await SqliteConnectionManager.GetConnectionAsync();
            using var transaction = conn.BeginTransaction();
            try
            {
                var command = conn.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
                    INSERT INTO MachineWiseDownDetails_MVP (IOTID, ProgramNo, DownStart, DownEnd, DownThreshold, DownID, UpdatedTS, SyncedStatus)
                    VALUES ($iotid, $prog, $ds, $de, $dth, $did, $uts, $synced)";
                var pIOTID = command.CreateParameter(); pIOTID.ParameterName = "$iotid"; command.Parameters.Add(pIOTID);
                var pProg = command.CreateParameter(); pProg.ParameterName = "$prog"; command.Parameters.Add(pProg);
                var pDS = command.CreateParameter(); pDS.ParameterName = "$ds"; command.Parameters.Add(pDS);
                var pDE = command.CreateParameter(); pDE.ParameterName = "$de"; command.Parameters.Add(pDE);
                var pDth = command.CreateParameter(); pDth.ParameterName = "$dth"; command.Parameters.Add(pDth);
                var pDID = command.CreateParameter(); pDID.ParameterName = "$did"; command.Parameters.Add(pDID);
                var pUTS = command.CreateParameter(); pUTS.ParameterName = "$uts"; command.Parameters.Add(pUTS);
                var pSynced = command.CreateParameter(); pSynced.ParameterName = "$synced"; command.Parameters.Add(pSynced);
                foreach (var r in records)
                {
                    pIOTID.Value = r.IOTID;
                    pProg.Value = r.ProgramNo ?? (object)DBNull.Value;
                    pDS.Value = r.DownStart ?? (object)DBNull.Value;
                    pDE.Value = r.DownEnd ?? (object)DBNull.Value;
                    pDth.Value = r.DownThreshold;
                    pDID.Value = r.DownID;
                    pUTS.Value = r.UpdatedTS ?? (object)DBNull.Value;
                    pSynced.Value = 0;
                    await command.ExecuteNonQueryAsync();
                }
                await transaction.CommitAsync();
                Logger.WriteDebugLog($"Batch inserted {records.Count} downtime records into MachineWiseDownDetails_MVP");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Logger.WriteErrorLog($"BatchInsertMvpMachineDowntime transaction error: {ex.Message}");
                throw;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteErrorLog($"BatchInsertMvpMachineDowntime error: {ex.Message}");
        }
    }
    // 5. MachineWiseEnergyDetails_MVP
    public static async Task BatchInsertMvpMachineEnergy(List<MvpMachineEnergy> records)
    {
        if (records == null || records.Count == 0) return;
        try
        {
            using var conn = await SqliteConnectionManager.GetConnectionAsync();
            using var transaction = conn.BeginTransaction();
            try
            {
                var command = conn.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
                    UPDATE MachineWiseEnergyDetails_MVP 
                    SET ServoEnergy = $servo, SpindleEnergy = $spindle, CO2 = $co2 , TotalEnergy = $total, UpdatedTS = $uts, SyncedStatus = $synced
                    WHERE IOTID = $iotid AND Category = $cat;

                    INSERT INTO MachineWiseEnergyDetails_MVP (IOTID, Category, ServoEnergy, SpindleEnergy, CO2, TotalEnergy, UpdatedTS, SyncedStatus)
                    SELECT $iotid, $cat, $servo, $spindle, $co2, $total, $uts, $synced
                    WHERE (SELECT Changes() = 0);";

                var pIOTID = command.CreateParameter(); pIOTID.ParameterName = "$iotid"; command.Parameters.Add(pIOTID);
                var pCat = command.CreateParameter(); pCat.ParameterName = "$cat"; command.Parameters.Add(pCat);
                var pServo = command.CreateParameter(); pServo.ParameterName = "$servo"; command.Parameters.Add(pServo);
                var pSpindle = command.CreateParameter(); pSpindle.ParameterName = "$spindle"; command.Parameters.Add(pSpindle);
                var pCO2 = command.CreateParameter(); pCO2.ParameterName = "$co2"; command.Parameters.Add(pCO2);
                var pTotal = command.CreateParameter(); pTotal.ParameterName = "$total"; command.Parameters.Add(pTotal);
                var pUTS = command.CreateParameter(); pUTS.ParameterName = "$uts"; command.Parameters.Add(pUTS);
                var pSynced = command.CreateParameter(); pSynced.ParameterName = "$synced"; command.Parameters.Add(pSynced);
                foreach (var r in records)
                {
                    pIOTID.Value = r.IOTID;
                    pCat.Value = r.Category ?? (object)DBNull.Value;
                    pServo.Value = r.ServoEnergy;
                    pSpindle.Value = r.SpindleEnergy;
                    pCO2.Value = 0;
                    pTotal.Value = r.TotalEnergy;
                    pUTS.Value = r.UpdatedTS ?? (object)DBNull.Value;
                    pSynced.Value = 0;
                    await command.ExecuteNonQueryAsync();
                }
                await transaction.CommitAsync();
                Logger.WriteDebugLog($"Batch upserted {records.Count} energy records into MachineWiseEnergyDetails_MVP");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Logger.WriteErrorLog($"BatchInsertMvpMachineEnergy transaction error: {ex.Message}");
                throw;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteErrorLog($"BatchInsertMvpMachineEnergy error: {ex.Message}");
        }
    }
    // 6. MachineWiseFocasDetails_MVP
    public static async Task BatchInsertMvpMachineFocas(List<MvpMachineFocas> records)
    {
        if (records == null || records.Count == 0) return;
        try
        {
            using var conn = await SqliteConnectionManager.GetConnectionAsync();
            using var transaction = conn.BeginTransaction();
            try
            {
                var command = conn.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
                    UPDATE MachineWiseFocasDetails_MVP 
                    SET PartCount = $pc, RejCount = $rc, POT = $pot, OT = $ot, CT = $ct, OperatorID = $opid, UpdatedTS = $uts, SyncedStatus = $synced
                    WHERE IOTID = $iotid AND Date = $date AND ShiftID = $sid;

                    INSERT INTO MachineWiseFocasDetails_MVP (IOTID, Date, ShiftID, PartCount, RejCount, POT, OT, CT, OperatorID, UpdatedTS, SyncedStatus)
                    SELECT $iotid, $date, $sid, $pc, $rc, $pot, $ot, $ct, $opid, $uts, $synced
                    WHERE (SELECT Changes() = 0);";

                var pIOTID = command.CreateParameter(); pIOTID.ParameterName = "$iotid"; command.Parameters.Add(pIOTID);
                var pDate = command.CreateParameter(); pDate.ParameterName = "$date"; command.Parameters.Add(pDate);
                var pSID = command.CreateParameter(); pSID.ParameterName = "$sid"; command.Parameters.Add(pSID);
                var pPC = command.CreateParameter(); pPC.ParameterName = "$pc"; command.Parameters.Add(pPC);
                var pRC = command.CreateParameter(); pRC.ParameterName = "$rc"; command.Parameters.Add(pRC);
                var pPOT = command.CreateParameter(); pPOT.ParameterName = "$pot"; command.Parameters.Add(pPOT);
                var pOT = command.CreateParameter(); pOT.ParameterName = "$ot"; command.Parameters.Add(pOT);
                var pCT = command.CreateParameter(); pCT.ParameterName = "$ct"; command.Parameters.Add(pCT);
                var pOpID = command.CreateParameter(); pOpID.ParameterName = "$opid"; command.Parameters.Add(pOpID);
                var pUTS = command.CreateParameter(); pUTS.ParameterName = "$uts"; command.Parameters.Add(pUTS);
                var pSynced = command.CreateParameter(); pSynced.ParameterName = "$synced"; command.Parameters.Add(pSynced);
                foreach (var r in records)
                {
                    pIOTID.Value = r.IOTID;
                    pDate.Value = r.Date ?? (object)DBNull.Value;
                    pSID.Value = r.ShiftID ?? (object)DBNull.Value;
                    pPC.Value = r.PartCount;
                    pRC.Value = r.RejCount;
                    pPOT.Value = r.POT;
                    pOT.Value = r.OT;
                    pCT.Value = r.CT;
                    pOpID.Value = r.OperatorID ?? (object)DBNull.Value;
                    pUTS.Value = r.UpdatedTS ?? (object)DBNull.Value;
                    pSynced.Value = 0;
                    await command.ExecuteNonQueryAsync();
                }
                await transaction.CommitAsync();
                Logger.WriteDebugLog($"Batch upserted {records.Count} focas records into MachineWiseFocasDetails_MVP");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Logger.WriteErrorLog($"BatchInsertMvpMachineFocas transaction error: {ex.Message}");
                throw;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteErrorLog($"BatchInsertMvpMachineFocas error: {ex.Message}");
        }
    }
    // 7. MachineWiseProgramDetails_MVP
    public static async Task BatchInsertMvpMachineProgramProduction(List<MvpMachineProgramProduction> records)
    {
        if (records == null || records.Count == 0) return;
        try
        {
            using var conn = await SqliteConnectionManager.GetConnectionAsync();
            using var transaction = conn.BeginTransaction();
            try
            {
                var command = conn.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
                    UPDATE MachineWiseProgramDetails_MVP
                    SET Actual = $act, Target = $tar, StdCycleTime = $sct, StdLoadUnload = $slu, UpdatedTS = $uts, SyncedStatus = $synced
                    WHERE IOTID = $iotid AND Date = $date AND ShiftID = $sid AND ProgramNo = $prog;

                    INSERT INTO MachineWiseProgramDetails_MVP (IOTID, Date, ShiftID, ProgramNo, Actual, Target, StdCycleTime, StdLoadUnload, UpdatedTS, SyncedStatus)
                    SELECT $iotid, $date, $sid, $prog, $act, $tar, $sct, $slu, $uts, $synced
                    WHERE (SELECT Changes() = 0);";

                var pIOTID = command.CreateParameter(); pIOTID.ParameterName = "$iotid"; command.Parameters.Add(pIOTID);
                var pDate = command.CreateParameter(); pDate.ParameterName = "$date"; command.Parameters.Add(pDate);
                var pSID = command.CreateParameter(); pSID.ParameterName = "$sid"; command.Parameters.Add(pSID);
                var pProg = command.CreateParameter(); pProg.ParameterName = "$prog"; command.Parameters.Add(pProg);
                var pAct = command.CreateParameter(); pAct.ParameterName = "$act"; command.Parameters.Add(pAct);
                var pTar = command.CreateParameter(); pTar.ParameterName = "$tar"; command.Parameters.Add(pTar);
                var pSCT = command.CreateParameter(); pSCT.ParameterName = "$sct"; command.Parameters.Add(pSCT);
                var pSLU = command.CreateParameter(); pSLU.ParameterName = "$slu"; command.Parameters.Add(pSLU);
                var pUTS = command.CreateParameter(); pUTS.ParameterName = "$uts"; command.Parameters.Add(pUTS);
                var pSynced = command.CreateParameter(); pSynced.ParameterName = "$synced"; command.Parameters.Add(pSynced);
                foreach (var r in records)
                {
                    pIOTID.Value = r.IOTID;
                    pDate.Value = r.Date ?? (object)DBNull.Value;
                    pSID.Value = r.ShiftID ?? (object)DBNull.Value;
                    pProg.Value = r.ProgramNo ?? (object)DBNull.Value;
                    pAct.Value = r.Actual;
                    pTar.Value = r.Target;
                    pSCT.Value = r.StdCycleTime;
                    pSLU.Value = r.StdLoadUnload;
                    pUTS.Value = r.UpdatedTS ?? (object)DBNull.Value;
                    pSynced.Value = 0;
                    await command.ExecuteNonQueryAsync();
                }
                await transaction.CommitAsync();
                Logger.WriteDebugLog($"Batch upserted {records.Count} program production records into MachineWiseProgramDetails_MVP");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Logger.WriteErrorLog($"BatchInsertMvpMachineProgramProduction transaction error: {ex.Message}");
                throw;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteErrorLog($"BatchInsertMvpMachineProgramProduction error: {ex.Message}");
        }
    }
    #endregion
}