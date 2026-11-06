#Requires -Version 7.0
<#
.SYNOPSIS
发布 win-x64 的协议生成器、Haiyu 和 NativeAOT ABI，并部署 ABI。
.EXAMPLE
pwsh -File .\publish.ps1 Release
.EXAMPLE
pwsh -File .\publish.ps1 -Configuration Debug
.NOTES
需要 Windows、.NET 10 SDK（或更新的兼容 SDK）、Windows SDK 和 Visual Studio C++ 构建工具。
仅发布生成器，不自动运行它或改写游戏协议。Debug 同样发布 NativeAOT，保留调试配置。
发现 Low/Untrusted 完整性标签时询问是否改为 Medium；拒绝则停止，不自动提权。
目录设置 (OI)(CI) 继承；已有显式低完整性子项会分别询问。仅修改完整性标签，不重置 DACL。
运行前请退出 Haiyu 和后台 ABI 宿主；旧 ABI 会保留为相邻的 backup 目录。
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$Configuration = if ($Configuration -ieq 'Debug') { 'Debug' } else { 'Release' }

function Initialize-IntegrityReader {
    if ('Haiyu.Publish.IntegrityReader' -as [type]) { return }
    # LABEL_SECURITY_INFORMATION 只查询完整性标签，无需读取审计 SACL 的特权。
    # 使用 SID/RID 判定，避免解析 icacls 的本地化显示名称。
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Haiyu.Publish
{
    public static class IntegrityReader
    {
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint GetNamedSecurityInfoW(string name, int type, uint info,
            IntPtr owner, IntPtr group, IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetAce(IntPtr acl, uint index, out IntPtr ace);
        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(IntPtr token, int info, IntPtr buffer, uint size, out uint required);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        private static uint ReadRid(IntPtr sid)
        {
            byte count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            if (count == 0) throw new InvalidOperationException("完整性 SID 无效。");
            return unchecked((uint)Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)count - 1)));
        }

        public static uint? ReadPath(string path)
        {
            IntPtr sacl, descriptor;
            uint error = GetNamedSecurityInfoW(path, 1, 0x10, IntPtr.Zero, IntPtr.Zero,
                IntPtr.Zero, out sacl, out descriptor);
            if (error != 0) throw new Win32Exception((int)error, "无法检查完整性：" + path + "，Win32 错误：" + error);
            try
            {
                if (sacl == IntPtr.Zero) return null; // 无显式标签，按 Windows 的默认 Medium 处理。
                uint? minimum = null;
                ushort count = unchecked((ushort)Marshal.ReadInt16(sacl, 4));
                for (uint index = 0; index < count; index++)
                {
                    IntPtr ace;
                    if (!GetAce(sacl, index, out ace)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (Marshal.ReadByte(ace) != 0x11) continue; // SYSTEM_MANDATORY_LABEL_ACE
                    uint rid = ReadRid(IntPtr.Add(ace, 8));
                    if (!minimum.HasValue || rid < minimum.Value) minimum = rid;
                }
                return minimum;
            }
            finally { if (descriptor != IntPtr.Zero) LocalFree(descriptor); }
        }

        public static uint ReadProcess()
        {
            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), 0x8, out token))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                uint size;
                GetTokenInformation(token, 25, IntPtr.Zero, 0, out size); // TokenIntegrityLevel
                if (size == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                IntPtr buffer = Marshal.AllocHGlobal(checked((int)size));
                try
                {
                    if (!GetTokenInformation(token, 25, buffer, size, out size))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    return ReadRid(Marshal.ReadIntPtr(buffer));
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseHandle(token); }
        }
    }
}
'@
}

function Assert-PathIntegrity {
    param([string] $Path)

    $rid = [Haiyu.Publish.IntegrityReader]::ReadPath($Path)
    if ($null -ne $rid -and $rid -lt 8192) {
        & $script:Icacls $Path | Out-Host
        $isDirectory = Test-Path -LiteralPath $Path -PathType Container
        $scope = if ($isDirectory) { '目录及其子项继承标签' } else { '此文件的完整性标签' }
        Write-Host "发现 Low/Untrusted 完整性标签 S-1-16-$rid：$Path" -ForegroundColor Yellow
        [string] $answer = Read-Host "是否将${scope}改为 Medium（中完整性）？目标：$Path [y/N]"
        if ($answer.Trim() -notin @('y', 'yes', '是')) {
            throw "用户未同意修改完整性标签，停止发布：$Path"
        }

        $level = if ($isDirectory) { '(OI)(CI)M' } else { 'M' }
        & $script:Icacls $Path '/setintegritylevel' $level | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "完整性标签修改失败，退出码：$LASTEXITCODE。请确认当前终端具有修改权限：$Path"
        }
        $verifiedRid = [Haiyu.Publish.IntegrityReader]::ReadPath($Path)
        if ($null -ne $verifiedRid -and $verifiedRid -ne 8192) {
            throw "修改后复核失败，完整性仍为 S-1-16-$verifiedRid：$Path。停止发布。"
        }
        Write-Host "完整性复核通过（Medium）：$Path" -ForegroundColor Green
    }
}

