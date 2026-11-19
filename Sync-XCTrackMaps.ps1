#requires -Version 5.1

<#
.SYNOPSIS
    Bidirectionally synchronizes XCTrack road maps and terrain files between
    two Android MTP devices connected to Windows.

.DESCRIPTION
    The script discovers portable devices through the Windows Shell MTP
    interface, locates the XCTrack Map directory on each device, compares the
    selected folders, and copies missing files to the other device.

    If the same relative path exists with a different size, the file with the
    newer modification time wins. The older file is backed up locally before
    replacement. Files are never deleted.

    Transfers are staged through a local Windows directory because direct
    MTP-to-MTP copies are unreliable on some Android devices.
#>

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [Parameter()]
    [string] $DeviceA,

    [Parameter()]
    [string] $DeviceB,

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string[]] $Folders = @('RoadMap', 'gec'),

    [Parameter()]
    [ValidateSet('NewerWins', 'SkipConflicts')]
    [string] $ConflictPolicy = 'NewerWins',

    [Parameter()]
    [ValidateRange(0, 86400)]
    [int] $ClockSkewSeconds = 120,

    [Parameter()]
    [ValidateRange(30, 86400)]
    [int] $TransferTimeoutSeconds = 7200,

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string] $WorkRoot = (Join-Path $env:USERPROFILE 'Documents\XCTrackMapSync'),

    [Parameter()]
    [switch] $ListDevices,

    [Parameter()]
    [switch] $KeepStaging,

    [Parameter()]
    [switch] $GuiStatusJson,

    [Parameter()]
    [switch] $GuiCompareJson,

    [Parameter()]
    [switch] $GuiProgress
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$script:Shell = New-Object -ComObject Shell.Application
$script:CopyFlags = 532 # silent, no confirmation, no mkdir confirmation
$script:Audit = New-Object System.Collections.Generic.List[object]
$script:FailureCount = 0

function Write-Status {
    param([string] $Message)
    Write-Host ('[{0:HH:mm:ss}] {1}' -f (Get-Date), $Message)
}

function ConvertTo-SafeName {
    param([string] $Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return 'device'
    }

    $invalid = [IO.Path]::GetInvalidFileNameChars()
    $result = $Value
    foreach ($character in $invalid) {
        $result = $result.Replace([string] $character, '_')
    }
    return $result.Trim()
}

function Get-ChildMtpItem {
    param(
        [Parameter(Mandatory = $true)] $ParentItem,
        [Parameter(Mandatory = $true)] [string] $Name,
        [switch] $FolderOnly
    )

    if ($null -eq $ParentItem) {
        return $null
    }

    $items = @($ParentItem.GetFolder.Items())
    foreach ($item in $items) {
        if ($item.Name -ieq $Name) {
            if (-not $FolderOnly -or $item.IsFolder) {
                return $item
            }
        }
    }
    return $null
}

function Resolve-ChildPath {
    param(
        [Parameter(Mandatory = $true)] $RootItem,
        [Parameter(Mandatory = $true)] [string[]] $Parts
    )

    $current = $RootItem
    foreach ($part in $Parts) {
        $current = Get-ChildMtpItem -ParentItem $current -Name $part -FolderOnly
        if ($null -eq $current) {
            return $null
        }
    }
    return $current
}

