#Requires -Version 7.2
<#
.SYNOPSIS
Starts a parameterized Cosmos logical backup on a Hybrid Runbook Worker.
.DESCRIPTION
This operator script only starts an already-published runbook job. It does not deploy,
publish, or edit the runbook.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory)]
    [guid] $SubscriptionId,

    [Parameter(Mandatory)]
    [string] $ResourceGroupName,

    [Parameter(Mandatory)]
    [string] $AutomationAccountName,

    [ValidateSet('AzureCloud', 'AzureChinaCloud')]
    [string] $Cloud = 'AzureChinaCloud',

    [Parameter(Mandatory)]
    [string] $HybridWorkerGroupName,

    [string] $RunbookName = 'Export-CosmosLTR',

    [ValidateSet('Both', 'NoSql', 'Gremlin')]
    [string] $Api = 'Both',

    [string] $SourceSubscriptionId,

    [string] $CosmosResourceGroupName,

    [string] $AccountName,

    [string] $SourceRegion,

    [string] $DatabaseName,

    [string] $NoSqlContainers,

    [string] $GremlinGraphs,

    [string] $StorageAccountName,

    [string] $StorageContainerName,

    [string] $StorageBlobEndpoint,

    [string] $ReplicaBlobEndpoint,

    [string] $ManagedIdentityClientId,

    [string] $BackupId,

    [string] $BackupMonth,

    [string] $ConfigurationPath,

    [string] $DotNetPath,

    [string] $ApplicationPath,

    [ValidateRange(1, 64)]
    [int] $Parallelism = 4,

    [ValidateRange(1, 1000000)]
    [double] $MaxRuPerSecond = 200,

    [ValidateRange(1, 120)]
    [int] $RetentionMonths = 13,

    [ValidateRange(0, 3600)]
    [int] $ChangeFeedCatchUpSeconds = 300,

    [ValidateRange(1, 100)]
    [int] $SampleQueryCount = 3,

    [bool] $ReplicaRequired = $true,

    [ValidateRange(1, 600)]
    [int] $TimeoutMinutes = 120,

    [switch] $Wait,

    [ValidateRange(30, 3600)]
    [int] $MaxWaitSeconds = 900
)

$ErrorActionPreference = 'Stop'
Import-Module Az.Accounts -MinimumVersion 2.12.0 -ErrorAction Stop
Import-Module Az.Automation -MinimumVersion 1.12.0 -ErrorAction Stop
Import-Module (Join-Path $PSScriptRoot 'CosmosAutomationJobs.psm1') -Force -ErrorAction Stop
$jobOptions = [ordered] @{
    Api = $Api
    Parallelism = $Parallelism
    MaxRuPerSecond = $MaxRuPerSecond
    RetentionMonths = $RetentionMonths
    ChangeFeedCatchUpSeconds = $ChangeFeedCatchUpSeconds
    SampleQueryCount = $SampleQueryCount
    ReplicaRequired = $ReplicaRequired
    TimeoutMinutes = $TimeoutMinutes
}
foreach ($pair in @(
    @('SubscriptionId', $SourceSubscriptionId),
    @('ResourceGroupName', $CosmosResourceGroupName),
    @('AccountName', $AccountName),
    @('SourceRegion', $SourceRegion),
    @('DatabaseName', $DatabaseName),
    @('NoSqlContainers', $NoSqlContainers),
    @('GremlinGraphs', $GremlinGraphs),
    @('StorageAccountName', $StorageAccountName),
    @('StorageContainerName', $StorageContainerName),
    @('StorageBlobEndpoint', $StorageBlobEndpoint),
    @('ReplicaBlobEndpoint', $ReplicaBlobEndpoint),
    @('ManagedIdentityClientId', $ManagedIdentityClientId),
    @('BackupId', $BackupId),
    @('BackupMonth', $BackupMonth),
    @('ConfigurationPath', $ConfigurationPath),
    @('DotNetPath', $DotNetPath),
    @('ApplicationPath', $ApplicationPath)
)) {
    if (-not [string]::IsNullOrWhiteSpace([string] $pair[1])) {
        $jobOptions[$pair[0]] = $pair[1]
    }
}
$optionsJson = $jobOptions | ConvertTo-Json -Compress -Depth 5
$optionsHex = -join (
    [Text.Encoding]::UTF8.GetBytes($optionsJson) |
        ForEach-Object { $_.ToString('x2') }
)
$parameters = @{ OptionsJson = $optionsHex }
if (-not $PSCmdlet.ShouldProcess(
    "$AutomationAccountName / $RunbookName / $HybridWorkerGroupName",
    "Start Cosmos logical backup with Api=$Api"
)) {
    return
}
Start-CosmosAutomationJob -SubscriptionId $SubscriptionId -ResourceGroupName $ResourceGroupName `
    -AutomationAccountName $AutomationAccountName -RunbookName $RunbookName `
    -HybridWorkerGroupName $HybridWorkerGroupName -Parameters $parameters -Cloud $Cloud `
    -Wait:$Wait -MaxWaitSeconds $MaxWaitSeconds
