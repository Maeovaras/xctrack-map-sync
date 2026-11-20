using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace XCTrackMapSync;

internal static class WindowsUsbDetector
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfAllClasses = 0x00000004;
    private const uint SpdrpDeviceDesc = 0x00000000;
    private const uint SpdrpFriendlyName = 0x0000000C;
    private const uint CrSuccess = 0;
    private const uint CmGetIdListFilterEnumerator = 0x00000001;
    private const uint CmGetIdListFilterPresent = 0x00000100;
    private const uint CmLocateDevnodeNormal = 0x00000000;
    private const uint CmDrpDeviceDesc = 0x00000001;
    private const uint CmDrpFriendlyName = 0x0000000D;

    // Match by the name the device reports (for example "AIR3-7.3+"), the same
    // rule the PowerShell engine uses. The USB ID alone is not enough: AIR3
    // uses MediaTek's generic MTP ID (VID_0E8D&PID_2008), which many MediaTek
    // Android phones share.
    private static readonly Regex Air3Name = new(@"AIR\s*(3|\u00B3)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public static IReadOnlyList<DeviceStatus> GetConnectedAir3Devices()
    {
        var instanceIds = ReadPresentUsbInstanceIds();
        if (instanceIds is null)
        {
            return GetConnectedAir3DevicesWithSetupApi();
        }

        var devices = new List<(string InstanceId, string Name)>();
        foreach (var instanceId in instanceIds)
        {
            var name = ReadDevNodeName(instanceId);
            if (IsAir3Name(name))
            {
                devices.Add((instanceId, name));
            }
        }
        return BuildDeviceList(devices);
    }

    private static bool IsAir3Name(string name) =>
        !string.IsNullOrWhiteSpace(name) && Air3Name.IsMatch(name);

    private static string ReadDevNodeName(string instanceId)
    {
        if (CmLocateDevNode(out var devInst, instanceId, CmLocateDevnodeNormal) != CrSuccess)
        {
            return string.Empty;
        }

        var name = ReadDevNodeProperty(devInst, CmDrpFriendlyName);
        return string.IsNullOrWhiteSpace(name) ? ReadDevNodeProperty(devInst, CmDrpDeviceDesc) : name;
    }

    private static string ReadDevNodeProperty(uint devInst, uint property)
    {
        var buffer = new byte[1024];
        var length = (uint)buffer.Length;
        if (CmGetDevNodeRegistryProperty(devInst, property, out _, buffer, ref length, 0) != CrSuccess)
        {
            return string.Empty;
        }
        var bytes = (int)Math.Min(length, (uint)buffer.Length);
        return Encoding.Unicode.GetString(buffer, 0, bytes).TrimEnd('\0');
    }

    private static IReadOnlyList<string>? ReadPresentUsbInstanceIds()
    {
        const uint flags = CmGetIdListFilterEnumerator | CmGetIdListFilterPresent;
        if (CmGetDeviceIdListSize(out var length, "USB", flags) != CrSuccess || length == 0)
        {
            return null;
        }

        var buffer = new char[length];
        if (CmGetDeviceIdList("USB", buffer, length, flags) != CrSuccess)
        {
            return null;
        }

        return new string(buffer)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
    }

    private static IReadOnlyList<DeviceStatus> GetConnectedAir3DevicesWithSetupApi()
    {
        var deviceInfoSet = SetupDiGetClassDevs(
            IntPtr.Zero,
            "USB",
            IntPtr.Zero,
            DigcfPresent | DigcfAllClasses);
        if (deviceInfoSet == InvalidHandleValue)
        {
            return Array.Empty<DeviceStatus>();
        }

        try
        {
            var devices = new List<(string InstanceId, string Name)>();
            for (uint index = 0; ; index++)
            {
                var deviceInfo = new SpDevinfoData
                {
                    Size = Marshal.SizeOf<SpDevinfoData>()
                };
                if (!SetupDiEnumDeviceInfo(deviceInfoSet, index, ref deviceInfo))
                {
                    break;
                }

                var instanceId = ReadInstanceId(deviceInfoSet, ref deviceInfo);
                if (string.IsNullOrWhiteSpace(instanceId))
                {
                    continue;
                }

                var name = ReadRegistryProperty(deviceInfoSet, ref deviceInfo, SpdrpFriendlyName);
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = ReadRegistryProperty(deviceInfoSet, ref deviceInfo, SpdrpDeviceDesc);
                }
                if (!IsAir3Name(name))
                {
                    continue;
                }

                devices.Add((instanceId, name));
            }

            return BuildDeviceList(devices);
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    private static IReadOnlyList<DeviceStatus> BuildDeviceList(
        IEnumerable<(string InstanceId, string Name)> detectedDevices)
    {
        var sorted = detectedDevices
            .Select(device => new DeviceStatus
            {
                DisplayName = device.Name,
                Name = device.Name,
                Key = GetUsbDeviceKey(device.InstanceId),
                Layout = "USB-MTP",
                Path = device.InstanceId,
                Ready = false
            })
            .OrderBy(device => device.Identity, StringComparer.OrdinalIgnoreCase)
            .ThenBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var group in sorted.GroupBy(device => device.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() < 2)
            {
                continue;
            }

            var number = 1;
            foreach (var device in group)
            {
                device.DisplayName = $"{device.Name} #{number}";
                number++;
            }
        }
        return sorted;
    }

    private static string ReadInstanceId(IntPtr deviceInfoSet, ref SpDevinfoData deviceInfo)
    {
        var buffer = new StringBuilder(512);
        return SetupDiGetDeviceInstanceId(deviceInfoSet, ref deviceInfo, buffer, buffer.Capacity, out _)
            ? buffer.ToString()
            : string.Empty;
    }

    private static string ReadRegistryProperty(
        IntPtr deviceInfoSet,
        ref SpDevinfoData deviceInfo,
        uint property)
    {
        var buffer = new byte[1024];
        if (!SetupDiGetDeviceRegistryProperty(
                deviceInfoSet,
                ref deviceInfo,
                property,
                out _,
                buffer,
                buffer.Length,
                out _))
        {
            return string.Empty;
        }
        return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    private static string GetUsbDeviceKey(string instanceId)
    {
        var parts = instanceId.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return instanceId.ToLowerInvariant();
        }

        var hardware = parts[1].Split('&', StringSplitOptions.RemoveEmptyEntries);
        var vendor = hardware.FirstOrDefault(part => part.StartsWith("VID_", StringComparison.OrdinalIgnoreCase));
        var product = hardware.FirstOrDefault(part => part.StartsWith("PID_", StringComparison.OrdinalIgnoreCase));
        var interfaceId = hardware.FirstOrDefault(part => part.StartsWith("MI_", StringComparison.OrdinalIgnoreCase));
        if (vendor is null || product is null)
        {
            return instanceId.ToLowerInvariant();
        }

        var vendorValue = vendor[4..];
        var productValue = product[4..];
        var interfaceValue = interfaceId is null ? string.Empty : $"&{interfaceId}";
        return $"usb:{vendorValue}:{productValue}{interfaceValue}:{parts[2]}".ToLowerInvariant();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        public int Size;
        public Guid ClassGuid;
        public uint DeviceInstance;
        public UIntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        IntPtr classGuid,
        string? enumerator,
        IntPtr parentWindow,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet,
        uint memberIndex,
        ref SpDevinfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceId(
        IntPtr deviceInfoSet,
        ref SpDevinfoData deviceInfoData,
        StringBuilder deviceInstanceId,
        int deviceInstanceIdSize,
        out int requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr deviceInfoSet,
        ref SpDevinfoData deviceInfoData,
        uint property,
        out uint propertyRegDataType,
        [Out] byte[] propertyBuffer,
        int propertyBufferSize,
        out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", CharSet = CharSet.Unicode)]
    private static extern uint CmLocateDevNode(
        out uint devInst,
        string deviceId,
        uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Registry_PropertyW", CharSet = CharSet.Unicode)]
    private static extern uint CmGetDevNodeRegistryProperty(
        uint devInst,
        uint property,
        out uint registryDataType,
        [Out] byte[] buffer,
        ref uint length,
        uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_List_SizeW", CharSet = CharSet.Unicode)]
    private static extern uint CmGetDeviceIdListSize(
        out uint length,
        string filter,
        uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_ListW", CharSet = CharSet.Unicode)]
    private static extern uint CmGetDeviceIdList(
        string filter,
        [Out] char[] buffer,
        uint bufferLength,
        uint flags);
}