function Resolve-XCTrackDevice {
    param([Parameter(Mandatory = $true)] $DeviceItem)

    $storageItems = @()
    try {
        $storageItems = @($DeviceItem.GetFolder.Items() | Where-Object { $_.IsFolder })
    }
    catch {
        return $null
    }

    $layouts = @(
        [pscustomobject]@{
            Name  = 'Android-data'
            Parts = @('Android', 'data', 'org.xcontest.XCTrack', 'files', 'Map')
        },
        [pscustomobject]@{
            Name  = 'Legacy-XCTrack'
            Parts = @('XCTrack', 'Map')
        }
    )

    foreach ($storage in $storageItems) {
        foreach ($layout in $layouts) {
            $map = Resolve-ChildPath -RootItem $storage -Parts $layout.Parts
            if ($null -ne $map) {
                $path = ''
                try { $path = [string] $DeviceItem.Path } catch { $path = '' }

                return [pscustomobject]@{
                    Name        = [string] $DeviceItem.Name
                    DisplayName = [string] $DeviceItem.Name
                    Path        = $path
                    Layout      = $layout.Name
                    DeviceItem  = $DeviceItem
                    StorageItem = $storage
                    MapItem     = $map
                }
            }
        }
    }

    return $null
}

function Get-XCTrackDevices {
    $thisComputer = $script:Shell.Namespace(17)
    if ($null -eq $thisComputer) {
        throw 'Windows Shell This PC namespace is unavailable.'
    }

    $devices = New-Object System.Collections.Generic.List[object]
    foreach ($item in @($thisComputer.Items())) {
        $resolved = Resolve-XCTrackDevice -DeviceItem $item
        if ($null -ne $resolved) {
            $devices.Add($resolved)
        }
    }

    foreach ($group in @($devices | Group-Object -Property Name)) {
        if ($group.Count -gt 1) {
            $index = 1
            foreach ($device in $group.Group) {
                $device.DisplayName = '{0} #{1}' -f $device.Name, $index
                $index++
            }
        }
    }

    return $devices.ToArray()
}

function Select-XCTrackDevice {
    param(
        [Parameter(Mandatory = $true)] [object[]] $Devices,
        [Parameter(Mandatory = $true)] [string] $Selector
    )

    $matches = @($Devices | Where-Object {
        $_.DisplayName -ieq $Selector -or
        $_.Name -ieq $Selector -or
        (-not [string]::IsNullOrWhiteSpace($_.Path) -and $_.Path -ieq $Selector)
    })

    if ($matches.Count -eq 0) {
        throw "XCTrack device '$Selector' was not found. Use -ListDevices."
    }
    if ($matches.Count -gt 1) {
        $choices = ($matches | ForEach-Object { $_.DisplayName }) -join ', '
        throw "Device selector '$Selector' is ambiguous. Use one of: $choices"
    }
    return $matches[0]
}

function ConvertTo-DateTime {
    param($Value)

    if ($null -eq $Value) {
        return [datetime]::MinValue
    }
    if ($Value -is [datetime]) {
        return ([datetime] $Value).ToUniversalTime()
    }

    $parsed = [datetime]::MinValue
    if ([datetime]::TryParse([string] $Value, [ref] $parsed)) {
        return $parsed.ToUniversalTime()
    }
    return [datetime]::MinValue
}

function Get-MtpItemSize {
    param([Parameter(Mandatory = $true)] $Item)

    $value = $Item.ExtendedProperty('System.Size')
    if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string] $value)) {
        return [int64] 0
    }
    return [int64] $value
}

function Get-MtpItemModifiedUtc {
    param([Parameter(Mandatory = $true)] $Item)

    $value = $Item.ExtendedProperty('System.DateModified')
    if ($null -eq $value) {
        try { $value = $Item.ModifyDate } catch { $value = $null }
    }
    return ConvertTo-DateTime $value
}

function Add-MtpInventoryRecursive {
    param(
        [Parameter(Mandatory = $true)] $FolderItem,
        [Parameter(Mandatory = $true)] [string] $RelativeFolder,
        [Parameter(Mandatory = $true)] [hashtable] $Inventory
    )

    foreach ($item in @($FolderItem.GetFolder.Items())) {
        $relativePath = if ([string]::IsNullOrWhiteSpace($RelativeFolder)) {
            [string] $item.Name
        }
        else {
            $RelativeFolder + '\' + [string] $item.Name
        }

        if ($item.IsFolder) {
            Add-MtpInventoryRecursive -FolderItem $item -RelativeFolder $relativePath -Inventory $Inventory
        }
        else {
            $Inventory[$relativePath] = [pscustomobject]@{
                RelativePath   = $relativePath
                ParentPath     = $RelativeFolder
                Name           = [string] $item.Name
                Size           = Get-MtpItemSize -Item $item
                ModifiedUtc    = Get-MtpItemModifiedUtc -Item $item
                Item           = $item
                ParentFolder   = $FolderItem
            }
        }
    }
}

