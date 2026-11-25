#Requires -Version 7.2
<#
.SYNOPSIS
Validates, generates, or publishes the self-contained Cosmos LTR runbook.
.DESCRIPTION
Validate is offline. Generate writes portal-uploadable standalone PowerShell 7.2 files.
Publish replaces only Export-CosmosLTR and can link the already-created monthly
schedule in the selected Automation Account. It never starts a backup job.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [ValidateSet('Validate', 'Generate', 'Publish')]
    [string] $Mode = 'Validate',

    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'portal'),

    [guid] $SubscriptionId,

    [string] $ResourceGroupName,

    [string] $AutomationAccountName,

    [bool] $LinkMonthlySchedule = $false,

    [string] $ScheduleName = 'Monthly-CosmosLTR',

    [string] $HybridWorkerGroupName,

    [ValidateSet('AzureCloud', 'AzureChinaCloud')]
    [string] $Cloud = 'AzureChinaCloud',

    [ValidateRange(1, 60)]
    [int] $TimeoutMinutes = 20
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'CosmosRunbookPublishing.psm1') -Force -ErrorAction Stop
$runbooks = @(
    ConvertTo-CosmosStandaloneRunbook `
        -RunbookPath (Join-Path $PSScriptRoot 'Export-CosmosLTR.ps1') `
        -ModulePath (Join-Path $PSScriptRoot 'CosmosLtrRunbook\CosmosLtrRunbook.psm1')
)
if ($Mode -eq 'Validate') {
    Write-Output 'Local validation passed: 1 self-contained PowerShell 7.2 runbook. No Azure access, uploads, or jobs started.'
    return
}
if ($Mode -eq 'Generate') {
    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        throw 'OutputDirectory is required for Generate.'
    }
    $null = New-Item -ItemType Directory -Path $OutputDirectory -Force
    foreach ($runbook in $runbooks) {
        $path = Join-Path $OutputDirectory "$($runbook.Name).standalone.ps1"
        [IO.File]::WriteAllText($path, $runbook.Content, [Text.UTF8Encoding]::new($false))
        [pscustomobject] @{
            Name = $runbook.Name
            Path = $path
            Runtime = 'PowerShell 7.2'
            Bytes = (Get-Item -LiteralPath $path).Length
            Sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        }
    }
    Write-Output 'Generated portal upload files. No Azure access, publishing, or job execution occurred.'
    return
}
if ($SubscriptionId -eq [guid]::Empty -or [string]::IsNullOrWhiteSpace($ResourceGroupName) -or
    [string]::IsNullOrWhiteSpace($AutomationAccountName)) {
    throw 'SubscriptionId, ResourceGroupName, and AutomationAccountName are required for Publish.'
}
if (-not $PSCmdlet.ShouldProcess(
    "$Cloud / $SubscriptionId / $ResourceGroupName / $AutomationAccountName",
    'Replace/publish Export-CosmosLTR and optionally link an existing monthly schedule; do not start jobs'
)) {
    return
}
Import-Module Az.Accounts -MinimumVersion 2.12.0 -ErrorAction Stop
Import-Module Az.Automation -MinimumVersion 1.12.0 -ErrorAction Stop
Publish-CosmosStandaloneRunbook -Runbooks $runbooks -SubscriptionId $SubscriptionId `
    -ResourceGroupName $ResourceGroupName -AutomationAccountName $AutomationAccountName `
    -Cloud $Cloud -TimeoutMinutes $TimeoutMinutes
if ($LinkMonthlySchedule) {
    if ([string]::IsNullOrWhiteSpace($HybridWorkerGroupName)) {
        throw 'HybridWorkerGroupName is required when LinkMonthlySchedule is true.'
    }
    $scheduledRunbooks = @(Get-AzAutomationScheduledRunbook `
        -ResourceGroupName $ResourceGroupName `
        -AutomationAccountName $AutomationAccountName `
        -ErrorAction Stop)
    $existing = @($scheduledRunbooks | Where-Object {
            $_.RunbookName -eq 'Export-CosmosLTR' -and
            $_.ScheduleName -eq $ScheduleName -and
            $_.RunOn -eq $HybridWorkerGroupName
        })
    if ($existing.Count -eq 0) {
        Register-AzAutomationScheduledRunbook `
            -ResourceGroupName $ResourceGroupName `
            -AutomationAccountName $AutomationAccountName `
            -RunbookName 'Export-CosmosLTR' `
            -ScheduleName $ScheduleName `
            -RunOn $HybridWorkerGroupName `
            -ErrorAction Stop | Out-Null
    }
    Write-Output "Monthly schedule '$ScheduleName' targets Export-CosmosLTR on '$HybridWorkerGroupName'. No job was started."
}
