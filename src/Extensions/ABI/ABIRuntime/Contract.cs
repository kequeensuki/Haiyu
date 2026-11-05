using System;
using System.Collections.Generic;
using System.Text;
using ABI.Models;
using ABIRuntime.Abstractions;

namespace ABIRuntime;

public static class Contract
{
    public static PrivilegedServiceContract<
        ABISystemConfigRequest,
        ABISystemConfigProgress,
        RunResult
    > ABISystemConfigContract =>
        new PrivilegedServiceContract<ABISystemConfigRequest, ABISystemConfigProgress, RunResult>(
            "haiyu.systemInit.v1",
            ABIJsonContext.Default.ABISystemConfigRequest,
            ABIJsonContext.Default.ABISystemConfigProgress,
            ABIJsonContext.Default.RunResult
        );

    public static PrivilegedServiceContract<
        CleanMemoryRequest,
        CleanMemoryProgress,
        RunResult
    > CleanMemoryContract =>
        new(
            "haiyu.clean.v1",
            ABIJsonContext.Default.CleanMemoryRequest,
            ABIJsonContext.Default.CleanMemoryProgress,
            ABIJsonContext.Default.RunResult
        );

    public static PrivilegedServiceContract<
        CMonitorRequest,
        CMonitorProgress,
        RunResult
    > ComputerMonitorContract =>
        new(
            "haiyu.monitor.v1",
            ABIJsonContext.Default.CMonitorRequest,
            ABIJsonContext.Default.CMonitorProgress,
            ABIJsonContext.Default.RunResult
        );

    public static PrivilegedServiceContract<
        FpsMonitorRequest,
        FpsMonitorProgress,
        RunResult
    > FpsMonitorContract =>
        new(
            "haiyu.fpsMonitor.v1",
            ABIJsonContext.Default.FpsMonitorRequest,
            ABIJsonContext.Default.FpsMonitorProgress,
            ABIJsonContext.Default.RunResult
        );

    public static PrivilegedServiceContract<
        XboxExtOpenRequest,
        XboxReportProcess,
        RunResult
    > XboxOpenControllerContract =>
        new(
            "haiyu.xboxOpen.v1",
            ABIJsonContext.Default.XboxExtOpenRequest,
            ABIJsonContext.Default.XboxReportProcess,
            ABIJsonContext.Default.RunResult
        );

    public static PrivilegedServiceContract<
        XboxExtCloseRequest,
        XboxReportProcess,
        RunResult
    > XboxCloseControllerContract =>
        new(
            "haiyu.xboxClose.v1",
            ABIJsonContext.Default.XboxExtCloseRequest,
            ABIJsonContext.Default.XboxReportProcess,
            ABIJsonContext.Default.RunResult
        );
    /// <summary>单次采集四个 XInput 槽位，通过进度返回快照。</summary>
    public static PrivilegedServiceContract<XboxSessionRequest, XboxSessionReportProcess, RunResult>
        XboxSessionContract => new(
            "haiyu.xboxSession.v1",
            ABIJsonContext.Default.XboxSessionRequest,
            ABIJsonContext.Default.XboxSessionReportProcess,
            ABIJsonContext.Default.RunResult);

    /// <summary>
    /// 游戏数据导出（抓包 + 解密 + 落盘）。
    /// 响应体是 <see cref="GameDataExportResult"/> 而非 <see cref="RunResult"/>——
    /// 背包数据体量大，必须随结果返回。
    /// </summary>
    public static PrivilegedServiceContract<
        GameDataExportRequest,
        GameDataExportProgress,
        GameDataExportResult
    > GameDataExportContract =>
        new(
            "haiyu.gameDataExport.v1",
            ABIJsonContext.Default.GameDataExportRequest,
            ABIJsonContext.Default.GameDataExportProgress,
            ABIJsonContext.Default.GameDataExportResult
        );
}
