#Requires -Version 7.2
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [guid] $SubscriptionId,

    [Parameter(Mandatory)]
    [string] $ResourceGroupName,

    [Parameter(Mandatory)]
    [guid] $PrincipalId,

    [Parameter(Mandatory)]
    [ValidateSet('NoSql', 'Gremlin')]
    [string] $Api,

    [Parameter(Mandatory)]
    [string] $CosmosAccountName,

    [string] $DatabaseName,

    [Parameter(Mandatory)]
    [string] $StorageAccountName,

    [Parameter(Mandatory)]
    [string] $StorageContainerName
)

$ErrorActionPreference = 'Stop'
if ($Api -eq 'NoSql' -and [string]::IsNullOrWhiteSpace($DatabaseName)) {
    throw 'DatabaseName is required for NoSql.'
}

function Invoke-AzJson {
    param([string[]] $Arguments)
    $global:LASTEXITCODE = 0
    $output = & az @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Azure CLI failed: az $($Arguments -join ' ')"
    }
    return $output
}

function Add-RoleAssignment {
    param(
        [string] $Role,
        [string] $Scope
    )
    $existing = @(Invoke-AzJson @(
        'role', 'assignment', 'list',
        '--subscription', $SubscriptionId.ToString(),
        '--scope', $Scope,
        '--only-show-errors', '-o', 'json'
    ) | ConvertFrom-Json | Where-Object {
        $_.principalId -eq $PrincipalId.ToString() -and
        $_.roleDefinitionId -eq $Role
    })
    if ($existing.Count -ne 0) {
        return
    }
    if ($PSCmdlet.ShouldProcess($Scope, "Assign role $Role to $PrincipalId")) {
        $null = Invoke-AzJson @(
            'role', 'assignment', 'create',
            '--subscription', $SubscriptionId.ToString(),
            '--assignee-object-id', $PrincipalId.ToString(),
            '--assignee-principal-type', 'ServicePrincipal',
            '--role', $Role,
            '--scope', $Scope,
            '--only-show-errors', '-o', 'none'
        )
    }
}

function New-OrGet-CustomRole {
    param(
        [string] $RoleName,
        [string] $Description,
        [string[]] $Actions
    )
    $existing = @(
        Invoke-AzJson @(
            'role', 'definition', 'list',
            '--subscription', $SubscriptionId.ToString(),
            '--name', $RoleName,
            '--custom-role-only', 'true',
            '--only-show-errors', '-o', 'json'
        ) | ConvertFrom-Json
    )
    if ($existing.Count -gt 0) {
        return [pscustomobject]@{
            Name = [string] $existing[0].name
            Id = [string] $existing[0].id
        }
    }

    $roleDefinition = [ordered]@{
        Name = [guid]::NewGuid().ToString()
        IsCustom = $true
        RoleName = $RoleName
        Description = $Description
        Actions = $Actions
        NotActions = @()
        DataActions = @()
        NotDataActions = @()
        AssignableScopes = @(
            "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroupName"
        )
    }
    $temp = [IO.Path]::GetTempFileName()
    try {
        $roleDefinition | ConvertTo-Json -Depth 10 |
            Set-Content -LiteralPath $temp -Encoding utf8
        if (-not $PSCmdlet.ShouldProcess(
            $RoleName,
            'Create custom Azure role'
        )) {
            throw 'Cannot continue without creating the required custom role.'
        }
        $created = Invoke-AzJson @(
            'role', 'definition', 'create',
            '--subscription', $SubscriptionId.ToString(),
            '--role-definition', "@$temp",
            '--only-show-errors', '-o', 'json'
        ) | ConvertFrom-Json
        return [pscustomobject]@{
            Name = [string] $created.name
            Id = [string] $created.id
        }
    } finally {
        Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
    }
}

$null = Invoke-AzJson @(
    'account', 'set', '--subscription', $SubscriptionId.ToString(), '-o', 'none'
)
$resourceGroupId = "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroupName"
$accountId = "$resourceGroupId/providers/Microsoft.DocumentDB/databaseAccounts/$CosmosAccountName"
$containerId = "$resourceGroupId/providers/Microsoft.Storage/storageAccounts/$StorageAccountName/blobServices/default/containers/$StorageContainerName"

