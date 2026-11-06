#Requires -Version 7.0
# 使用真实 .NET 10 SDK 检查发布属性和整个项目引用图，不执行 WinUI 编译或 NativeAOT 链接。
# 运行：pwsh -NoProfile -File tools/tests/test_publish_settings.ps1
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$generatorProject = Join-Path $repoRoot 'tools/WavesProtocolGenerator/WavesProtocolGenerator.csproj'
$uiProject = Join-Path $repoRoot 'src/WutheringWavesTool/Haiyu.csproj'
$abiProject = Join-Path $repoRoot 'src/Extensions/Haiyu.ABI/Haiyu.ABI.csproj'
$generatorOutput = 'unused-generator-output'
$uiOutput = 'unused-ui-output'
$abiOutput = 'unused-abi-output'

$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $repoRoot 'publish.ps1'), [ref] $tokens, [ref] $parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
$publisher = $ast.Find({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Publish-Project'
}, $true)
. ([scriptblock]::Create($publisher.Extent.Text))
$publishCalls = $ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Publish-Project'
}, $true)
if ($publishCalls.Count -ne 3) { throw '应发布三个项目。' }

# 只捕获脚本实际构造的 dotnet 参数，不运行发布命令。
$script:Captured = [Collections.Generic.List[object]]::new()
function Invoke-DotNet {
    param([string[]] $Arguments)
    $script:Captured.Add($Arguments)
}

$temporary = Join-Path ([IO.Path]::GetTempPath()) ('Haiyu-Publish-Settings-' + [Guid]::NewGuid().ToString('N'))
[void] [IO.Directory]::CreateDirectory($temporary)
$validationTargets = Join-Path $temporary 'ValidatePublish.targets'
[IO.File]::WriteAllText($validationTargets, @'
<Project>
  <Target Name="CheckHaiyuPublishGraph" DependsOnTargets="_ComputeToolPackInputsToProcessFrameworkReferences;ProcessFrameworkReferences">
    <Error Condition="'$(IsRoslynComponent)' == 'true' And ('$(PublishAot)' == 'true' Or '$(PublishTrimmed)' == 'true')"
           Text="AOT/trimming leaked into source generator $(MSBuildProjectFullPath)" />
    <Error Condition="'$(MSBuildProjectName)' == 'Haiyu' And ('$(PublishAot)' != 'true' Or '$(PublishTrimmed)' != 'true' Or '$(PublishReadyToRun)' != 'false')"
           Text="UI must retain NativeAOT and trimming without ReadyToRun." />
    <Error Condition="'$(MSBuildProjectName)' == 'Haiyu.ABI' And ('$(PublishAot)' != 'true' Or '$(PublishTrimmed)' != 'true' Or '$(NativeLib)' != 'Shared')"
           Text="ABI must publish a NativeAOT shared library." />
    <Error Condition="'$(Configuration)' != '$(ExpectedPublishConfiguration)'"
           Text="Publish profile overwrote the requested configuration." />
    <Message Importance="high" Text="CHECK $(MSBuildProjectName): $(TargetFramework); configuration=$(Configuration); AOT=$(PublishAot); trim=$(PublishTrimmed)" />
    <MSBuild Projects="@(ProjectReference)" Targets="CheckHaiyuPublishGraph" BuildInParallel="false" />
  </Target>
</Project>
'@)

Push-Location -LiteralPath $repoRoot
try {
    # 负对照：同样的真实 SDK 任务必须能重现原 netstandard2.0 NETSDK1207。
    $negativeArguments = @('msbuild', (Join-Path $repoRoot 'src/Haiyu.Analyzers/Haiyu.Analyzers.csproj'),
        '-target:_ComputeToolPackInputsToProcessFrameworkReferences;ProcessFrameworkReferences',
        '-p:PublishAot=true', '-p:PublishTrimmed=true', '-p:RuntimeIdentifier=win-x64',
        '-p:SelfContained=true', '-m:1', '-nr:false', '-verbosity:minimal')
    $negative = @(& $dotnet @negativeArguments 2>&1)
    if ($LASTEXITCODE -eq 0 -or -not ($negative -match 'NETSDK1207')) {
        throw '未能重现源生成器受到全局 NativeAOT 属性影响的错误。'
    }
    Write-Host 'PASS 全局 NativeAOT 属性导致 NETSDK1207 的负对照'

    foreach ($Configuration in @('Release', 'Debug')) {
        $script:Captured.Clear()
        foreach ($call in $publishCalls) { . ([scriptblock]::Create($call.Extent.Text)) }
        foreach ($arguments in $script:Captured) {
            $configurationIndex = [Array]::IndexOf($arguments, '--configuration')
            if ($arguments[$configurationIndex + 1] -cne $Configuration) { throw '发布配置传参错误。' }
            if ($arguments -match '^[-/]p:(PublishAot|PublishTrimmed)=true$') {
                throw '不能用命令行全局属性启用 AOT/裁剪。'
            }
            $properties = @($arguments | Where-Object { $_ -match '^[-/]p:' })
            $checkArguments = @('msbuild', $arguments[1], '-target:CheckHaiyuPublishGraph',
                "-p:Configuration=$Configuration", '-p:RuntimeIdentifier=win-x64', '-p:SelfContained=true',
                '-p:_IsPublishing=true', '-p:EnableWindowsTargeting=true', "-p:CustomAfterMicrosoftCommonTargets=$validationTargets",
                "-p:ExpectedPublishConfiguration=$Configuration", '-m:1', '-nr:false', '-verbosity:minimal')
            & $dotnet @checkArguments @properties | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "项目引用图检查失败：$Configuration / $($arguments[1])" }
        }
        Write-Host "PASS $Configuration：应用本地 NativeAOT、完整引用图及源生成器隔离"
    }
}
finally {
    Pop-Location
    [IO.Directory]::Delete($temporary, $true)
}
