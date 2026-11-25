Set-StrictMode -Version Latest

function Start-CosmosAutomationJob {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [guid] $SubscriptionId,

        [Parameter(Mandatory)]
        [string] $ResourceGroupName,

        [Parameter(Mandatory)]
        [string] $AutomationAccountName,

        [Parameter(Mandatory)]
        [string] $RunbookName,

        [Parameter(Mandatory)]
        [hashtable] $Parameters,

        [Parameter(Mandatory)]
        [string] $HybridWorkerGroupName,

        [ValidateSet('AzureCloud', 'AzureChinaCloud')]
        [string] $Cloud = 'AzureChinaCloud',

        [switch] $Wait,

        [ValidateRange(30, 3600)]
        [int] $MaxWaitSeconds = 900
    )

    $ErrorActionPreference = 'Stop'
    $context = Get-AzContext -ErrorAction Stop
    if ($null -eq $context -or $context.Environment.Name -ne $Cloud) {
        throw "Sign in first with Connect-AzAccount -Environment $Cloud. This script does not change clouds."
    }
    $context = Set-AzContext -SubscriptionId $SubscriptionId.ToString() `
        -DefaultProfile $context -Scope Process -ErrorAction Stop
    $arguments = @{
        ResourceGroupName = $ResourceGroupName
        AutomationAccountName = $AutomationAccountName
        Name = $RunbookName
        RunOn = $HybridWorkerGroupName
        Parameters = $Parameters
        DefaultProfile = $context
        ErrorAction = 'Stop'
    }
    if ($Wait) {
        $arguments.Wait = $true
        $arguments.MaxWaitSeconds = $MaxWaitSeconds
    }
    Start-AzAutomationRunbook @arguments
}

Export-ModuleMember -Function Start-CosmosAutomationJob
