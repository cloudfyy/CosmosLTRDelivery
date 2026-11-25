# Cosmos DB Long-Term Retention Tool Deployment Guide

## 1. Purpose

This package installs the Cosmos DB Long-Term Retention Tool (`LTRTool`) and an
Azure Automation PowerShell 7.2 runbook on an existing environment. It does not
create or modify a Cosmos DB account, Storage account, Virtual Network, private
endpoint, Automation Account, or Hybrid Runbook Worker. Restore is not included.

The package supports:

- Azure Cosmos DB for NoSQL.
- Azure Cosmos DB for Gremlin.
- A monthly full export to an existing Blob container.
- GZIP JSON Lines data files.
- Account, database, container, and graph metadata.
- SHA-256 checksums, item counts, sampled query validation, and a manifest.
- A `STAGING` to `COMPLETED` publication sequence.
- Read-only inventory of completed monthly Recovery Sets.
- 13-month completed-set retention by default.
- Optional read-access geo-redundant (`RA-GRS`/`RA-GZRS`) Region B validation.

NoSQL Change Feed is used only to seal a full export. The current sealing mode is
Latest Version and does not capture delete events.

## 2. Package contents

The generated ZIP contains:

| Path | Purpose |
| --- | --- |
| `tool/source/Common/` | Shared `Common` configuration and cloud-environment source. |
| `tool/source/LTRTool/` | Modular C# source for `LTRTool`. |
| `automation/Export-CosmosLTR.ps1` | Thin PowerShell 7.2 Runbook entry point. |
| `automation/CosmosLtrRunbook/` | Embedded wrapper module for process launch and structured logs. |
| `automation/Publish-CosmosRunbooks.ps1` | Generates, validates, publishes, and verifies the standalone Runbook. |
| `automation/Start-CosmosBackup.ps1` | Starts a parameterized Runbook job on a Hybrid Worker. |
| `config/` | NoSQL and Gremlin configuration examples. |
| `rbac/Grant-CosmosLTRPermissions.ps1` | Idempotent data-plane, metadata-read, and Storage role setup. |
| `worker/Install-CosmosLTRTool.sh` | Installs a prepared publish output and configuration on Linux. |
| `package.json` | Build metadata. |
| `SHA256SUMS.txt` | Hash manifest for every delivered file. |

## 3. Existing resources required

Provide or confirm:

1. One existing Azure Cosmos DB for NoSQL account, one Gremlin account, or both.
2. The existing database names and container/graph names to export.
3. An existing private Blob container for Recovery Sets.
4. An existing Azure Automation Account that supports PowerShell 7.2 Runbooks.
5. An extension-based Hybrid Runbook Worker V2 group on a Linux VM.
6. A user-assigned managed identity attached to that VM, or a VM system-assigned identity.
7. Private network connectivity and DNS to Cosmos DB and Blob endpoints when public access is disabled.

The Runbook does not execute Cosmos DB or Storage SDK code in the Automation cloud
sandbox. All SDK work runs on the Hybrid Worker.

## 4. Runtime prerequisites

Install on each Hybrid Worker:

- .NET 10 host/runtime.
- PowerShell 7.2.
- Sufficient free disk space for the staging export plus the uploaded files.
- Outbound HTTPS to Azure Resource Manager.
- DNS and TCP 443 to:
  - the Cosmos NoSQL document endpoint, when using NoSQL;
  - the Cosmos Gremlin endpoint, when using Gremlin;
  - the primary Blob endpoint;
  - the secondary Blob endpoint only when Region B validation is required.

Azure CLI and the Az PowerShell modules are **not required on the worker**. They are
required only on the administrator/operator workstation that builds, publishes, or
starts the Runbook.

Runtime requirement matrix:

