Set-StrictMode -Version Latest

function ConvertTo-CosmosStandaloneRunbook {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $RunbookPath,

        [Parameter(Mandatory)]
        [string] $ModulePath
    )

    $ErrorActionPreference = 'Stop'
    $source = Get-Content -LiteralPath $RunbookPath -Raw
    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseInput(
        $source, [ref] $tokens, [ref] $parseErrors)
    if ($parseErrors.Count -gt 0) {
        throw "Runbook source has syntax errors: $RunbookPath. $($parseErrors -join '; ')"
    }
    $imports = @($ast.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.CommandAst] -and
        $node.GetCommandName() -eq 'Import-Module' -and $node.CommandElements.Count -gt 1 -and
        $node.CommandElements[1] -is [System.Management.Automation.Language.StringConstantExpressionAst] -and
        $node.CommandElements[1].Value -eq 'CosmosLtrRunbook'
    }, $true))
    if ($imports.Count -ne 1) {
        throw "Expected exactly one CosmosLtrRunbook import in $RunbookPath; found $($imports.Count)."
    }

    $moduleSource = Get-Content -LiteralPath $ModulePath -Raw
    $replacement = @"
Import-Module (New-Module -Name CosmosLtrRunbook -ScriptBlock {
$moduleSource
}) -Force -ErrorAction Stop
"@
    $content = $source.Remove($imports[0].Extent.StartOffset, $imports[0].Extent.Text.Length).
        Insert($imports[0].Extent.StartOffset, $replacement)
    $null = [System.Management.Automation.Language.Parser]::ParseInput(
        $content, [ref] $tokens, [ref] $parseErrors)
    if ($parseErrors.Count -gt 0) {
        throw "Standalone runbook has syntax errors: $RunbookPath. $($parseErrors -join '; ')"
    }
    [pscustomobject] @{
        Name = [IO.Path]::GetFileNameWithoutExtension($RunbookPath)
        Content = $content
    }
}

function Publish-CosmosStandaloneRunbook {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject[]] $Runbooks,

        [Parameter(Mandatory)]
        [guid] $SubscriptionId,

        [Parameter(Mandatory)]
        [string] $ResourceGroupName,

        [Parameter(Mandatory)]
        [string] $AutomationAccountName,

        [ValidateSet('AzureCloud', 'AzureChinaCloud')]
        [string] $Cloud = 'AzureChinaCloud',

        [ValidateRange(1, 60)]
        [int] $TimeoutMinutes = 20
    )

    $ErrorActionPreference = 'Stop'
    $context = Get-AzContext -ErrorAction Stop
    if ($null -eq $context -or $context.Environment.Name -ne $Cloud) {
        throw "Sign in first with Connect-AzAccount -Environment $Cloud. The publisher does not change clouds or log in automatically."
    }
    $context = Set-AzContext -SubscriptionId $SubscriptionId.ToString() `
        -DefaultProfile $context -Scope Process -ErrorAction Stop
    $azureArguments = @{
        ResourceGroupName = $ResourceGroupName
        AutomationAccountName = $AutomationAccountName
        DefaultProfile = $context
        ErrorAction = 'Stop'
    }
    $deadline = (Get-Date).ToUniversalTime().AddMinutes($TimeoutMinutes)
    $endpoint = if ($Cloud -eq 'AzureChinaCloud') {
        'https://management.chinacloudapi.cn'
    } else {
        'https://management.azure.com'
    }
    $accountPath = '/subscriptions/{0}/resourceGroups/{1}/providers/Microsoft.Automation/automationAccounts/{2}' -f
        $SubscriptionId, [uri]::EscapeDataString($ResourceGroupName), [uri]::EscapeDataString($AutomationAccountName)
    $temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) "cosmos-runbooks-$([guid]::NewGuid())"
    $null = New-Item -ItemType Directory -Path $temporaryDirectory
    $temporaryFiles = [System.Collections.Generic.List[string]]::new()
    try {
        foreach ($runbook in $Runbooks) {
            $path = Join-Path $temporaryDirectory "$($runbook.Name).ps1"
            $temporaryFiles.Add($path)
            [IO.File]::WriteAllText($path, $runbook.Content, [Text.UTF8Encoding]::new($false))
            $null = Import-AzAutomationRunbook @azureArguments -Name $runbook.Name -Path $path `
                -Type PowerShell72 -Force -LogProgress $false -LogVerbose $false
            $null = Publish-AzAutomationRunbook @azureArguments -Name $runbook.Name

            $verified = $false
            while ((Get-Date).ToUniversalTime() -lt $deadline) {
                $published = Get-AzAutomationRunbook @azureArguments -Name $runbook.Name
                if ($null -eq $published) {
                    throw "Runbook metadata missing after publish: $($runbook.Name)."
                }
                if ($published.State -eq 'Published') {
                    if ($published.RunbookType -ne 'PowerShell72') {
                        throw "Runbook $($runbook.Name) is not PowerShell72; refusing to report it as ready."
                    }
                    $contentUri = "$endpoint$accountPath/runbooks/$([uri]::EscapeDataString($runbook.Name))/content?api-version=2023-11-01"
                    $response = Invoke-AzRestMethod -Method GET -Uri $contentUri -DefaultProfile $context -ErrorAction Stop
                    if ([int] $response.StatusCode -ne 200) {
                        throw "Cannot verify published content for $($runbook.Name): HTTP $($response.StatusCode)."
                    }
                    $actual = $response.Content.Replace("`r`n", "`n").TrimEnd("`r", "`n")
                    $expected = $runbook.Content.Replace("`r`n", "`n").TrimEnd("`r", "`n")
                    if ($actual -ceq $expected) {
                        $verified = $true
                        break
                    }
                }
                Start-Sleep -Seconds 5
            }
            if (-not $verified) {
                throw "Timed out verifying the published runtime/content of $($runbook.Name). Publishing does not start a job."
            }
            [pscustomobject] @{
                Name = $runbook.Name
                AutomationAccountName = $AutomationAccountName
                State = 'Published'
                RuntimeVersion = '7.2'
                ContentVerified = $true
                JobsStarted = 0
            }
        }
    } finally {
        foreach ($path in $temporaryFiles) {
            if (Test-Path -LiteralPath $path) {
                Remove-Item -LiteralPath $path -Force
            }
        }
        if (Test-Path -LiteralPath $temporaryDirectory) {
            Remove-Item -LiteralPath $temporaryDirectory -Force
        }
    }
}

Export-ModuleMember -Function ConvertTo-CosmosStandaloneRunbook, Publish-CosmosStandaloneRunbook