function Get-DeviceInventory {
    param(
        [Parameter(Mandatory = $true)] $Device,
        [Parameter(Mandatory = $true)] [string[]] $SelectedFolders
    )

    $inventory = @{}
    foreach ($folderName in $SelectedFolders) {
        $folder = Get-ChildMtpItem -ParentItem $Device.MapItem -Name $folderName -FolderOnly
        if ($null -eq $folder) {
            Write-Warning ("{0}: folder '{1}' does not exist and is treated as empty." -f $Device.DisplayName, $folderName)
            continue
        }
        Add-MtpInventoryRecursive -FolderItem $folder -RelativeFolder $folderName -Inventory $inventory
    }
    return $inventory
}

function Get-SyncPlan {
    param(
        [Parameter(Mandatory = $true)] $FirstDevice,
        [Parameter(Mandatory = $true)] [hashtable] $FirstInventory,
        [Parameter(Mandatory = $true)] $SecondDevice,
        [Parameter(Mandatory = $true)] [hashtable] $SecondInventory,
        [Parameter(Mandatory = $true)] [string] $Policy,
        [Parameter(Mandatory = $true)] [int] $AllowedClockSkewSeconds
    )

    $allPaths = @(@($FirstInventory.Keys) + @($SecondInventory.Keys) | Sort-Object -Unique)
    $plan = New-Object System.Collections.Generic.List[object]
    $same = 0
    $conflicts = 0

    foreach ($relativePath in $allPaths) {
        $first = $FirstInventory[$relativePath]
        $second = $SecondInventory[$relativePath]

        if ($null -eq $second) {
            $plan.Add([pscustomobject]@{
                RelativePath      = $relativePath
                SourceDevice      = $FirstDevice
                DestinationDevice = $SecondDevice
                SourceRecord      = $first
                DestinationRecord = $null
                Action            = 'CopyMissing'
                Reason            = 'Missing on destination'
            })
            continue
        }

        if ($null -eq $first) {
            $plan.Add([pscustomobject]@{
                RelativePath      = $relativePath
                SourceDevice      = $SecondDevice
                DestinationDevice = $FirstDevice
                SourceRecord      = $second
                DestinationRecord = $null
                Action            = 'CopyMissing'
                Reason            = 'Missing on destination'
            })
            continue
        }

        # Equal size is considered equal content. This prevents timestamp-only
        # ping-pong because many MTP implementations replace modification times
        # with the copy time.
        if ($first.Size -eq $second.Size) {
            $same++
            continue
        }

        if ($Policy -eq 'SkipConflicts') {
            $conflicts++
            $script:Audit.Add([pscustomobject]@{
                Timestamp         = (Get-Date).ToString('s')
                Action            = 'ConflictSkipped'
                RelativePath      = $relativePath
                SourceDevice      = $FirstDevice.DisplayName
                DestinationDevice = $SecondDevice.DisplayName
                Size              = ''
                Status            = 'Skipped'
                Details           = 'Different sizes; ConflictPolicy=SkipConflicts'
            })
            continue
        }

        $difference = ($first.ModifiedUtc - $second.ModifiedUtc).TotalSeconds
        if ($first.ModifiedUtc -eq [datetime]::MinValue -or
            $second.ModifiedUtc -eq [datetime]::MinValue -or
            [math]::Abs($difference) -le $AllowedClockSkewSeconds) {
            $conflicts++
            $script:Audit.Add([pscustomobject]@{
                Timestamp         = (Get-Date).ToString('s')
                Action            = 'ConflictSkipped'
                RelativePath      = $relativePath
                SourceDevice      = $FirstDevice.DisplayName
                DestinationDevice = $SecondDevice.DisplayName
                Size              = ''
                Status            = 'Skipped'
                Details           = 'Different sizes, but modification times are missing or too close'
            })
            continue
        }

        if ($difference -gt 0) {
            $sourceDevice = $FirstDevice
            $destinationDevice = $SecondDevice
            $sourceRecord = $first
            $destinationRecord = $second
        }
        else {
            $sourceDevice = $SecondDevice
            $destinationDevice = $FirstDevice
            $sourceRecord = $second
            $destinationRecord = $first
        }

        $plan.Add([pscustomobject]@{
            RelativePath      = $relativePath
            SourceDevice      = $sourceDevice
            DestinationDevice = $destinationDevice
            SourceRecord      = $sourceRecord
            DestinationRecord = $destinationRecord
            Action            = 'ReplaceOlder'
            Reason            = 'Different size; source has newer modification time'
        })
    }

    return [pscustomobject]@{
        Items     = $plan.ToArray()
        SameCount = $same
        Conflicts = $conflicts
    }
}