$storageRoleId = '/subscriptions/{0}/providers/Microsoft.Authorization/roleDefinitions/ba92f5b4-2d11-453d-a403-e96b0029c9fe' -f
    $SubscriptionId
Add-RoleAssignment -Role $storageRoleId -Scope $containerId

$metadataActions = if ($Api -eq 'NoSql') {
    @(
        'Microsoft.DocumentDB/databaseAccounts/read'
        'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/read'
        'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/throughputSettings/read'
        'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/read'
        'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/throughputSettings/read'
    )
} else {
    @(
        'Microsoft.DocumentDB/databaseAccounts/read'
        'Microsoft.DocumentDB/databaseAccounts/gremlinDatabases/read'
        'Microsoft.DocumentDB/databaseAccounts/gremlinDatabases/throughputSettings/read'
        'Microsoft.DocumentDB/databaseAccounts/gremlinDatabases/graphs/read'
        'Microsoft.DocumentDB/databaseAccounts/gremlinDatabases/graphs/throughputSettings/read'
    )
}
$metadataRole = New-OrGet-CustomRole `
    -RoleName "Cosmos LTR $Api Metadata Reader - $ResourceGroupName" `
    -Description "Read Cosmos DB $Api account and metadata for LTR export." `
    -Actions $metadataActions
Add-RoleAssignment -Role $metadataRole.Id -Scope $accountId

if ($Api -eq 'NoSql') {
    $assignments = @(
        Invoke-AzJson @(
            'cosmosdb', 'sql', 'role', 'assignment', 'list',
            '--subscription', $SubscriptionId.ToString(),
            '--resource-group', $ResourceGroupName,
            '--account-name', $CosmosAccountName,
            '--only-show-errors', '-o', 'json'
        ) | ConvertFrom-Json | Where-Object {
            $_.principalId -eq $PrincipalId.ToString() -and
            $_.scope.EndsWith("/dbs/$DatabaseName", [StringComparison]::OrdinalIgnoreCase) -and
            $_.roleDefinitionId -like '*/sqlRoleDefinitions/00000000-0000-0000-0000-000000000001'
        }
    )
    if ($assignments.Count -eq 0 -and $PSCmdlet.ShouldProcess(
        "$CosmosAccountName/dbs/$DatabaseName",
        'Assign Cosmos DB Built-in Data Reader'
    )) {
        $null = Invoke-AzJson @(
            'cosmosdb', 'sql', 'role', 'assignment', 'create',
            '--subscription', $SubscriptionId.ToString(),
            '--resource-group', $ResourceGroupName,
            '--account-name', $CosmosAccountName,
            '--role-definition-name', 'Cosmos DB Built-in Data Reader',
            '--scope', "/dbs/$DatabaseName",
            '--principal-id', $PrincipalId.ToString(),
            '--only-show-errors', '-o', 'none'
        )
    }
} else {
    $keyRole = New-OrGet-CustomRole `
        -RoleName "Cosmos LTR Gremlin Key Reader - $ResourceGroupName" `
        -Description "Read the Gremlin account key required by the Cosmos DB Gremlin API." `
        -Actions @(
            'Microsoft.DocumentDB/databaseAccounts/listKeys/action'
        )
    Add-RoleAssignment -Role $keyRole.Id -Scope $accountId
}

[pscustomobject]@{
    Api = $Api
    PrincipalId = $PrincipalId
    CosmosAccount = $CosmosAccountName
    StorageContainer = $containerId
    MetadataRole = $metadataRole.Name
    StorageRole = 'Storage Blob Data Contributor'
    DataAccessRole = if ($Api -eq 'NoSql') {
        'Cosmos DB Built-in Data Reader'
    } else {
        "Cosmos LTR Gremlin Key Reader - $ResourceGroupName"
    }
} | ConvertTo-Json -Depth 5
