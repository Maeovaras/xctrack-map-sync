using System.Text.Json.Serialization;

namespace XCTrackMapSync;

internal enum UiLanguage
{
    Lithuanian,
    English
}

internal sealed class DeviceStatus
{
    public string DisplayName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Layout { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool Ready { get; set; }

    /// <summary>
    /// USB identity shared by the fast USB check and the XCTrack folder scan.
    /// Older engine output has no key, so fall back to the device path.
    /// </summary>
    public string Identity => string.IsNullOrWhiteSpace(Key) ? Path : Key;
}

internal sealed class DeviceComparison
{
    public string Name { get; set; } = string.Empty;
    public string Layout { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public int RoadMapCount { get; set; }
    public int GecCount { get; set; }
    public long RoadMapBytes { get; set; }
    public long GecBytes { get; set; }
    public bool CountsOnly { get; set; }
    public int MissingIncoming { get; set; }
    public int NewerIncoming { get; set; }
    public long BytesIncoming { get; set; }
}

internal sealed class ComparisonResult
{
    public DeviceComparison DeviceA { get; set; } = new();
    public DeviceComparison DeviceB { get; set; } = new();
    public int Transfers { get; set; }
    public int Same { get; set; }
    public int Conflicts { get; set; }
    public long TotalBytes { get; set; }
}

internal sealed class ProgressInfo
{
    public int Current { get; set; }
    public int Total { get; set; }
    public string Path { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
}

internal sealed class DoneInfo
{
    public int Transfers { get; set; }
    public int Failures { get; set; }
    public string Audit { get; set; } = string.Empty;
}

internal sealed class RestorePointInfo
{
    public string RunId { get; set; } = string.Empty;
    public string CreatedUtc { get; set; } = string.Empty;
    public string ManifestPath { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public int RestoreBackups { get; set; }
    public int RemoveAddedFiles { get; set; }
    public int TotalOperations { get; set; }
}

internal sealed class RestoreDoneInfo
{
    public string Device { get; set; } = string.Empty;
    public int Restored { get; set; }
    public int Skipped { get; set; }
    public int Failures { get; set; }
    public string Audit { get; set; } = string.Empty;
}

internal sealed record PowerShellLine(string Text, bool IsError);

internal sealed record PowerShellResult(int ExitCode, IReadOnlyList<PowerShellLine> Lines)
{
    public string? FindPayload(string prefix) =>
        Lines.LastOrDefault(line => line.Text.StartsWith(prefix, StringComparison.Ordinal))?.Text[prefix.Length..];
}