function Assert-TreeIntegrity {
    param([string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) { return }
    Assert-PathIntegrity $Path
    # PS7 默认不跟随目录符号链接；发布入口文件还会单独检查。
    Get-ChildItem -LiteralPath $Path -Force -Recurse | ForEach-Object {
        Assert-PathIntegrity $_.FullName
    }
}

function Invoke-DotNet {
    param([string[]] $Arguments)

    Write-Host ("`n> dotnet " + ($Arguments -join ' ')) -ForegroundColor Cyan
    & $script:Dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet 执行失败，退出码：$LASTEXITCODE。停止后续发布和部署。" }
}

function Publish-Project {
    param([string] $Project, [string] $Output, [string[]] $Properties)

    $arguments = @('publish', $Project, '--configuration', $Configuration, '--runtime', 'win-x64',
        '--self-contained', 'true', '--output', $Output, '-m:1', '-nr:false', '-p:UseSharedCompilation=false')
    Invoke-DotNet ($arguments + $Properties)
}

function Install-Abi {
    param([string] $Source, [string] $Destination)

    $parent = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $suffix = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
    $suffix += '-' + [Guid]::NewGuid().ToString('N')
    $staging = Join-Path $parent ".Haiyu.ABI-$suffix.new"
    $backup = "$Destination.backup-$suffix"
    $previousMoved = $false
    try {
        New-Item -ItemType Directory -Path $staging | Out-Null
        Get-ChildItem -LiteralPath $Source -Force | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $staging -Recurse -Force
        }
        Assert-TreeIntegrity $staging
        if (-not (Test-Path -LiteralPath (Join-Path $staging 'Haiyu.ABI.dll') -PathType Leaf)) {
            throw 'ABI 暂存目录缺少 Haiyu.ABI.dll，不执行部署。'
        }
        if (Test-Path -LiteralPath $Destination) {
            [IO.Directory]::Move($Destination, $backup)
            $previousMoved = $true
        }
        try {
            [IO.Directory]::Move($staging, $Destination)
        }
        catch {
            $deploymentError = $_.Exception
            if ($previousMoved) {
                try { [IO.Directory]::Move($backup, $Destination) }
                catch { throw "部署失败且无法自动回滚。旧 ABI 保留在 $backup。请退出 Haiyu/ABI 宿主后恢复该目录。$($_.Exception.Message)" }
            }
            throw $deploymentError
        }
        if ($previousMoved) { Write-Host "旧 ABI 已保留：$backup" }
    }
    finally {
        if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    }
}

