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
    [switch] $GuiUsbStatusJson,

    [Parameter()]
    [switch] $GuiCompareJson,

    [Parameter()]
    [switch] $GuiProgress,

    [Parameter()]
    [switch] $GuiListRestoresJson,

    [Parameter()]
    [switch] $GuiRestore,

    [Parameter()]
    [string] $RestoreDevicePath,

    [Parameter()]
    [string] $RestoreManifest
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$script:Shell = $null
$script:CopyFlags = 532 # silent, no confirmation, no mkdir confirmation
$script:Audit = New-Object System.Collections.Generic.List[object]
$script:FailureCount = 0
$script:RestoreOperations = New-Object System.Collections.Generic.List[object]
$script:RestoreManifestPath = ''
$script:RestoreRunRoot = ''
$script:RestoreManifestHeader = $null

if ($GuiRestore -and -not ('XCTrackMapSync.MtpShellOperations' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace XCTrackMapSync
{
    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem
    {
    }

    [ComImport]
    [Guid("3AD05575-8857-4850-9277-11B85BDB8E09")]
    internal class FileOperation
    {
    }

    [ComImport]
    [Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFileOperation
    {
        [PreserveSig] int Advise(IntPtr sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint flags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog(IntPtr dialog);
        [PreserveSig] int SetProperties(IntPtr properties);
        [PreserveSig] int SetOwnerWindow(uint ownerWindow);
        [PreserveSig] int ApplyPropertiesToItem(IShellItem item);
        [PreserveSig] int ApplyPropertiesToItems(IntPtr items);
        [PreserveSig] int RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr sink);
        [PreserveSig] int RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        [PreserveSig] int MoveItem(IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr sink);
        [PreserveSig] int MoveItems(IntPtr items, IShellItem destinationFolder);
        [PreserveSig] int CopyItem(IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string copyName, IntPtr sink);
        [PreserveSig] int CopyItems(IntPtr items, IShellItem destinationFolder);
        [PreserveSig] int DeleteItem(IShellItem item, IntPtr sink);
        [PreserveSig] int DeleteItems(IntPtr items);
        [PreserveSig] int NewItem(IShellItem destinationFolder, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string templateName, IntPtr sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    public static class MtpShellOperations
    {
        [DllImport("shell32.dll", PreserveSig = true)]
        private static extern int SHGetIDListFromObject(
            [MarshalAs(UnmanagedType.IUnknown)] object shellObject,
            out IntPtr itemIdList);

        [DllImport("shell32.dll", PreserveSig = true)]
        private static extern int SHCreateItemFromIDList(
            IntPtr itemIdList,
            ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr memory);

        private static IShellItem CreateShellItem(object shellObject)
        {
            IntPtr itemIdList = IntPtr.Zero;
            try
            {
                Marshal.ThrowExceptionForHR(SHGetIDListFromObject(shellObject, out itemIdList));
                Guid shellItemId = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
                IShellItem item;
                Marshal.ThrowExceptionForHR(SHCreateItemFromIDList(itemIdList, ref shellItemId, out item));
                return item;
            }
            finally
            {
                if (itemIdList != IntPtr.Zero)
                {
                    CoTaskMemFree(itemIdList);
                }
            }
        }

        public static void Delete(object shellObject)
        {
            IShellItem item = null;
            IFileOperation operation = null;
            try
            {
                item = CreateShellItem(shellObject);
                operation = (IFileOperation)new FileOperation();

                const uint FOF_SILENT = 0x0004;
                const uint FOF_NOCONFIRMATION = 0x0010;
                const uint FOF_NOERRORUI = 0x0400;
                const uint FOF_NOCOPYSECURITYATTRIBS = 0x0800;
                const uint FOFX_EARLYFAILURE = 0x00100000;
                const uint FOFX_NOCOPYHOOKS = 0x00800000;
                uint flags = FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI |
                    FOF_NOCOPYSECURITYATTRIBS | FOFX_EARLYFAILURE | FOFX_NOCOPYHOOKS;

                Marshal.ThrowExceptionForHR(operation.SetOperationFlags(flags));
                Marshal.ThrowExceptionForHR(operation.DeleteItem(item, IntPtr.Zero));
                Marshal.ThrowExceptionForHR(operation.PerformOperations());

                bool aborted;
                Marshal.ThrowExceptionForHR(operation.GetAnyOperationsAborted(out aborted));
                if (aborted)
                {
                    throw new InvalidOperationException("The MTP delete operation was aborted.");
                }
            }
            finally
            {
                if (operation != null) Marshal.FinalReleaseComObject(operation);
                if (item != null) Marshal.FinalReleaseComObject(item);
            }
        }
    }
}
'@
}

function Write-Status {
    param([string] $Message)
    Write-Host ('[{0:HH:mm:ss}] {1}' -f (Get-Date), $Message)
}

function Get-Air3UsbDevices {
    $devices = New-Object System.Collections.Generic.List[object]
    $pnpDevices = @(Get-PnpDevice -PresentOnly -Class WPD -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Status -eq 'OK' -and
            $_.FriendlyName -match '(?i)AIR\s*3'
        })

    foreach ($pnpDevice in $pnpDevices) {
        $name = [string] $pnpDevice.FriendlyName
        $devices.Add([pscustomobject]@{
            DisplayName = $name
            Name        = $name
            Layout      = 'USB-MTP'
            Path        = [string] $pnpDevice.InstanceId
            Ready       = $false
        })
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
            $topLevelFolder = @($RelativeFolder -split '\\')[0]
            $expectedExtension = if ($topLevelFolder -ieq 'RoadMap') {
                '.map'
            }
            elseif ($topLevelFolder -ieq 'gec') {
                '.gec'
            }
            else {
                ''
            }
            if (-not [string]::IsNullOrWhiteSpace($expectedExtension) -and
                [IO.Path]::GetExtension([string] $item.Name) -ine $expectedExtension) {
                continue
            }

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

function Save-RestoreManifest {
    if ([string]::IsNullOrWhiteSpace($script:RestoreManifestPath) -or
        $null -eq $script:RestoreManifestHeader) {
        return
    }

    $payload = [pscustomobject]@{
        SchemaVersion = 2
        RunId         = $script:RestoreManifestHeader.RunId
        CreatedUtc    = $script:RestoreManifestHeader.CreatedUtc
        Devices       = $script:RestoreManifestHeader.Devices
        Operations    = @($script:RestoreOperations.ToArray())
    }
    $temporaryPath = $script:RestoreManifestPath + '.tmp'
    $payload |
        ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $temporaryPath -Encoding UTF8 -WhatIf:$false
    Move-Item -LiteralPath $temporaryPath -Destination $script:RestoreManifestPath -Force -WhatIf:$false
}

function Initialize-RestoreManifest {
    param(
        [Parameter(Mandatory = $true)] [string] $RunRoot,
        [Parameter(Mandatory = $true)] $FirstDevice,
        [Parameter(Mandatory = $true)] $SecondDevice
    )

    $script:RestoreRunRoot = $RunRoot
    $script:RestoreManifestPath = Join-Path $RunRoot 'restore-manifest.json'
    $script:RestoreManifestHeader = [pscustomobject]@{
        RunId      = Split-Path -Leaf $RunRoot
        CreatedUtc = (Get-Date).ToUniversalTime().ToString('o')
        Devices    = @(
            [pscustomobject]@{
                DisplayName = $FirstDevice.DisplayName
                Name        = $FirstDevice.Name
                Path        = $FirstDevice.Path
                Layout      = $FirstDevice.Layout
            },
            [pscustomobject]@{
                DisplayName = $SecondDevice.DisplayName
                Name        = $SecondDevice.Name
                Path        = $SecondDevice.Path
                Layout      = $SecondDevice.Layout
            }
        )
    }
    Save-RestoreManifest
}

function Add-RestoreOperation {
    param(
        [Parameter(Mandatory = $true)] $PlanItem,
        [string] $BackupFile = ''
    )

    $backupRelativePath = ''
    $backupSha256 = ''
    if (-not [string]::IsNullOrWhiteSpace($BackupFile)) {
        $rootWithSeparator = $script:RestoreRunRoot.TrimEnd('\') + '\'
        if ($BackupFile.StartsWith($rootWithSeparator, [StringComparison]::OrdinalIgnoreCase)) {
            $backupRelativePath = $BackupFile.Substring($rootWithSeparator.Length)
            $backupSha256 = (Get-FileHash -LiteralPath $BackupFile -Algorithm SHA256).Hash
        }
        else {
            throw "Backup file is outside the restore run directory: '$BackupFile'."
        }
    }

    $script:RestoreOperations.Add([pscustomobject]@{
        Action                = $PlanItem.Action
        RelativePath          = $PlanItem.RelativePath
        DestinationDeviceName = $PlanItem.DestinationDevice.DisplayName
        DestinationDevicePath = $PlanItem.DestinationDevice.Path
        DestinationLayout     = $PlanItem.DestinationDevice.Layout
        ExpectedCurrentSize   = [int64] $PlanItem.SourceRecord.Size
        OriginalSize          = if ($null -eq $PlanItem.DestinationRecord) { $null } else { [int64] $PlanItem.DestinationRecord.Size }
        BackupRelativePath    = $backupRelativePath
        BackupSha256          = $backupSha256
        RecordedUtc           = (Get-Date).ToUniversalTime().ToString('o')
    })
    Save-RestoreManifest
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

        $backupFile = ''

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
        Add-RestoreOperation -PlanItem $PlanItem -BackupFile $backupFile
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

function Resolve-MtpRelativeFile {
    param(
        [Parameter(Mandatory = $true)] $Device,
        [Parameter(Mandatory = $true)] [string] $RelativePath
    )

    $parts = @($RelativePath -split '\\' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($parts.Count -lt 2) {
        throw "Invalid restore relative path: '$RelativePath'."
    }

    $fileName = $parts[-1]
    $parentParts = @($parts[0..($parts.Count - 2)])
    $parent = Resolve-ChildPath -RootItem $Device.MapItem -Parts $parentParts
    $item = if ($null -eq $parent) {
        $null
    }
    else {
        Get-ChildMtpItem -ParentItem $parent -Name $fileName
    }

    return [pscustomobject]@{
        Item       = $item
        Parent     = $parent
        FileName   = $fileName
        ParentPath = ($parentParts -join '\')
    }
}

function Wait-MtpItemAbsent {
    param(
        [Parameter(Mandatory = $true)] $ParentFolder,
        [Parameter(Mandatory = $true)] [string] $Name,
        [Parameter(Mandatory = $true)] [int] $TimeoutSeconds
    )

    $deadline = (Get-Date).AddSeconds([math]::Min($TimeoutSeconds, 120))
    do {
        if ($null -eq (Get-ChildMtpItem -ParentItem $ParentFolder -Name $Name)) {
            return
        }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)

    throw "MTP delete timeout: '$Name' is still present on the device."
}

function Remove-MtpItemSafely {
    param(
        [Parameter(Mandatory = $true)] $Item,
        [Parameter(Mandatory = $true)] $ParentFolder,
        [Parameter(Mandatory = $true)] [string] $Name,
        [Parameter(Mandatory = $true)] [int] $TimeoutSeconds
    )

    [XCTrackMapSync.MtpShellOperations]::Delete($Item)
    Wait-MtpItemAbsent `
        -ParentFolder $ParentFolder `
        -Name $Name `
        -TimeoutSeconds $TimeoutSeconds
}

function Get-RestorePointsForDevice {
    param([Parameter(Mandatory = $true)] [string] $DevicePath)

    if (-not (Test-Path -LiteralPath $WorkRoot)) {
        return @()
    }

    $points = New-Object System.Collections.Generic.List[object]
    foreach ($directory in @(Get-ChildItem -LiteralPath $WorkRoot -Directory -ErrorAction SilentlyContinue |
            Sort-Object -Property Name -Descending)) {
        $manifestPath = Join-Path $directory.FullName 'restore-manifest.json'
        if (-not (Test-Path -LiteralPath $manifestPath)) {
            continue
        }

        try {
            $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
            $operations = @($manifest.Operations | Where-Object {
                $_.DestinationDevicePath -eq $DevicePath
            })
            if ($operations.Count -eq 0) {
                continue
            }

            $deviceName = [string] $operations[0].DestinationDeviceName
            $points.Add([pscustomobject]@{
                RunId            = [string] $manifest.RunId
                CreatedUtc       = [string] $manifest.CreatedUtc
                ManifestPath     = $manifestPath
                DeviceName       = $deviceName
                RestoreBackups   = @($operations | Where-Object { $_.Action -eq 'ReplaceOlder' }).Count
                RemoveAddedFiles = @($operations | Where-Object { $_.Action -eq 'CopyMissing' }).Count
                TotalOperations  = $operations.Count
            })
        }
        catch {
            Write-Warning ("Skipping invalid restore manifest '{0}': {1}" -f $manifestPath, $_.Exception.Message)
        }
    }
    return $points.ToArray()
}

function Invoke-DeviceRestore {
    param(
        [Parameter(Mandatory = $true)] [object[]] $ConnectedDevices,
        [Parameter(Mandatory = $true)] [string] $DevicePath,
        [Parameter(Mandatory = $true)] [string] $ManifestPath,
        [Parameter(Mandatory = $true)] [int] $TimeoutSeconds
    )

    $device = @($ConnectedDevices | Where-Object { $_.Path -eq $DevicePath }) | Select-Object -First 1
    if ($null -eq $device) {
        throw 'The selected restore device is not connected or its USB identity changed.'
    }

    $resolvedRoot = [IO.Path]::GetFullPath($WorkRoot).TrimEnd('\') + '\'
    $resolvedManifest = [IO.Path]::GetFullPath($ManifestPath)
    if (-not $resolvedManifest.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedManifest) -ne 'restore-manifest.json') {
        throw 'The restore manifest must be inside the XCTrackMapSync work directory.'
    }
    if (-not (Test-Path -LiteralPath $resolvedManifest)) {
        throw "Restore manifest not found: '$resolvedManifest'."
    }

    $manifest = Get-Content -LiteralPath $resolvedManifest -Raw | ConvertFrom-Json
    [array] $operations = @($manifest.Operations | Where-Object {
        $_.DestinationDevicePath -eq $DevicePath
    })
    if ($operations.Count -eq 0) {
        throw 'The selected restore point has no operations for this device.'
    }
    [array]::Reverse($operations)

    $runRoot = Split-Path -Parent $resolvedManifest
    $restoreId = Get-Date -Format 'yyyyMMdd-HHmmss'
    $quarantineRoot = Join-Path (Join-Path $runRoot 'restore-quarantine') $restoreId
    $restoreAudit = New-Object System.Collections.Generic.List[object]
    $successCount = 0
    $skippedCount = 0
    $failureCount = 0
    $index = 0

    foreach ($operation in $operations) {
        $index++
        if ($GuiRestore) {
            $progressPayload = [pscustomobject]@{
                Current     = $index
                Total       = $operations.Count
                Path        = [string] $operation.RelativePath
                Action      = [string] $operation.Action
                Destination = $device.DisplayName
            }
            Write-Output ('XCT_RESTORE_PROGRESS:' + ($progressPayload | ConvertTo-Json -Compress))
        }

        $status = 'Failed'
        $details = ''
        $quarantineFile = ''
        $currentWasRemoved = $false
        try {
            $resolved = Resolve-MtpRelativeFile -Device $device -RelativePath ([string] $operation.RelativePath)
            $currentItem = $resolved.Item
            $expectedCurrentSize = [int64] $operation.ExpectedCurrentSize

            if ([string] $operation.Action -eq 'CopyMissing') {
                if ($null -eq $currentItem) {
                    $status = 'AlreadyRestored'
                    $details = 'The synchronized file is already absent.'
                    $successCount++
                    continue
                }

                $currentSize = Get-MtpItemSize -Item $currentItem
                if ($currentSize -ne $expectedCurrentSize) {
                    $status = 'ConflictSkipped'
                    $details = "Current size $currentSize differs from synchronized size $expectedCurrentSize."
                    $skippedCount++
                    continue
                }

                $quarantineDirectory = Join-Path `
                    (Join-Path $quarantineRoot (ConvertTo-SafeName $device.DisplayName)) `
                    $resolved.ParentPath
                $quarantineFile = Copy-MtpItemToLocal `
                    -SourceItem $currentItem `
                    -DestinationDirectory $quarantineDirectory `
                    -ExpectedSize $currentSize `
                    -TimeoutSeconds $TimeoutSeconds
                Remove-MtpItemSafely `
                    -Item $currentItem `
                    -ParentFolder $resolved.Parent `
                    -Name $resolved.FileName `
                    -TimeoutSeconds $TimeoutSeconds
                $currentWasRemoved = $true
                $status = 'Success'
                $details = "Added file removed from the device after a safety copy was saved to '$quarantineFile'."
                $successCount++
                continue
            }

            if ([string] $operation.Action -ne 'ReplaceOlder') {
                $status = 'Skipped'
                $details = "Unsupported restore action: $($operation.Action)"
                $skippedCount++
                continue
            }

            $backupFile = Join-Path $runRoot ([string] $operation.BackupRelativePath)
            $resolvedBackup = [IO.Path]::GetFullPath($backupFile)
            $runRootWithSeparator = [IO.Path]::GetFullPath($runRoot).TrimEnd('\') + '\'
            if (-not $resolvedBackup.StartsWith($runRootWithSeparator, [StringComparison]::OrdinalIgnoreCase) -or
                -not (Test-Path -LiteralPath $resolvedBackup)) {
                throw "Restore backup is missing or outside the run directory: '$backupFile'."
            }

            $originalSize = [int64] $operation.OriginalSize
            if ((Get-Item -LiteralPath $resolvedBackup).Length -ne $originalSize) {
                throw "Restore backup size does not match the manifest: '$resolvedBackup'."
            }
            $backupHashProperty = $operation.PSObject.Properties['BackupSha256']
            $expectedBackupHash = if ($null -eq $backupHashProperty) { '' } else { [string] $backupHashProperty.Value }
            if (-not [string]::IsNullOrWhiteSpace($expectedBackupHash)) {
                $actualBackupHash = (Get-FileHash -LiteralPath $resolvedBackup -Algorithm SHA256).Hash
                if ($actualBackupHash -ine $expectedBackupHash) {
                    throw "Restore backup SHA-256 does not match the manifest: '$resolvedBackup'."
                }
            }

            if ($null -ne $currentItem) {
                $currentSize = Get-MtpItemSize -Item $currentItem
                if ($currentSize -eq $originalSize) {
                    $status = 'AlreadyRestored'
                    $details = 'The original-size file is already present.'
                    $successCount++
                    continue
                }
                if ($currentSize -ne $expectedCurrentSize) {
                    $status = 'ConflictSkipped'
                    $details = "Current size $currentSize differs from synchronized size $expectedCurrentSize."
                    $skippedCount++
                    continue
                }

                $quarantineDirectory = Join-Path `
                    (Join-Path $quarantineRoot (ConvertTo-SafeName $device.DisplayName)) `
                    $resolved.ParentPath
                $quarantineFile = Copy-MtpItemToLocal `
                    -SourceItem $currentItem `
                    -DestinationDirectory $quarantineDirectory `
                    -ExpectedSize $currentSize `
                    -TimeoutSeconds $TimeoutSeconds
                Remove-MtpItemSafely `
                    -Item $currentItem `
                    -ParentFolder $resolved.Parent `
                    -Name $resolved.FileName `
                    -TimeoutSeconds $TimeoutSeconds
                $currentWasRemoved = $true
            }

            $destinationFolder = Get-OrCreateMtpFolderPath `
                -RootItem $device.MapItem `
                -RelativeFolder $resolved.ParentPath `
                -TimeoutSeconds $TimeoutSeconds
            $null = Copy-LocalFileToMtp `
                -SourcePath $resolvedBackup `
                -DestinationFolder $destinationFolder `
                -ExpectedSize $originalSize `
                -TimeoutSeconds $TimeoutSeconds
            $status = 'Success'
            $details = "Original file restored from: $resolvedBackup"
            $successCount++
        }
        catch {
            $failureCount++
            $details = $_.Exception.Message
            if ($currentWasRemoved -and
                [string] $operation.Action -eq 'ReplaceOlder' -and
                -not [string]::IsNullOrWhiteSpace($quarantineFile) -and
                (Test-Path -LiteralPath $quarantineFile)) {
                try {
                    $resolved = Resolve-MtpRelativeFile -Device $device -RelativePath ([string] $operation.RelativePath)
                    if ($null -eq $resolved.Parent) {
                        throw "The original parent folder is no longer available: '$($resolved.ParentPath)'."
                    }

                    if ($null -ne $resolved.Item) {
                        $failedCopySize = Get-MtpItemSize -Item $resolved.Item
                        $failedCopyDirectory = Join-Path `
                            (Join-Path $quarantineRoot (ConvertTo-SafeName $device.DisplayName)) `
                            ('failed-restore\' + $resolved.ParentPath)
                        $failedCopyFile = Copy-MtpItemToLocal `
                            -SourceItem $resolved.Item `
                            -DestinationDirectory $failedCopyDirectory `
                            -ExpectedSize $failedCopySize `
                            -TimeoutSeconds $TimeoutSeconds
                        Remove-MtpItemSafely `
                            -Item $resolved.Item `
                            -ParentFolder $resolved.Parent `
                            -Name $resolved.FileName `
                            -TimeoutSeconds $TimeoutSeconds
                        $details += " The failed restore copy was preserved at '$failedCopyFile'."
                    }

                    $rollbackSize = (Get-Item -LiteralPath $quarantineFile).Length
                    $null = Copy-LocalFileToMtp `
                        -SourcePath $quarantineFile `
                        -DestinationFolder $resolved.Parent `
                        -ExpectedSize $rollbackSize `
                        -TimeoutSeconds $TimeoutSeconds
                    $details += ' The synchronized file was copied back to the device.'
                }
                catch {
                    $details += " Automatic rollback also failed: $($_.Exception.Message)"
                }
            }
        }
        finally {
            $restoreAudit.Add([pscustomobject]@{
                Timestamp    = (Get-Date).ToString('s')
                Device       = $device.DisplayName
                Action       = [string] $operation.Action
                RelativePath = [string] $operation.RelativePath
                Status       = $status
                Details      = $details
            })
        }
    }

    $auditPath = Join-Path $runRoot ("restore-{0}-{1}.csv" -f $restoreId, (ConvertTo-SafeName $device.DisplayName))
    $restoreAudit | Export-Csv -LiteralPath $auditPath -NoTypeInformation -Encoding UTF8 -WhatIf:$false

    $donePayload = [pscustomobject]@{
        Device   = $device.DisplayName
        Restored = $successCount
        Skipped  = $skippedCount
        Failures = $failureCount
        Audit    = $auditPath
    }
    Write-Output ('XCT_RESTORE_DONE:' + ($donePayload | ConvertTo-Json -Compress))

    if ($failureCount -gt 0) {
        throw "Restore completed with $failureCount failed operation(s). See '$auditPath'."
    }
}

try {
    if ($GuiUsbStatusJson) {
        $usbPayload = @(Get-Air3UsbDevices)
        Write-Output ('XCT_JSON:' + (ConvertTo-Json -InputObject $usbPayload -Compress))
        exit 0
    }

    $script:Shell = New-Object -ComObject Shell.Application
    Write-Status 'Searching for connected XCTrack MTP devices...'
    $devices = @(Get-XCTrackDevices)

    if ($GuiStatusJson) {
        $statusPayload = @($devices | ForEach-Object {
            [pscustomobject]@{
                DisplayName = $_.DisplayName
                Name        = $_.Name
                Layout      = $_.Layout
                Path        = $_.Path
                Ready       = $true
            }
        })
        Write-Output ('XCT_JSON:' + (ConvertTo-Json -InputObject $statusPayload -Compress))
        exit 0
    }

    if ($GuiListRestoresJson) {
        if ([string]::IsNullOrWhiteSpace($RestoreDevicePath)) {
            throw 'RestoreDevicePath is required with GuiListRestoresJson.'
        }
        $connected = @($devices | Where-Object { $_.Path -eq $RestoreDevicePath })
        $restorePoints = if ($connected.Count -eq 0) {
            @()
        }
        else {
            @(Get-RestorePointsForDevice -DevicePath $RestoreDevicePath)
        }
        Write-Output ('XCT_RESTORE_POINTS:' + (ConvertTo-Json -InputObject $restorePoints -Depth 5 -Compress))
        exit 0
    }

    if ($GuiRestore) {
        if ([string]::IsNullOrWhiteSpace($RestoreDevicePath) -or
            [string]::IsNullOrWhiteSpace($RestoreManifest)) {
            throw 'RestoreDevicePath and RestoreManifest are required with GuiRestore.'
        }
        Invoke-DeviceRestore `
            -ConnectedDevices $devices `
            -DevicePath $RestoreDevicePath `
            -ManifestPath $RestoreManifest `
            -TimeoutSeconds $TransferTimeoutSeconds
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

        $firstRoadMapBytes = ($firstInventory.Values |
            Where-Object { $_.RelativePath -like 'RoadMap\*' } |
            Measure-Object -Property Size -Sum).Sum
        $firstGecBytes = ($firstInventory.Values |
            Where-Object { $_.RelativePath -like 'gec\*' } |
            Measure-Object -Property Size -Sum).Sum
        $secondRoadMapBytes = ($secondInventory.Values |
            Where-Object { $_.RelativePath -like 'RoadMap\*' } |
            Measure-Object -Property Size -Sum).Sum
        $secondGecBytes = ($secondInventory.Values |
            Where-Object { $_.RelativePath -like 'gec\*' } |
            Measure-Object -Property Size -Sum).Sum
        if ($null -eq $firstRoadMapBytes) { $firstRoadMapBytes = 0 }
        if ($null -eq $firstGecBytes) { $firstGecBytes = 0 }
        if ($null -eq $secondRoadMapBytes) { $secondRoadMapBytes = 0 }
        if ($null -eq $secondGecBytes) { $secondGecBytes = 0 }

        $comparePayload = [pscustomobject]@{
            DeviceA = [pscustomobject]@{
                Name            = $firstDevice.DisplayName
                Layout          = $firstDevice.Layout
                FileCount       = $firstInventory.Count
                RoadMapCount    = @($firstInventory.Keys | Where-Object { $_ -like 'RoadMap\*' }).Count
                GecCount        = @($firstInventory.Keys | Where-Object { $_ -like 'gec\*' }).Count
                RoadMapBytes    = [int64] $firstRoadMapBytes
                GecBytes        = [int64] $firstGecBytes
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
                RoadMapBytes    = [int64] $secondRoadMapBytes
                GecBytes        = [int64] $secondGecBytes
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
    Initialize-RestoreManifest `
        -RunRoot $runRoot `
        -FirstDevice $firstDevice `
        -SecondDevice $secondDevice

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
