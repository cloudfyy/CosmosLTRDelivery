#Requires -Version 7.2
<#
.SYNOPSIS
Runs a parameterized Cosmos DB long-term recovery-set export.
.DESCRIPTION
This runbook is a thin PowerShell 7.2 wrapper. The C# LTRTool executable performs
all Cosmos DB and Blob SDK operations on the extension-based Hybrid Runbook Worker.
#>
[CmdletBinding()]
param(
    [string] $OptionsJson,

    [ValidateSet('Both', 'NoSql', 'Gremlin')]
    [string] $Api = 'Both',
    [string] $SubscriptionId,
    [string] $ResourceGroupName,
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
    [ValidateNotNullOrEmpty()]
    [string] $ConfigurationPath = '/opt/cosmos-ltr/config.json',

    [ValidateNotNullOrEmpty()]
    [string] $DotNetPath = '/usr/bin/dotnet',

    [ValidateNotNullOrEmpty()]
    [string] $ApplicationPath = '/opt/cosmos-ltr/publish/LTRTool/LTRTool.dll',
    [ValidateRange(1, 600)]
    [int] $TimeoutMinutes = 120
)

$ErrorActionPreference = 'Stop'
Import-Module CosmosLtrRunbook -ErrorAction Stop
$invokeParameters = @{
    Api = $Api
    SubscriptionId = $SubscriptionId
    ResourceGroupName = $ResourceGroupName
    AccountName = $AccountName
    SourceRegion = $SourceRegion
    DatabaseName = $DatabaseName
    NoSqlContainers = $NoSqlContainers
    GremlinGraphs = $GremlinGraphs
    StorageAccountName = $StorageAccountName
    StorageContainerName = $StorageContainerName
    StorageBlobEndpoint = $StorageBlobEndpoint
    ReplicaBlobEndpoint = $ReplicaBlobEndpoint
    ManagedIdentityClientId = $ManagedIdentityClientId
    BackupId = $BackupId
    BackupMonth = $BackupMonth
    Parallelism = $Parallelism
    MaxRuPerSecond = $MaxRuPerSecond
    RetentionMonths = $RetentionMonths
    ChangeFeedCatchUpSeconds = $ChangeFeedCatchUpSeconds
    SampleQueryCount = $SampleQueryCount
    ReplicaRequired = $ReplicaRequired
    ConfigurationPath = $ConfigurationPath
    DotNetPath = $DotNetPath
    ApplicationPath = $ApplicationPath
    TimeoutMinutes = $TimeoutMinutes
}
if (-not [string]::IsNullOrWhiteSpace($OptionsJson)) {
    $optionsText = $OptionsJson
    if ($OptionsJson.Length % 2 -eq 0 -and $OptionsJson -match '^[0-9A-Fa-f]+$') {
        $optionsBytes = [byte[]]::new($OptionsJson.Length / 2)
        for ($index = 0; $index -lt $optionsBytes.Length; $index++) {
            $optionsBytes[$index] = [Convert]::ToByte(
                $OptionsJson.Substring($index * 2, 2), 16)
        }
        $optionsText = [Text.Encoding]::UTF8.GetString($optionsBytes)
    }
    $options = $optionsText | ConvertFrom-Json -AsHashtable -ErrorAction Stop
    $allowedOptions = [System.Collections.Generic.HashSet[string]]::new(
        [string[]] $invokeParameters.Keys,
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $options.GetEnumerator()) {
        if (-not $allowedOptions.Contains([string] $entry.Key)) {
            throw "Unknown LTR job option '$($entry.Key)'."
        }
        $invokeParameters[[string] $entry.Key] = $entry.Value
    }
}
Invoke-CosmosLtrExport @invokeParameters