| Component | Hybrid Worker VM | Administrator/operator workstation |
| --- | --- | --- |
| PowerShell 7.2 | Required for the Automation PowerShell 7.2 Runbook wrapper. | PowerShell 7.2+ required. |
| .NET 10 host/runtime | Required to execute `LTRTool.dll`. | .NET 10 SDK required only when compiling source. |
| Azure CLI (`az`) | **Not required**. `LTRTool` and the Runbook use Azure SDKs with managed identity. | Required only for `Grant-CosmosLTRPermissions.ps1`. |
| Az PowerShell modules (`Az.Accounts`, `Az.Automation`) | **Not required**. | Required by `Publish-CosmosRunbooks.ps1` and `Start-CosmosBackup.ps1`. |

Validate a Linux Hybrid Worker before publishing:

```bash
pwsh -NoLogo -NoProfile -Command '$PSVersionTable.PSVersion'
/path/to/dotnet --version
grep '^powershell_7_2_path=' /etc/environment
command -v az || true
systemctl is-active hwd
```

Expected:

- PowerShell reports `7.2.x`.
- `/path/to/dotnet --version` reports a .NET 10 SDK/runtime version, for example
  `10.0.404`.
- `/etc/environment` contains
  `powershell_7_2_path="/usr/bin/pwsh"` when `/usr/bin/pwsh` is the worker
  PowerShell 7.2 executable.
- `az` may be absent; that is acceptable on the worker.
- `hwd` is `active` when the extension-based Hybrid Worker is healthy.

For Automation Hybrid Worker runtime discovery, set the following Automation
environment/runtime paths as appropriate for the worker:

- PowerShell: `/usr/bin/pwsh`
- .NET: for example `/opt/dotnet/dotnet` or `/usr/bin/dotnet`

Do not install Azure CLI on the Hybrid Worker solely for `LTRTool` operation.
Installing it introduces another credential/client surface without a runtime
requirement in this implementation.

Pass the actual executable paths through `DotNetPath`, `ApplicationPath`, and
`ConfigurationPath` when starting the job. The tool's default paths match the
reference lab and should not be assumed on a new worker.

## 5. Identity and permission matrix

### 5.1 Hybrid Worker identity

Recommended: attach a dedicated user-assigned managed identity to the VM and pass
its client ID as `ManagedIdentityClientId`.

The identity requires:

| Target | Required permission | Reason |
| --- | --- | --- |
| NoSQL database | `Cosmos DB Built-in Data Reader` | Read items and initialize NoSQL metadata. |
| NoSQL account | Custom Cosmos LTR NoSQL metadata reader | Read account/database/container and throughput metadata through ARM. |
| Gremlin account | Custom Gremlin key reader (`databaseAccounts/listKeys/action`) | Gremlin API has no native Microsoft Entra data-plane authentication. |
| Gremlin account | Custom Cosmos LTR Gremlin metadata reader | Read account/database/graph and throughput metadata through ARM. |
| Backup Blob container | `Storage Blob Data Contributor` | Upload, verify, mark complete, reuse, and delete expired Recovery Sets. |

The custom metadata roles use `read` operations only. They must not be replaced with
Cosmos data-writer roles for this use case.

Run:

```powershell
.\rbac\Grant-CosmosLTRPermissions.ps1 `
  -SubscriptionId '<subscription-id>' `
  -ResourceGroupName '<resource-group>' `
  -PrincipalId '<managed-identity-principal-object-id>' `
  -Api NoSql `
  -CosmosAccountName '<cosmos-account>' `
  -DatabaseName '<database>' `
  -StorageAccountName '<storage-account>' `
  -StorageContainerName '<backup-container>'
```

For Gremlin:

```powershell
.\rbac\Grant-CosmosLTRPermissions.ps1 `
  -SubscriptionId '<subscription-id>' `
  -ResourceGroupName '<resource-group>' `
  -PrincipalId '<managed-identity-principal-object-id>' `
  -Api Gremlin `
  -CosmosAccountName '<gremlin-account>' `
  -StorageAccountName '<storage-account>' `
  -StorageContainerName '<backup-container>'