function Wait-LocalFile {
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [int64] $ExpectedSize,
        [Parameter(Mandatory = $true)] [int] $TimeoutSeconds
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        if (Test-Path -LiteralPath $Path) {
            $length = (Get-Item -LiteralPath $Path).Length
            if ($length -eq $ExpectedSize) {
                return
            }
        }
        Start-Sleep -Milliseconds 750
    } while ((Get-Date) -lt $deadline)

    $actual = if (Test-Path -LiteralPath $Path) {
        (Get-Item -LiteralPath $Path).Length
    }
    else {
        0
    }
    throw "Local copy timeout: '$Path' actual=$actual expected=$ExpectedSize"
}

function Wait-MtpFile {
    param(
        [Parameter(Mandatory = $true)] $FolderItem,
        [Parameter(Mandatory = $true)] [string] $Name,
        [Parameter(Mandatory = $true)] [int64] $ExpectedSize,
        [Parameter(Mandatory = $true)] [int] $TimeoutSeconds
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $candidate = Get-ChildMtpItem -ParentItem $FolderItem -Name $Name
        if ($null -ne $candidate -and -not $candidate.IsFolder) {
            $actual = Get-MtpItemSize -Item $candidate
            if ($actual -eq $ExpectedSize) {
                return $candidate
            }
        }
        Start-Sleep -Milliseconds 1000
    } while ((Get-Date) -lt $deadline)

    throw "MTP copy timeout: '$Name' did not reach expected size $ExpectedSize."
}