$exitCode = 0
$locationPushed = $false
try {
    if (-not $IsWindows) { throw '此脚本仅支持 Windows 上的 PowerShell 7。' }
    $command = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $command) {
        throw '未找到 .NET SDK。请安装 .NET 10 SDK 后重试：https://dotnet.microsoft.com/download/dotnet/10.0'
    }
    $script:Dotnet = $command.Source
    Push-Location -LiteralPath $PSScriptRoot
    $locationPushed = $true
    $sdks = @(& $script:Dotnet --list-sdks 2>&1)
    if ($LASTEXITCODE -ne 0 -or -not ($sdks -match '^\d+\.\d+\.\d+')) {
        throw '本机没有可用的 .NET SDK（仅安装 Runtime 也无法编译）。请安装 .NET 10 SDK 后重试。'
    }
    $sdkVersion = (& $script:Dotnet --version 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch '^(\d+)\.' -or [int]$Matches[1] -lt 10) {
        throw "当前目录未选用 .NET 10 或更新的兼容 SDK：$sdkVersion。请检查 SDK 安装及 global.json。"
    }
    Write-Host "SDK：$sdkVersion；配置：$Configuration；目标：win-x64"
    if (Get-Process -Name Haiyu -ErrorAction SilentlyContinue) {
        throw '请先退出 Haiyu 再发布，避免正在运行的 UI/ABI 与新版本混用。'
    }

    $generatorProject = Join-Path $PSScriptRoot 'tools/WavesProtocolGenerator/WavesProtocolGenerator.csproj'
    $uiProject = Join-Path $PSScriptRoot 'src/WutheringWavesTool/Haiyu.csproj'
    $abiProject = Join-Path $PSScriptRoot 'src/Extensions/Haiyu.ABI/Haiyu.ABI.csproj'
    foreach ($project in @($generatorProject, $uiProject, $abiProject)) {
        if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw "仓库文件缺失：$project" }
    }
    if ([string]::IsNullOrWhiteSpace($env:USERPROFILE)) { throw 'USERPROFILE 为空，无法确定 ABI 部署位置。' }
    $generatorOutput = Join-Path $PSScriptRoot 'tools/WavesProtocolGenerator/bin/win-x64/publish'
    $uiOutput = Join-Path $PSScriptRoot 'src/WutheringWavesTool/bin/win-x64/publish'
    $abiOutput = Join-Path $PSScriptRoot 'src/Extensions/Haiyu.ABI/bin/win-x64/publish'
    $abiDestination = Join-Path $env:USERPROFILE 'Waves/Haiyu.ABI'
    $script:Icacls = (Get-Command icacls.exe -CommandType Application -ErrorAction Stop).Source
    Initialize-IntegrityReader
    if ([Haiyu.Publish.IntegrityReader]::ReadProcess() -lt 8192) {
        throw '当前 PowerShell 进程使用 Low/Untrusted IL 令牌，请从正常完整性的终端重新运行。'
    }
    & $script:Icacls $PSScriptRoot | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'icacls 无法读取仓库 ACL，停止发布。' }
    Write-Host '正在检查仓库及现有发布文件的完整性标签……'
    Assert-PathIntegrity $script:Dotnet
    Assert-TreeIntegrity $PSScriptRoot
    Assert-PathIntegrity $env:USERPROFILE
    $wavesDirectory = Join-Path $env:USERPROFILE 'Waves'
    if (Test-Path -LiteralPath $wavesDirectory) { Assert-PathIntegrity $wavesDirectory }
    Assert-TreeIntegrity $abiDestination
    Assert-TreeIntegrity $uiOutput
    if (Test-Path -LiteralPath (Join-Path $uiOutput 'Haiyu.exe')) {
        Assert-PathIntegrity (Join-Path $uiOutput 'Haiyu.exe')
    }
    if ((Test-Path -LiteralPath $abiDestination) -and -not (Test-Path -LiteralPath $abiDestination -PathType Container)) {
        throw "ABI 部署目标不是目录：$abiDestination"
    }

    Publish-Project $generatorProject $generatorOutput @('-p:PublishAot=false', '-p:PublishTrimmed=false')
    Publish-Project $uiProject $uiOutput @('-p:Platform=x64', '-p:PublishProfile=win-x64',
        '-p:PublishAot=true', '-p:PublishTrimmed=true', '-p:PublishReadyToRun=false')
    Publish-Project $abiProject $abiOutput @('-p:PublishAot=true', '-p:PublishTrimmed=true',
        '-p:PublishReadyToRun=false', '-p:NativeLib=Shared')
    foreach ($entry in @((Join-Path $generatorOutput 'WavesProtocolGenerator.exe'),
            (Join-Path $uiOutput 'Haiyu.exe'), (Join-Path $abiOutput 'Haiyu.ABI.dll'))) {
        if (-not (Test-Path -LiteralPath $entry -PathType Leaf)) { throw "发布入口文件缺失：$entry" }
        Assert-PathIntegrity $entry
    }
    Assert-TreeIntegrity $generatorOutput
    Assert-TreeIntegrity $uiOutput
    Assert-TreeIntegrity $abiOutput
    Install-Abi $abiOutput $abiDestination
    Write-Host "`n发布完成。" -ForegroundColor Green
    Write-Host "生成器：$generatorOutput"
    Write-Host "Haiyu：$(Join-Path $uiOutput 'Haiyu.exe')"
    Write-Host "ABI：$abiDestination"
}
catch {
    [Console]::Error.WriteLine("发布失败：$($_.Exception.Message)")
    $exitCode = 1
}
finally {
    if ($locationPushed) { Pop-Location }
}
exit $exitCode