```

Use the managed identity's **principal/object ID**, not its client ID, for RBAC.
The client ID is used by `LTRTool` at runtime.

### 5.2 Automation Account

The Automation Account needs no Cosmos DB or Storage data-plane role for this
implementation. The Hybrid Worker process uses the VM identity.

Human permissions for publication and job operation:

| Action | Permission |
| --- | --- |
| Publish/edit Runbook and schedule | Automation Contributor, or equivalent write permissions. |
| Start/read jobs only | Automation Job Operator. |
| Discover resources in the resource group | Resource Group Reader. |
| Create custom RBAC roles/assignments | Owner, User Access Administrator, or equivalent role-assignment and role-definition permissions. |

If a dedicated Automation Account identity is deliberately used instead of a VM
identity, assign the same data-plane/storage permissions to that identity and test
from a Hybrid Worker. The reference workflow uses the VM identity and leaves the
Automation Account without a managed identity.

## 6. Create the source-only delivery

From the repository:

```powershell
.\scripts\cosmos\delivery\Build-CosmosLTRDelivery.ps1 `
  -OutputDirectory .\dist\CosmosLTRDelivery `
  -CreateZip `
  -Force
```

This command does not compile or include any binaries. The delivery process:

1. Copies `LTRTool` and `Common` source.
2. Copies Automation Runbook source and publication/start scripts.
3. Copies configuration examples, worker installer, RBAC script, and this guide.
4. Produces `SHA256SUMS.txt`.

Review the hashes before transfer:

```powershell
Get-Content .\dist\CosmosLTRDelivery\SHA256SUMS.txt
```

The delivered `Build-LTRTool.ps1` compiles the source in the recipient environment.
This can be run on a controlled workstation or build agent:

```powershell
.\Build-LTRTool.ps1 `
  -OutputDirectory .\tool\publish `
  -RuntimeIdentifier linux-x64 `
  -SelfContained:$false
```

For a Windows Hybrid Worker, use `-RuntimeIdentifier win-x64`.

## 7. Configure LTRTool

Copy one example from `config/` and replace all placeholder values. The shared
`Common` model remains the configuration implementation for this package.

NoSQL minimum:

```json
{
  "cloudName": "AzureChinaCloud",
  "noSql": {
    "endpoint": "https://account.documents.azure.cn/",
    "accountResourceId": "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.DocumentDB/databaseAccounts/account",
    "database": "database",
    "containers": ["container-a", "container-b"]
  },
  "storage": {
    "blobEndpoint": "https://storage.blob.core.chinacloudapi.cn/",
    "containers": ["ltr-backups"],
    "replicaBlobEndpoint": null
  },
  "backup": {
    "managedIdentityClientId": "00000000-0000-0000-0000-000000000000",
    "retentionMonths": 13,
    "noSqlContainer": "ltr-backups"
  }
}
```

Gremlin uses `gremlin.accountResourceId`, `gremlin.hostname`, `database`, and
`graphs`. Use `config.gremlin.example.json`.

The examples default to `AzureChinaCloud`. Use `AzureCloud` only when the target
environment is in global Azure, and replace all endpoint suffixes accordingly.

Set `replicaBlobEndpoint` only when the Storage account supports read access to
secondary (`RAGRS` or `RAGZRS`) and the worker can resolve/access the secondary
endpoint through private DNS/private endpoint. Set `ReplicaRequired` to `false`
for LRS, ZRS, or GRS where the secondary read endpoint is not available.

Do not put keys, Storage SAS tokens, or connection strings in the configuration.
The tool uses Microsoft Entra managed identity. Gremlin obtains the account key
through ARM `listKeys` at runtime and keeps it in process memory.

## 8. Install on the Linux Hybrid Worker

Compile the source in the recipient environment first. Then transfer the approved
source package and resulting publish output through an internal artifact mechanism.
Extract the package on the worker or transfer host, then:

```bash
cd <extracted-package>
chmod +x ./worker/Install-CosmosLTRTool.sh
sudo DOTNET_PATH=/usr/bin/dotnet \
  ./worker/Install-CosmosLTRTool.sh \
  ./tool/publish \
  ./config/config.json \
  /opt/cosmos-ltr
