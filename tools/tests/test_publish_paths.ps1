#Requires -Version 7.0
# 运行：pwsh -NoProfile -File tools/tests/test_publish_paths.ps1
# Linux 验证路径转换与辅助类编译；Windows 额外验证实际长路径文件/目录的 ACL 查询。
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptPath = Join-Path $PSScriptRoot '../../publish.ps1'
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    [IO.Path]::GetFullPath($scriptPath), [ref] $tokens, [ref] $parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
$initializer = $ast.Find({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Initialize-IntegrityReader'
}, $true)
if ($null -eq $initializer) { throw '没有找到完整性读取助手。' }

# 模拟用户已在同一窗口执行过旧版脚本；新脚本不能复用旧的 Win32 查询实现。
if (-not ('Haiyu.Publish.IntegrityReader' -as [type])) {
    Add-Type 'namespace Haiyu.Publish { public static class IntegrityReader { } }'
}
. ([scriptblock]::Create($initializer.Extent.Text))
Initialize-IntegrityReader

$reportedPath = 'C:\Users\user1\Desktop\Haiyu\src\WutheringWavesTool\obj\Generated\CommunityToolkit.Mvvm.SourceGenerators\CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator\Haiyu.ViewModel.GameViewModels.GameContexts.PunishV2GameContextViewModel.RefreshLocalGameUser.g.cs'
if ($reportedPath.Length -ne 264) { throw '回归样本必须保留实机报错的 264 字符长度。' }
$longUnc = '\\server\share\' + ('nested-folder\' * 25) + 'generated.g.cs'
$cases = @(
    @('C:\repo\Haiyu', '\\?\C:\repo\Haiyu'),
    @($reportedPath, ('\\?\' + $reportedPath)),
    @($longUnc, ('\\?\UNC\' + $longUnc.Substring(2))),
    @('\\?\C:\repo\file.cs', '\\?\C:\repo\file.cs'),
    @('\\?\UNC\server\share\file.cs', '\\?\UNC\server\share\file.cs'),
    @('C:/repo/生成文件.cs', '\\?\C:\repo\生成文件.cs')
)
foreach ($case in $cases) {
    $actual = [Haiyu.Publish.IntegrityReaderV2]::ToExtendedPath($case[0])
    if ($actual -cne $case[1]) { throw "扩展路径转换不正确：$($case[0]) → $actual" }
}
foreach ($invalid in @('', ' ', 'src\file.cs', 'C:relative.cs')) {
    $rejected = $false
    try { [void] [Haiyu.Publish.IntegrityReaderV2]::ToExtendedPath($invalid) }
    catch { $rejected = $true }
    if (-not $rejected) { throw "未拒绝非绝对路径：$invalid" }
}
Write-Host 'PASS 264 字符路径、UNC、已有扩展前缀、分隔符和旧辅助类缓存回归'

if ($IsWindows) {
    # 覆盖真实仓库根目录的现有 ACL；只读取，不修改权限或完整性标签。
    $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
    [void] [Haiyu.Publish.IntegrityReaderV2]::ReadPath($repoRoot)
    $temporary = Join-Path ([IO.Path]::GetTempPath()) ('Haiyu-Publish-PathTest-' + [Guid]::NewGuid().ToString('N'))
    $extendedTemporary = [Haiyu.Publish.IntegrityReaderV2]::ToExtendedPath($temporary)
    try {
        $directory = $temporary
        for ($index = 0; $index -lt 12; $index++) { $directory = Join-Path $directory 'source-generator-long-path' }
        [void] [IO.Directory]::CreateDirectory([Haiyu.Publish.IntegrityReaderV2]::ToExtendedPath($directory))
        $file = Join-Path $directory 'generated.g.cs'
        [IO.File]::WriteAllText([Haiyu.Publish.IntegrityReaderV2]::ToExtendedPath($file), '// regression fixture')
        [void] [Haiyu.Publish.IntegrityReaderV2]::ReadPath($file)
        [void] [Haiyu.Publish.IntegrityReaderV2]::ReadPath($directory)
        Write-Host 'PASS Windows 仓库根目录、实际长路径文件/目录完整性查询（未修改 ACL）'
    }
    finally {
        if ([IO.Directory]::Exists($extendedTemporary)) { [IO.Directory]::Delete($extendedTemporary, $true) }
    }
}
else {
    Write-Host 'SKIP Windows 原生 ACL 查询：当前不是 Windows'
}