function Get-OrCreateMtpFolderPath {
    param(
        [Parameter(Mandatory = $true)] $RootItem,
        [Parameter(Mandatory = $true)] [string] $RelativeFolder,
        [Parameter(Mandatory = $true)] [int] $TimeoutSeconds
    )

    $current = $RootItem
    $parts = @($RelativeFolder -split '\\' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    foreach ($part in $parts) {
        $child = Get-ChildMtpItem -ParentItem $current -Name $part -FolderOnly
        if ($null -eq $child) {
            $current.GetFolder.NewFolder($part, $script:CopyFlags)
            $deadline = (Get-Date).AddSeconds([math]::Min($TimeoutSeconds, 120))
            do {
                Start-Sleep -Milliseconds 500
                $child = Get-ChildMtpItem -ParentItem $current -Name $part -FolderOnly
            } while ($null -eq $child -and (Get-Date) -lt $deadline)

            if ($null -eq $child) {
                throw "Could not create MTP folder '$RelativeFolder'."
            }
        }
        $current = $child
    }
    return $current
}

function Copy-MtpItemToLocal {
    param(
        [Parameter(Mandatory = $true)] $SourceItem,
        [Parameter(Mandatory = $true)] [string] $DestinationDirectory,
        [Parameter(Mandatory = $true)] [int64] $ExpectedSize,
        [Parameter(Mandatory = $true)] [int] $TimeoutSeconds
    )

    if (-not (Test-Path -LiteralPath $DestinationDirectory)) {
        $null = New-Item -ItemType Directory -Path $DestinationDirectory -Force
    }
    $target = Join-Path $DestinationDirectory ([string] $SourceItem.Name)
    if (Test-Path -LiteralPath $target) {
        Remove-Item -LiteralPath $target -Force
    }

    $namespace = $script:Shell.Namespace($DestinationDirectory)
    if ($null -eq $namespace) {
        throw "Cannot open local Shell namespace '$DestinationDirectory'."
    }
    $namespace.CopyHere($SourceItem, $script:CopyFlags)
    Wait-LocalFile -Path $target -ExpectedSize $ExpectedSize -TimeoutSeconds $TimeoutSeconds
    return $target
}

function Copy-LocalFileToMtp {
    param(
        [Parameter(Mandatory = $true)] [string] $SourcePath,
        [Parameter(Mandatory = $true)] $DestinationFolder,
        [Parameter(Mandatory = $true)] [int64] $ExpectedSize,
        [Parameter(Mandatory = $true)] [int] $TimeoutSeconds
    )

    $DestinationFolder.GetFolder.CopyHere($SourcePath, $script:CopyFlags)
    return Wait-MtpFile -FolderItem $DestinationFolder -Name ([IO.Path]::GetFileName($SourcePath)) -ExpectedSize $ExpectedSize -TimeoutSeconds $TimeoutSeconds
}

function Add-AuditRecord {
    param(
        [Parameter(Mandatory = $true)] $PlanItem,
        [Parameter(Mandatory = $true)] [string] $Status,
        [string] $Details = ''
    )

    $script:Audit.Add([pscustomobject]@{
        Timestamp         = (Get-Date).ToString('s')
        Action            = $PlanItem.Action
        RelativePath      = $PlanItem.RelativePath
        SourceDevice      = $PlanItem.SourceDevice.DisplayName
        DestinationDevice = $PlanItem.DestinationDevice.DisplayName
        Size              = $PlanItem.SourceRecord.Size
        Status            = $Status
        Details           = $Details
    })
}

function Invoke-SyncItem {
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
    param(
        [Parameter(Mandatory = $true)] $PlanItem,
        [Parameter(Mandatory = $true)] [string] $StagingRoot,
        [Parameter(Mandatory = $true)] [string] $BackupRoot,
        [Parameter(Mandatory = $true)] [int] $TimeoutSeconds
    )

    $description = '{0}: {1} -> {2}' -f $PlanItem.RelativePath,
        $PlanItem.SourceDevice.DisplayName,
        $PlanItem.DestinationDevice.DisplayName

    if (-not $PSCmdlet.ShouldProcess($description, $PlanItem.Action)) {
        Add-AuditRecord -PlanItem $PlanItem -Status 'WhatIf' -Details $PlanItem.Reason
        return
    }

    try {
        Write-Status ("COPY {0}" -f $description)

        if ($null -ne $PlanItem.DestinationRecord) {
            $deviceBackup = ConvertTo-SafeName $PlanItem.DestinationDevice.DisplayName
            $backupDirectory = Join-Path (Join-Path $BackupRoot $deviceBackup) $PlanItem.DestinationRecord.ParentPath
            $backupFile = Copy-MtpItemToLocal `
                -SourceItem $PlanItem.DestinationRecord.Item `
                -DestinationDirectory $backupDirectory `
                -ExpectedSize $PlanItem.DestinationRecord.Size `
                -TimeoutSeconds $TimeoutSeconds
            Write-Status ("BACKUP {0}" -f $backupFile)
        }

        $stagingDirectory = Join-Path $StagingRoot ([guid]::NewGuid().ToString('N'))
        $localFile = Copy-MtpItemToLocal `
            -SourceItem $PlanItem.SourceRecord.Item `
            -DestinationDirectory $stagingDirectory `
            -ExpectedSize $PlanItem.SourceRecord.Size `
            -TimeoutSeconds $TimeoutSeconds

        $destinationFolder = Get-OrCreateMtpFolderPath `
            -RootItem $PlanItem.DestinationDevice.MapItem `
            -RelativeFolder $PlanItem.SourceRecord.ParentPath `
            -TimeoutSeconds $TimeoutSeconds

        $null = Copy-LocalFileToMtp `
            -SourcePath $localFile `
            -DestinationFolder $destinationFolder `
            -ExpectedSize $PlanItem.SourceRecord.Size `
            -TimeoutSeconds $TimeoutSeconds

        if (-not $KeepStaging) {
            Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
        }
        Add-AuditRecord -PlanItem $PlanItem -Status 'Success' -Details $PlanItem.Reason
    }
    catch {
        $script:FailureCount++
        Add-AuditRecord -PlanItem $PlanItem -Status 'Failed' -Details $_.Exception.Message
        Write-Warning ("FAILED {0}: {1}" -f $PlanItem.RelativePath, $_.Exception.Message)
    }
}

function Write-AuditFiles {
    param(
        [Parameter(Mandatory = $true)] [string] $RunRoot,
        [Parameter(Mandatory = $true)] $FirstDevice,
        [Parameter(Mandatory = $true)] $SecondDevice,
        [Parameter(Mandatory = $true)] $PlanResult
    )

    $csvPath = Join-Path $RunRoot 'audit.csv'
    $script:Audit | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding UTF8 -WhatIf:$false

    $success = @($script:Audit | Where-Object { $_.Status -eq 'Success' }).Count
    $failed = @($script:Audit | Where-Object { $_.Status -eq 'Failed' }).Count
    $skipped = @($script:Audit | Where-Object { $_.Status -eq 'Skipped' }).Count
    $whatIf = @($script:Audit | Where-Object { $_.Status -eq 'WhatIf' }).Count

    $lines = @(
        '# XCTrack map synchronization audit',
        '',
        ('- Date: {0}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss K')),
        ('- Device A: {0}' -f $FirstDevice.DisplayName),
        ('- Device B: {0}' -f $SecondDevice.DisplayName),
        ('- Folders: {0}' -f ($Folders -join ', ')),
        ('- Conflict policy: {0}' -f $ConflictPolicy),
        '',
        '## Result',
        '',
        ('- Planned transfers: {0}' -f $PlanResult.Items.Count),
        ('- Already equal by path and size: {0}' -f $PlanResult.SameCount),
        ('- Successful: {0}' -f $success),
        ('- Failed: {0}' -f $failed),
        ('- Conflicts skipped: {0}' -f $skipped),
        ('- WhatIf entries: {0}' -f $whatIf),
        '',
        ('Detailed CSV: `{0}`' -f $csvPath)
    )
    $markdownPath = Join-Path $RunRoot 'audit.md'
    $lines | Set-Content -LiteralPath $markdownPath -Encoding UTF8 -WhatIf:$false

    Write-Status ("Audit: {0}" -f $markdownPath)
}

try {
    Write-Status 'Searching for connected XCTrack MTP devices...'
    $devices = @(Get-XCTrackDevices)

    if ($GuiStatusJson) {
        $statusPayload = @($devices | ForEach-Object {
            [pscustomobject]@{
                DisplayName = $_.DisplayName
                Name        = $_.Name
                Layout      = $_.Layout
                Path        = $_.Path
            }
        })
        Write-Output ('XCT_JSON:' + (ConvertTo-Json -InputObject $statusPayload -Compress))
        exit 0
    }

    if ($ListDevices) {
        if ($devices.Count -eq 0) {
            Write-Host 'No connected MTP device with an XCTrack Map directory was found.'
            exit 2
        }
        $devices |
            Select-Object @{Name = 'Selector'; Expression = { $_.DisplayName } }, Name, Layout, Path |
            Format-Table -AutoSize
        exit 0
    }

    if ($devices.Count -lt 2) {
        throw "Two XCTrack devices are required; found $($devices.Count). Unlock both devices and select USB File transfer."
    }

    if ([string]::IsNullOrWhiteSpace($DeviceA) -and [string]::IsNullOrWhiteSpace($DeviceB)) {
        if ($devices.Count -ne 2) {
            $choices = ($devices | ForEach-Object { $_.DisplayName }) -join ', '
            throw "Found $($devices.Count) devices. Specify -DeviceA and -DeviceB. Choices: $choices"
        }
        $firstDevice = $devices[0]
        $secondDevice = $devices[1]
    }
    elseif (-not [string]::IsNullOrWhiteSpace($DeviceA) -and -not [string]::IsNullOrWhiteSpace($DeviceB)) {
        $firstDevice = Select-XCTrackDevice -Devices $devices -Selector $DeviceA
        $secondDevice = Select-XCTrackDevice -Devices $devices -Selector $DeviceB
        if ($firstDevice.Path -eq $secondDevice.Path -and $firstDevice.DisplayName -eq $secondDevice.DisplayName) {
            throw 'DeviceA and DeviceB resolve to the same device.'
        }
    }
    else {
        throw 'Specify both -DeviceA and -DeviceB, or neither for automatic selection.'
    }

    Write-Status ("Device A: {0} ({1})" -f $firstDevice.DisplayName, $firstDevice.Layout)
    Write-Status ("Device B: {0} ({1})" -f $secondDevice.DisplayName, $secondDevice.Layout)
    Write-Status ("Reading inventory: {0}" -f ($Folders -join ', '))

    $firstInventory = Get-DeviceInventory -Device $firstDevice -SelectedFolders $Folders
    $secondInventory = Get-DeviceInventory -Device $secondDevice -SelectedFolders $Folders

    Write-Status ("Inventory A: {0} files" -f $firstInventory.Count)
    Write-Status ("Inventory B: {0} files" -f $secondInventory.Count)

    $planResult = Get-SyncPlan `
        -FirstDevice $firstDevice `
        -FirstInventory $firstInventory `
        -SecondDevice $secondDevice `
        -SecondInventory $secondInventory `
        -Policy $ConflictPolicy `
        -AllowedClockSkewSeconds $ClockSkewSeconds

    Write-Status ("Plan: {0} transfers, {1} equal, {2} unresolved conflicts" -f `
        $planResult.Items.Count, $planResult.SameCount, $planResult.Conflicts)

    if ($GuiCompareJson) {
        $toFirst = @($planResult.Items | Where-Object {
            $_.DestinationDevice.DisplayName -eq $firstDevice.DisplayName
        })
        $toSecond = @($planResult.Items | Where-Object {
            $_.DestinationDevice.DisplayName -eq $secondDevice.DisplayName
        })

        $sumFirst = ($toFirst | ForEach-Object { $_.SourceRecord.Size } | Measure-Object -Sum).Sum
        $sumSecond = ($toSecond | ForEach-Object { $_.SourceRecord.Size } | Measure-Object -Sum).Sum
        if ($null -eq $sumFirst) { $sumFirst = 0 }
        if ($null -eq $sumSecond) { $sumSecond = 0 }

        $comparePayload = [pscustomobject]@{
            DeviceA = [pscustomobject]@{
                Name            = $firstDevice.DisplayName
                Layout          = $firstDevice.Layout
                FileCount       = $firstInventory.Count
                RoadMapCount    = @($firstInventory.Keys | Where-Object { $_ -like 'RoadMap\*' }).Count
                GecCount        = @($firstInventory.Keys | Where-Object { $_ -like 'gec\*' }).Count
                MissingIncoming = @($toFirst | Where-Object { $_.Action -eq 'CopyMissing' }).Count
                NewerIncoming   = @($toFirst | Where-Object { $_.Action -eq 'ReplaceOlder' }).Count
                BytesIncoming   = [int64] $sumFirst
            }
            DeviceB = [pscustomobject]@{
                Name            = $secondDevice.DisplayName
                Layout          = $secondDevice.Layout
                FileCount       = $secondInventory.Count
                RoadMapCount    = @($secondInventory.Keys | Where-Object { $_ -like 'RoadMap\*' }).Count
                GecCount        = @($secondInventory.Keys | Where-Object { $_ -like 'gec\*' }).Count
                MissingIncoming = @($toSecond | Where-Object { $_.Action -eq 'CopyMissing' }).Count
                NewerIncoming   = @($toSecond | Where-Object { $_.Action -eq 'ReplaceOlder' }).Count
                BytesIncoming   = [int64] $sumSecond
            }
            Transfers  = $planResult.Items.Count
            Same       = $planResult.SameCount
            Conflicts  = $planResult.Conflicts
            TotalBytes = [int64] ($sumFirst + $sumSecond)
        }
        Write-Output ('XCT_JSON:' + ($comparePayload | ConvertTo-Json -Depth 4 -Compress))
        exit 0
    }

    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $runRoot = Join-Path $WorkRoot $timestamp
    $stagingRoot = Join-Path $runRoot 'staging'
    $backupRoot = Join-Path $runRoot 'backup'
    # Keep a local plan/audit even in -WhatIf mode. Device writes remain
    # protected by ShouldProcess in Invoke-SyncItem.
    $null = New-Item -ItemType Directory -Path $stagingRoot -Force -WhatIf:$false
    $null = New-Item -ItemType Directory -Path $backupRoot -Force -WhatIf:$false

    $progressIndex = 0
    foreach ($planItem in $planResult.Items) {
        $progressIndex++
        if ($GuiProgress) {
            $progressPayload = [pscustomobject]@{
                Current     = $progressIndex
                Total       = $planResult.Items.Count
                Path        = $planItem.RelativePath
                Action      = $planItem.Action
                Destination = $planItem.DestinationDevice.DisplayName
            }
            Write-Output ('XCT_PROGRESS:' + ($progressPayload | ConvertTo-Json -Compress))
        }
        Invoke-SyncItem `
            -PlanItem $planItem `
            -StagingRoot $stagingRoot `
            -BackupRoot $backupRoot `
            -TimeoutSeconds $TransferTimeoutSeconds
    }

    Write-AuditFiles `
        -RunRoot $runRoot `
        -FirstDevice $firstDevice `
        -SecondDevice $secondDevice `
        -PlanResult $planResult

    if (-not $KeepStaging -and (Test-Path -LiteralPath $stagingRoot)) {
        $remaining = @(Get-ChildItem -LiteralPath $stagingRoot -Force)
        if ($remaining.Count -eq 0) {
            Remove-Item -LiteralPath $stagingRoot -Force
        }
    }

    if ($script:FailureCount -gt 0) {
        throw "Synchronization completed with $($script:FailureCount) failed transfer(s). See the audit log."
    }

    if ($GuiProgress) {
        $donePayload = [pscustomobject]@{
            Transfers = $planResult.Items.Count
            Failures  = $script:FailureCount
            Audit     = (Join-Path $runRoot 'audit.md')
        }
        Write-Output ('XCT_DONE:' + ($donePayload | ConvertTo-Json -Compress))
    }

    Write-Status 'Synchronization completed. Restart XCTrack on both devices.'
}
finally {
    if ($null -ne $script:Shell) {
        [void] [Runtime.InteropServices.Marshal]::FinalReleaseComObject($script:Shell)
    }
}