```

The installer prints the three paths to use in Automation:

- `DotNetPath`
- `ApplicationPath`
- `ConfigurationPath`

Verify:

```bash
/usr/bin/dotnet \
  /opt/cosmos-ltr/publish/LTRTool/LTRTool.dll \
  --help
```

## 9. Validate the published Runbook locally

On an administrator workstation with PowerShell 7.2 and the Az PowerShell modules:

```powershell
az cloud set --name AzureChinaCloud
Connect-AzAccount -Environment AzureChinaCloud
.\automation\Publish-CosmosRunbooks.ps1 -Mode Validate
```

For Azure global, replace `AzureChinaCloud` with `AzureCloud` in both commands.

To create a portal-uploadable file:

```powershell
.\automation\Publish-CosmosRunbooks.ps1 `
  -Mode Generate `
  -OutputDirectory .\portal
```

The generated `Export-CosmosLTR.standalone.ps1` embeds the wrapper module and can
be uploaded directly to an existing Automation Account.

## 10. Publish to Azure Automation

```powershell
.\automation\Publish-CosmosRunbooks.ps1 `
  -Mode Publish `
  -SubscriptionId '<subscription-id>' `
  -ResourceGroupName '<automation-resource-group>' `
  -AutomationAccountName '<automation-account>' `
  -Cloud AzureChinaCloud `
  -LinkMonthlySchedule:$false
```

This:

- creates or updates `Export-CosmosLTR`;
- publishes it as a PowerShell 7.2 Runbook;
- verifies the uploaded content;
- starts no backup job.

To link an existing Automation Schedule, explicitly set:

```powershell
-LinkMonthlySchedule $true `
-ScheduleName '<existing-schedule-name>' `
-HybridWorkerGroupName '<existing-hybrid-worker-group>'
```

The delivery package does not delete any existing Runbook or Schedule. Add or link
new resources explicitly through reviewed change control.

## 11. Start and validate a backup

Example for a NoSQL-only deployment:

```powershell
.\automation\Start-CosmosBackup.ps1 `
  -SubscriptionId '<automation-subscription-id>' `
  -ResourceGroupName '<automation-resource-group>' `
  -AutomationAccountName '<automation-account>' `
  -HybridWorkerGroupName '<worker-group>' `
  -Cloud AzureChinaCloud `
  -Api NoSql `
  -SourceSubscriptionId '<cosmos-subscription-id>' `
  -CosmosResourceGroupName '<cosmos-resource-group>' `
  -AccountName '<cosmos-account>' `
  -DatabaseName '<database>' `
  -NoSqlContainers 'container-a,container-b' `
  -StorageBlobEndpoint 'https://storage.blob.core.chinacloudapi.cn/' `
  -StorageContainerName 'ltr-backups' `
  -ManagedIdentityClientId '<uami-client-id>' `
  -ConfigurationPath '/opt/cosmos-ltr/config.json' `
  -DotNetPath '/usr/bin/dotnet' `
  -ApplicationPath '/opt/cosmos-ltr/publish/LTRTool/LTRTool.dll' `
  -ReplicaRequired $false `
  -Wait `
  -Confirm:$false
```

For Gremlin, use `-Api Gremlin` and `-GremlinGraphs 'graph-a,graph-b'`.

The first successful Recovery Set uses:

```text
cosmos-ltr/<account>/<yyyy-mm>/
  manifest.json
  checksums.json
  metadata/
  data/
  changefeed/
  status/COMPLETED
```

Repeat execution in the same month verifies and reuses the existing `COMPLETED`
Recovery Set. It does not overwrite it.

## 12. Verify Recovery Sets

Read-only inventory on the Hybrid Worker:

```bash
/usr/bin/dotnet \
  /opt/cosmos-ltr/publish/LTRTool/LTRTool.dll \
  inventory \
  --config /opt/cosmos-ltr/config.json \
  --api NoSql
