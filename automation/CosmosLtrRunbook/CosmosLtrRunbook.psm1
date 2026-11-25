Set-StrictMode -Version Latest

function Write-CosmosLtrDiagnostic {
    param(
        [string] $Stage,
        [string] $Message,
        [hashtable] $Details = @{}
    )

    $record = [ordered] @{
        timestampUtc = (Get-Date).ToUniversalTime().ToString('o')
        application = 'LTRTool'
        stage = $Stage
        message = $Message
    }
    foreach ($entry in $Details.GetEnumerator()) {
        $record[$entry.Key] = $entry.Value
    }
    Write-Information ($record | ConvertTo-Json -Compress -Depth 12) -InformationAction Continue
}

function Add-CosmosLtrArgument {
    param(
        [System.Collections.Generic.List[string]] $Arguments,
        [string] $Name,
        [hashtable] $BoundParameters,
        [string] $ParameterName = $Name.TrimStart('-').Replace('-', '')
    )

    if ($BoundParameters.ContainsKey($ParameterName) -and
        -not [string]::IsNullOrWhiteSpace([string] $BoundParameters[$ParameterName])) {
        $Arguments.Add($Name)
        $Arguments.Add([string] $BoundParameters[$ParameterName])
    }
}

function Invoke-CosmosLtrExport {
    [CmdletBinding()]
    param(
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
    if (-not (Test-Path -LiteralPath $ConfigurationPath -PathType Leaf)) {
        throw "Cosmos configuration file was not found: $ConfigurationPath"
    }
    if (-not (Test-Path -LiteralPath $DotNetPath -PathType Leaf)) {
        throw "dotnet host was not found: $DotNetPath"
    }
    if (-not (Test-Path -LiteralPath $ApplicationPath -PathType Leaf)) {
        throw "LTRTool application was not found: $ApplicationPath"
    }

    $arguments = [System.Collections.Generic.List[string]]::new()
    $arguments.AddRange([string[]] @(
        $ApplicationPath,
        'export',
        '--config', $ConfigurationPath,
        '--api', $Api,
        '--parallelism', $Parallelism.ToString([Globalization.CultureInfo]::InvariantCulture),
        '--max-ru-per-second', $MaxRuPerSecond.ToString([Globalization.CultureInfo]::InvariantCulture),
        '--retention-months', $RetentionMonths.ToString([Globalization.CultureInfo]::InvariantCulture),
        '--change-feed-catch-up-seconds', $ChangeFeedCatchUpSeconds.ToString([Globalization.CultureInfo]::InvariantCulture),
        '--sample-query-count', $SampleQueryCount.ToString([Globalization.CultureInfo]::InvariantCulture),
        '--replica-required', $ReplicaRequired.ToString([Globalization.CultureInfo]::InvariantCulture)
    ))
    $pairs = @(
        @('--subscription-id', 'SubscriptionId'),
        @('--resource-group', 'ResourceGroupName'),
        @('--account', 'AccountName'),
        @('--source-region', 'SourceRegion'),
        @('--database', 'DatabaseName'),
        @('--containers', 'NoSqlContainers'),
        @('--graphs', 'GremlinGraphs'),
        @('--storage-account', 'StorageAccountName'),
        @('--storage-container', 'StorageContainerName'),
        @('--storage-blob-endpoint', 'StorageBlobEndpoint'),
        @('--replica-blob-endpoint', 'ReplicaBlobEndpoint'),
        @('--managed-identity-client-id', 'ManagedIdentityClientId'),
        @('--backup-id', 'BackupId'),
        @('--month', 'BackupMonth')
    )
    foreach ($pair in $pairs) {
        Add-CosmosLtrArgument -Arguments $arguments -Name $pair[0] `
            -BoundParameters $PSBoundParameters -ParameterName $pair[1]
    }

    Write-CosmosLtrDiagnostic -Stage 'start' -Message 'Starting Cosmos DB LTR export.' -Details @{
        Api = $Api
        AccountName = if ($AccountName) { $AccountName } else { '(configured)' }
        DatabaseName = if ($DatabaseName) { $DatabaseName } else { '(configured)' }
        BackupId = if ($BackupId) { $BackupId } else { '(generated)' }
        TimeoutMinutes = $TimeoutMinutes
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $DotNetPath
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    foreach ($argument in $arguments) {
        $null = $startInfo.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw 'Failed to start LTRTool.'
    }
    $outputTask = $process.StandardOutput.ReadToEndAsync()
    $errorTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutMinutes * 60 * 1000)) {
        try {
            $process.Kill($true)
        } catch {
            Write-Warning "LTRTool timed out and process cleanup failed: $($_.Exception.Message)"
        }
        throw "LTRTool exceeded the $TimeoutMinutes minute timeout."
    }
    $standardOutput = $outputTask.GetAwaiter().GetResult()
    $standardError = $errorTask.GetAwaiter().GetResult()
    foreach ($line in @($standardOutput -split "`r?`n")) {
        if (-not [string]::IsNullOrWhiteSpace($line)) {
            Write-Information $line -InformationAction Continue
        }
    }
    foreach ($line in @($standardError -split "`r?`n")) {
        if (-not [string]::IsNullOrWhiteSpace($line)) {
            Write-Warning $line
        }
    }
    if ($process.ExitCode -ne 0) {
        throw "LTRTool failed with exit code $($process.ExitCode). Review the preceding structured logs."
    }

    Write-CosmosLtrDiagnostic -Stage 'completed' -Message 'Cosmos DB LTR export completed.' -Details @{
        ExitCode = $process.ExitCode
        Api = $Api
        BackupId = if ($BackupId) { $BackupId } else { '(generated)' }
    }
    [pscustomobject] @{
        Operation = 'export'
        Status = 'Completed'
        ExitCode = $process.ExitCode
        Api = $Api
        BackupId = $BackupId
    }
}

Export-ModuleMember -Function Invoke-CosmosLtrExport
