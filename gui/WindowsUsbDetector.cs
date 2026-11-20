using System.Runtime.InteropServices;
using System.Text;

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
    private const string Air3HardwareId = "VID_0E8D&PID_2008";
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public static IReadOnlyList<DeviceStatus> GetConnectedAir3Devices()
    {
        var instanceIds = ReadPresentUsbInstanceIds();
        if (instanceIds is null)
        {
            return GetConnectedAir3DevicesWithSetupApi();
        }

        return BuildDeviceList(instanceIds.Select(instanceId => (InstanceId: instanceId, Name: "AIR3")));
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
            .Where(instanceId => instanceId.Contains(Air3HardwareId, StringComparison.OrdinalIgnoreCase))
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
                if (string.IsNullOrWhiteSpace(instanceId) ||
                    !instanceId.Contains(Air3HardwareId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = ReadRegistryProperty(deviceInfoSet, ref deviceInfo, SpdrpFriendlyName);
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = ReadRegistryProperty(deviceInfoSet, ref deviceInfo, SpdrpDeviceDesc);
                }
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = "AIR3";
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