```

Run a separate command for Gremlin to keep Automation/Run Command output below
size limits:

```bash
/usr/bin/dotnet \
  /opt/cosmos-ltr/publish/LTRTool/LTRTool.dll \
  inventory \
  --config /opt/cosmos-ltr/config.json \
  --api Gremlin
```

Inventory verifies primary-region manifest, checksums, byte sizes, and SHA-256. It
also reports Region B as `Verified`, `Unavailable`, or `NotConfigured`.

## 13. Scheduling

Create a monthly Automation Schedule in the existing Automation Account and link it
to `Export-CosmosLTR` with `RunOn=<hybrid-worker-group>`.

Pass the same Options/worker paths used for the manual validation. Schedule
parameters should include:

- API and selected containers/graphs.
- Storage account endpoint/container.
- Managed identity client ID.
- `ReplicaRequired`.
- `ConfigurationPath`, `DotNetPath`, and `ApplicationPath`.
- RU limit, parallelism, timeout, and retention.

Recommended initial RU limits are conservative. Increase only after confirming the
source production workload is not affected.

## 14. 429 and throttling behavior

NoSQL export:

- Uses low-priority queries.
- Limits Feed Range parallelism.
- Applies a token bucket keyed to consumed request units.
- Honors the Cosmos SDK 429 retry policy and SDK `Retry-After` handling.

Gremlin export:

- Reads request charge from status attributes.
- Applies the same token bucket.
- Retries transient/429 responses with `Retry-After` when valid and exponential
  backoff otherwise.

ARM metadata reads:

- Retry 429 and transient 5xx responses.
- Honor `Retry-After` when present.

## 15. Operational controls

- Do not enable public network access solely for LTRTool. Use existing private
  endpoints and private DNS.
- Keep shared-key access disabled on Storage. Use Microsoft Entra managed identity.
- Keep the backup container private.
- Use `Storage Blob Data Reader` for operators who need download/read-only access.
- Use a dedicated managed identity; do not reuse an identity with broad Contributor
  or Owner permissions.
- Monitor Automation job failures and Recovery Set `COMPLETED` markers.
- Treat `COMPLETED` plus successful primary validation as the minimum local validity
  condition. Require Region B `Verified` when the recovery design depends on it.

## 16. Troubleshooting

| Symptom | Check |
| --- | --- |
| Job fails before writing logs | Confirm PowerShell 7.2 runtime and Hybrid Worker registration. |
| `dotnet host was not found` | Pass the worker's real `DotNetPath`. |
| `LTRTool application was not found` | Pass the real `ApplicationPath` and verify the file exists. |
| 403 from Cosmos `offers` or throughput reads | Assign the custom metadata-reader role to the Hybrid Worker identity. |
| 403 from Blob | Assign `Storage Blob Data Contributor` at the backup container scope. |
| Gremlin 401/403 | Verify `listKeys/action` on the Gremlin account and private Gremlin DNS. |
| Secondary DNS failure | Set `ReplicaRequired=false`, or repair the RAGRS `blob_secondary` private endpoint/DNS. |
| 429s or slow export | Lower `MaxRuPerSecond` and `Parallelism`; confirm `Retry-After` is not missing. |
| Same-month rerun does not create a new set | Expected: the existing completed set is revalidated and reused. |

## 17. Not included

- Provisioning Cosmos DB, Storage, Automation, VM, VNet, or private endpoints.
- Restore from Recovery Sets.
- Continuous incremental backup.
- NoSQL All Versions and Deletes Change Feed.
- Rehydration of Archive-tier data.
- Recovery of networking, RBAC, or VM state.

Cosmos DB native continuous backup/PITR remains a separate recovery mechanism and
is not replaced by this logical Recovery Set format.
