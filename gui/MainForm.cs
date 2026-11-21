using System.Text.Json;
using System.Globalization;

namespace XCTrackMapSync;

internal sealed class MainForm : Form
{
    private const int WmDeviceChange = 0x0219;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceRemoveComplete = 0x8004;
    private const string JsonPrefix = "XCT_JSON:";
    private const string ProgressPrefix = "XCT_PROGRESS:";
    private const string DonePrefix = "XCT_DONE:";
    private const string RestorePointsPrefix = "XCT_RESTORE_POINTS:";
    private const string RestoreProgressPrefix = "XCT_RESTORE_PROGRESS:";
    private const string RestoreDonePrefix = "XCT_RESTORE_DONE:";
    private const int MapCountAttempts = 2;
    private static readonly TimeSpan MapCountAttemptTimeout = TimeSpan.FromSeconds(75);

    private PowerShellEngine? _engine;
    private readonly DeviceCard _deviceA = new("Device A");
    private readonly DeviceCard _deviceB = new("Device B");
    private readonly Label _titleLabel = new();
    private readonly Label _subtitleLabel = new();
    private readonly Label _footerLabel = new();
    private readonly ComboBox _languageCombo = new();
    private readonly Label _statusLabel = new();
    private readonly Label _summaryTitle = new();
    private readonly Label _toALabel = new();
    private readonly Label _toBLabel = new();
    private readonly Label _conflictsLabel = new();
    private readonly Label _equalLabel = new();
    private readonly Button _refreshButton = new();
    private readonly Button _syncButton = new();
    private readonly Button _restoreAButton = new();
    private readonly Button _restoreBButton = new();
    private readonly ProgressBar _progressA = new();
    private readonly ProgressBar _progressB = new();
    private readonly RichTextBox _log = new();
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly CancellationTokenSource _closing = new();
    private readonly string? _demoMode;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private bool _busy;
    private ComparisonResult? _comparison;
    private IReadOnlyList<DeviceStatus> _lastDevices = Array.Empty<DeviceStatus>();
    private IReadOnlyList<DeviceStatus> _usbDevices = Array.Empty<DeviceStatus>();
    private IReadOnlyList<DeviceStatus> _readyDevices = Array.Empty<DeviceStatus>();
    private Task<IReadOnlyList<DeviceStatus>>? _initialUsbDetection;
    private string _comparedDevicesKey = string.Empty;

    // A USB arrival/removal refresh must not move the progress bars or show the
    // wait cursor unless it starts real work: map counting or a comparison.
    private bool _quietRefresh;

    // Per-device map statistics, keyed by USB identity. Counting ~5 000 terrain
    // files over MTP takes 20-40 s, so it runs once per connection, not per tick.
    private readonly Dictionary<string, DeviceComparison> _deviceStats = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _deviceStatsFailed = new(StringComparer.OrdinalIgnoreCase);
    private string _lastLoggedDeviceStateKey = string.Empty;
    private UiLanguage _language;

    // Extracting the embedded PowerShell engine from a single-file EXE can be
    // delayed by Windows security scanning. Do it only after the window and the
    // immediate native USB status have been painted.
    private PowerShellEngine Engine => _engine ??= new PowerShellEngine();

    public MainForm(string? demoMode = null, string? languageOverride = null)
    {
        _demoMode = demoMode;
        _language = LoadLanguage(languageOverride);
        if (string.IsNullOrWhiteSpace(_demoMode))
        {
            // Run the first native USB lookup while the interface is being built.
            // By the time the window is shown, its result is normally ready.
            _initialUsbDetection = Task.Run(WindowsUsbDetector.GetConnectedAir3Devices);
        }
        Text = "XCTrack Map Sync";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1_012, 782);
        Size = new Size(1_104, 874);
        BackColor = Color.FromArgb(246, 248, 251);
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;

        BuildInterface();
        ApplyLanguage();

        // One-shot debounce for real Windows USB arrival/removal messages.
        // There is no periodic full refresh: completed progress stays at 100%.
        _timer.Interval = 1_000;
        _timer.Tick += async (_, _) =>
        {
            _timer.Stop();
            if (!_busy)
            {
                await RefreshAndCompareAsync(showErrors: false);
            }
        };

        Shown += async (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_demoMode))
            {
                ApplyDemoState(_demoMode);
                return;
            }
            await RefreshAndCompareAsync(showErrors: true, forceCompare: true);
        };
        FormClosing += (_, _) => _closing.Cancel();
    }

    private void BuildInterface()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(28, 22, 28, 22),
            ColumnCount = 1,
            RowCount = 7,
            BackColor = BackColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 94));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 228));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 122));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        _titleLabel.Dock = DockStyle.Fill;
        _titleLabel.Font = new Font("Segoe UI Semibold", 18f);
        _titleLabel.ForeColor = Color.FromArgb(26, 34, 48);
        _titleLabel.TextAlign = ContentAlignment.MiddleLeft;
        _titleLabel.AutoEllipsis = true;
        _titleLabel.Margin = Padding.Empty;

        _subtitleLabel.Dock = DockStyle.Fill;
        _subtitleLabel.Font = new Font("Segoe UI", 10f);
        _subtitleLabel.ForeColor = Color.FromArgb(100, 110, 125);
        _subtitleLabel.TextAlign = ContentAlignment.TopLeft;
        _subtitleLabel.AutoEllipsis = true;
        _subtitleLabel.Margin = new Padding(1, 0, 12, 0);

        _languageCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _languageCombo.Items.AddRange(new object[] { "Lietuvių", "English" });
        _languageCombo.Width = 108;
        _languageCombo.Anchor = AnchorStyles.Right;
        _languageCombo.Margin = new Padding(0, 0, 10, 0);
        _languageCombo.SelectedIndex = _language == UiLanguage.Lithuanian ? 0 : 1;
        _languageCombo.SelectedIndexChanged += (_, _) =>
        {
            var selected = _languageCombo.SelectedIndex == 0
                ? UiLanguage.Lithuanian
                : UiLanguage.English;
            if (selected == _language)
            {
                return;
            }
            _language = selected;
            SaveLanguage();
            ApplyLanguage();
        };
        _refreshButton.Size = new Size(112, 36);
        _refreshButton.Anchor = AnchorStyles.Right;
        _refreshButton.Margin = Padding.Empty;
        StyleSecondaryButton(_refreshButton);
        _refreshButton.Click += async (_, _) => await RefreshAndCompareAsync(showErrors: true, forceCompare: true);
        header.Controls.Add(_titleLabel, 0, 0);
        header.Controls.Add(_subtitleLabel, 0, 1);
        header.Controls.Add(_languageCombo, 1, 0);
        header.SetRowSpan(_languageCombo, 2);
        header.Controls.Add(_refreshButton, 2, 0);
        header.SetRowSpan(_refreshButton, 2);

        var devices = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(0, 0, 0, 14)
        };
        devices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        devices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _deviceA.Dock = DockStyle.Fill;
        _deviceB.Dock = DockStyle.Fill;
        _deviceB.Margin = new Padding(12, 0, 0, 0);
        devices.Controls.Add(_deviceA, 0, 0);
        devices.Controls.Add(_deviceB, 1, 0);

        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.Font = new Font("Segoe UI Semibold", 10f);
        _statusLabel.ForeColor = Color.FromArgb(76, 88, 105);

        var summary = BuildSummaryPanel();

        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 215));
        var progressPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Margin = new Padding(0, 0, 20, 0),
            Padding = Padding.Empty
        };
        progressPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        progressPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        progressPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        progressPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var progressALabel = BuildProgressLabel("A");
        var progressBLabel = BuildProgressLabel("B");
        ConfigureProgressBar(_progressA);
        ConfigureProgressBar(_progressB);
        progressALabel.Margin = new Padding(0, 0, 8, 0);
        progressBLabel.Margin = new Padding(8, 0, 0, 0);
        _progressA.Margin = new Padding(0, 6, 8, 6);
        _progressB.Margin = new Padding(8, 6, 0, 6);
        progressPanel.Controls.Add(progressALabel, 0, 0);
        progressPanel.Controls.Add(progressBLabel, 1, 0);
        progressPanel.Controls.Add(_progressA, 0, 1);
        progressPanel.Controls.Add(_progressB, 1, 1);

        _syncButton.Enabled = false;
        _syncButton.Size = new Size(210, 42);
        _syncButton.Anchor = AnchorStyles.Right;
        _syncButton.Margin = Padding.Empty;
        StylePrimaryButton(_syncButton);
        _syncButton.Click += async (_, _) => await SynchronizeAsync();

        _restoreAButton.Enabled = false;
        _restoreAButton.Size = new Size(140, 42);
        _restoreAButton.Anchor = AnchorStyles.Right;
        _restoreAButton.Margin = new Padding(0, 0, 8, 0);
        StyleSecondaryButton(_restoreAButton);
        _restoreAButton.Click += async (_, _) => await RestoreDeviceAsync(0);

        _restoreBButton.Enabled = false;
        _restoreBButton.Size = new Size(140, 42);
        _restoreBButton.Anchor = AnchorStyles.Right;
        _restoreBButton.Margin = new Padding(0, 0, 8, 0);
        StyleSecondaryButton(_restoreBButton);
        _restoreBButton.Click += async (_, _) => await RestoreDeviceAsync(1);

        actions.Controls.Add(progressPanel, 0, 0);
        actions.Controls.Add(_restoreAButton, 1, 0);
        actions.Controls.Add(_restoreBButton, 2, 0);
        actions.Controls.Add(_syncButton, 3, 0);

        var logPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Padding = new Padding(14),
            Margin = new Padding(0, 8, 0, 0)
        };
        _log.Dock = DockStyle.Fill;
        _log.Multiline = true;
        _log.ScrollBars = RichTextBoxScrollBars.Vertical;
        _log.ReadOnly = true;
        _log.BorderStyle = BorderStyle.None;
        _log.BackColor = Color.White;
        _log.ForeColor = Color.FromArgb(74, 84, 98);
        _log.Font = new Font("Consolas", 9f);
        logPanel.Controls.Add(_log);

        _footerLabel.Dock = DockStyle.Fill;
        _footerLabel.TextAlign = ContentAlignment.BottomLeft;
        _footerLabel.ForeColor = Color.FromArgb(120, 130, 145);
        _footerLabel.Font = new Font("Segoe UI", 8.5f);

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(devices, 0, 1);
        root.Controls.Add(_statusLabel, 0, 2);
        root.Controls.Add(summary, 0, 3);
        root.Controls.Add(actions, 0, 4);
        root.Controls.Add(logPanel, 0, 5);
        root.Controls.Add(_footerLabel, 0, 6);
        Controls.Add(root);
    }

    private static Label BuildProgressLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.FromArgb(100, 110, 125),
        Font = new Font("Segoe UI Semibold", 8.5f),
        Margin = Padding.Empty
    };

    private static void ConfigureProgressBar(ProgressBar progressBar)
    {
        progressBar.Dock = DockStyle.Fill;
        progressBar.Style = ProgressBarStyle.Continuous;
        progressBar.Minimum = 0;
        progressBar.Maximum = 100;
        progressBar.Value = 0;
        progressBar.Margin = new Padding(0, 4, 0, 4);
    }

    private Panel BuildSummaryPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Padding = new Padding(20, 12, 20, 12),
            ColumnCount = 2,
            RowCount = 3
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        _summaryTitle.Text = "Laukiama abiejų įrenginių";
        _summaryTitle.Font = new Font("Segoe UI Semibold", 13f);
        _summaryTitle.ForeColor = Color.FromArgb(31, 41, 55);
        _summaryTitle.Dock = DockStyle.Fill;
        _summaryTitle.TextAlign = ContentAlignment.MiddleLeft;
        _summaryTitle.AutoEllipsis = true;
        _summaryTitle.Margin = Padding.Empty;

        _toALabel.Dock = DockStyle.Fill;
        _toALabel.TextAlign = ContentAlignment.MiddleLeft;
        _toALabel.AutoEllipsis = true;
        _toALabel.Margin = new Padding(0, 0, 12, 0);
        _toALabel.ForeColor = Color.FromArgb(62, 72, 88);

        _toBLabel.Dock = DockStyle.Fill;
        _toBLabel.TextAlign = ContentAlignment.MiddleLeft;
        _toBLabel.AutoEllipsis = true;
        _toBLabel.Margin = new Padding(12, 0, 0, 0);
        _toBLabel.ForeColor = Color.FromArgb(62, 72, 88);

        _equalLabel.Dock = DockStyle.Fill;
        _equalLabel.TextAlign = ContentAlignment.MiddleLeft;
        _equalLabel.AutoEllipsis = true;
        _equalLabel.Margin = new Padding(0, 0, 12, 0);
        _equalLabel.ForeColor = Color.FromArgb(62, 72, 88);

        _conflictsLabel.Dock = DockStyle.Fill;
        _conflictsLabel.TextAlign = ContentAlignment.MiddleLeft;
        _conflictsLabel.AutoEllipsis = true;
        _conflictsLabel.Margin = new Padding(12, 0, 0, 0);
        _conflictsLabel.ForeColor = Color.FromArgb(62, 72, 88);

        panel.Controls.Add(_summaryTitle, 0, 0);
        panel.SetColumnSpan(_summaryTitle, 2);
        panel.Controls.Add(_toALabel, 0, 1);
        panel.Controls.Add(_toBLabel, 1, 1);
        panel.Controls.Add(_equalLabel, 0, 2);
        panel.Controls.Add(_conflictsLabel, 1, 2);
        return panel;
    }

    private async Task RefreshAndCompareAsync(bool showErrors, bool forceCompare = false)
    {
        if (_busy)
        {
            return;
        }

        _quietRefresh = !forceCompare;
        SetBusy(true, T("Tikrinami prijungti įrenginiai...", "Checking connected devices..."));
        if (forceCompare)
        {
            // Refresh, synchronization and restore change the files: count again.
            _deviceStats.Clear();
            _deviceStatsFailed.Clear();
        }

        try
        {
            try
            {
                var usbDevices = await DetectUsbDevicesAsync();

                // A ready device stays ready while it is still on USB. Only drop
                // it here if this check saw it before and it is now disconnected;
                // the full XCTrack folder scan below remains authoritative.
                var previousUsb = _usbDevices.Select(device => device.Identity).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var currentUsb = usbDevices.Select(device => device.Identity).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var stillReady = _readyDevices.Where(device =>
                    !previousUsb.Contains(device.Identity) || currentUsb.Contains(device.Identity));

                _usbDevices = usbDevices;
                var merged = MergeDevices(stillReady, usbDevices);
                UpdateDeviceState(merged);
                _statusLabel.Text = GetWaitingStatus(merged);
                SetProgressPercent(20);

                // Give Windows Forms a chance to paint the green USB state before
                // the slower MTP folder and map checks begin in the background.
                await Task.Yield();
            }
            catch (Exception) when (!_closing.IsCancellationRequested)
            {
                // The full XCTrack directory scan below remains authoritative.
                _usbDevices = Array.Empty<DeviceStatus>();
            }

            var result = await ReadDeviceStatusWithRetryAsync();

            var payload = result.FindPayload(JsonPrefix);
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(payload))
            {
                throw new InvalidOperationException(BuildError(result, T("Nepavyko nuskaityti įrenginių.", "Could not read connected devices.")));
            }

            _readyDevices = JsonSerializer.Deserialize<List<DeviceStatus>>(payload, _jsonOptions) ?? new();
            SetProgressPercent(45);

            // A device that is on USB but whose XCTrack folder scan did not see it
            // (locked, slow MTP) stays "USB / MTP connected" instead of disappearing.
            var devices = MergeDevices(_readyDevices, _usbDevices);
            ForgetDisconnectedStats(devices);
            UpdateDeviceState(devices);

            if (devices.Count == 2 && devices.All(device => device.Ready))
            {
                // Reading the full inventory over MTP is slow. Repeat it only when
                // the connected pair changed or the user/sync/restore asked for it.
                if (forceCompare || !IsComparisonCurrent(devices))
                {
                    await CompareAsync();
                    _comparedDevicesKey = GetDevicesKey(devices);

                    // The comparison already read both inventories; reuse its counts.
                    _deviceStats[devices[0].Identity] = _comparison!.DeviceA;
                    _deviceStats[devices[1].Identity] = _comparison.DeviceB;
                }
                else
                {
                    UpdateComparison(_comparison!);
                }
            }
            else if (devices.Count > 2)
            {
                ResetComparison(T(
                    "Aptikta daugiau nei du XCTrack įrenginiai. Atjunkite nereikalingus.",
                    "More than two XCTrack devices were found. Disconnect the extra devices."));
            }
            else
            {
                ResetComparison(GetWaitingStatus(devices));
                await LoadMissingStatsAsync(devices);
                _statusLabel.Text = GetWaitingStatus(devices);
            }
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            // The application is closing.
        }
        catch (TimeoutException)
        {
            var message = T(
                "MTP patikra neatsakė. Programa netrukus bandys dar kartą.",
                "The MTP check did not respond. The application will retry shortly.");
            ResetComparison(message);
            AppendLog(message, true);
        }
        catch (Exception ex)
        {
            ResetComparison(T("Įrenginių patikra nepavyko.", "Device check failed."));
            AppendLog(T("KLAIDA: ", "ERROR: ") + ex.Message, true);
            if (showErrors)
            {
                MessageBox.Show(this, ex.Message, "XCTrack Map Sync", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            SetBusy(false, _statusLabel.Text);
            _quietRefresh = false;
        }
    }

    private void ShowWorkProgress()
    {
        if (_quietRefresh)
        {
            _quietRefresh = false;
            UseWaitCursor = true;
        }
    }

    private void ForgetDisconnectedStats(IReadOnlyList<DeviceStatus> devices)
    {
        var ready = devices.Where(device => device.Ready).Select(device => device.Identity)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var identity in _deviceStats.Keys.Where(identity => !ready.Contains(identity)).ToList())
        {
            _deviceStats.Remove(identity);
        }
        _deviceStatsFailed.RemoveWhere(identity => !ready.Contains(identity));
    }

    private async Task<IReadOnlyList<DeviceStatus>> DetectUsbDevicesAsync()
    {
        var initialDetection = Interlocked.Exchange(ref _initialUsbDetection, null);
        if (initialDetection is not null)
        {
            return await initialDetection;
        }

        return await Task.Run(WindowsUsbDetector.GetConnectedAir3Devices, _closing.Token);
    }

    private async Task LoadMissingStatsAsync(IReadOnlyList<DeviceStatus> devices)
    {
        if (devices.Count > 2)
        {
            return;
        }

        var pendingDevices = devices.Where(device =>
                device.Ready &&
                !_deviceStats.ContainsKey(device.Identity) &&
                !_deviceStatsFailed.Contains(device.Identity))
            .ToList();
        if (pendingDevices.Count > 0)
        {
            ShowWorkProgress();
        }

        for (var index = 0; index < pendingDevices.Count; index++)
        {
            var device = pendingDevices[index];
            var deviceIndex = devices
                .Select((candidate, candidateIndex) => (candidate, candidateIndex))
                .First(pair => string.Equals(
                    pair.candidate.Identity,
                    device.Identity,
                    StringComparison.OrdinalIgnoreCase))
                .candidateIndex;
            _statusLabel.Text = T(
                $"Skaičiuojami {device.DisplayName} žemėlapiai...",
                $"Counting maps on {device.DisplayName}...");
            SetProgressPercent(55, deviceIndex);
            try
            {
                var result = await ReadDeviceStatsWithRetryAsync(device);
                var payload = result.FindPayload(JsonPrefix);
                if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(payload))
                {
                    throw new InvalidOperationException(BuildError(result, T(
                        "Nepavyko suskaičiuoti žemėlapių.",
                        "Could not count the maps.")));
                }

                _deviceStats[device.Identity] = JsonSerializer.Deserialize<DeviceComparison>(payload, _jsonOptions)
                    ?? throw new InvalidOperationException(T(
                        "Gautas netinkamas statistikos atsakymas.",
                        "The statistics response was invalid."));
            }
            catch (Exception ex) when (!_closing.IsCancellationRequested)
            {
                // Refresh clears this failure and allows another user-initiated try.
                _deviceStatsFailed.Add(device.Identity);
                var message = ex is TimeoutException
                    ? T(
                        "MTP žemėlapių skaičiavimas neatsakė po 2 bandymų. Spauskite Atnaujinti.",
                        "MTP map counting did not respond after 2 attempts. Click Refresh.")
                    : T("KLAIDA: ", "ERROR: ") + ex.Message;
                AppendLog(message, true);
            }
            SetProgressPercent(95, deviceIndex);
            UpdateDeviceState(devices);
        }
    }

    private async Task<PowerShellResult> ReadDeviceStatsWithRetryAsync(DeviceStatus device)
    {
        for (var attempt = 1; attempt <= MapCountAttempts; attempt++)
        {
            try
            {
                return await Engine.RunAsync(
                    new[] { "-GuiStatsJson", "-StatsDevicePath", device.Path },
                    line => HandleEngineLine(line, includeProtocolLines: false),
                    _closing.Token,
                    MapCountAttemptTimeout);
            }
            catch (TimeoutException) when (attempt < MapCountAttempts)
            {
                AppendLog(T(
                    $"{device.DisplayName} MTP neatsakė. Bandymas {attempt + 1}/{MapCountAttempts}...",
                    $"{device.DisplayName} MTP did not respond. Attempt {attempt + 1}/{MapCountAttempts}..."), true);
                await Task.Delay(750, _closing.Token);
            }
        }

        throw new TimeoutException();
    }

    private (string RoadMaps, string Terrain) FormatStats(DeviceComparison stats)
    {
        if (stats.CountsOnly)
        {
            return (
                T(
                    $"Kelių žemėlapiai: {N(stats.RoadMapCount)}",
                    $"Road maps: {N(stats.RoadMapCount)}"),
                T(
                    $"Reljefo žemėlapiai: {N(stats.GecCount)}",
                    $"Terrain: {N(stats.GecCount)}"));
        }

        return (
            T(
                $"Kelių: {N(stats.RoadMapCount)} failai, {FormatBytes(stats.RoadMapBytes)}",
                $"Road maps: {N(stats.RoadMapCount)} files, {FormatBytes(stats.RoadMapBytes)}"),
            T(
                $"Reljefo: {N(stats.GecCount)} failai, {FormatBytes(stats.GecBytes)}",
                $"Terrain: {N(stats.GecCount)} files, {FormatBytes(stats.GecBytes)}"));
    }

    private void ShowReadyDevice(DeviceCard card, DeviceStatus device)
    {
        if (_deviceStats.TryGetValue(device.Identity, out var stats))
        {
            var (roadMaps, terrain) = FormatStats(stats);
            card.SetConnected(device.DisplayName, device.Layout, roadMaps, secondaryDetails: terrain);
        }
        else if (_deviceStatsFailed.Contains(device.Identity))
        {
            card.SetConnected(device.DisplayName, device.Layout, T(
                "Žemėlapių suskaičiuoti nepavyko – spauskite Atnaujinti.",
                "Could not count the maps. Click Refresh."));
        }
        else
        {
            card.SetConnected(device.DisplayName, device.Layout, T(
                "Skaičiuojami žemėlapiai...",
                "Counting maps..."));
        }
    }

    private static List<DeviceStatus> MergeDevices(
        IEnumerable<DeviceStatus> readyDevices,
        IEnumerable<DeviceStatus> usbDevices)
    {
        var merged = new Dictionary<string, DeviceStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in usbDevices)
        {
            merged[device.Identity] = device;
        }
        foreach (var device in readyDevices)
        {
            merged[device.Identity] = device; // The validated XCTrack entry wins.
        }

        // Same order as the engine: device A/B must not swap between checks.
        return merged.Values
            .OrderBy(device => device.Identity, StringComparer.OrdinalIgnoreCase)
            .ThenBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string GetDevicesKey(IEnumerable<DeviceStatus> devices) =>
        string.Join("|", devices.Select(device => $"{device.Identity}:{device.Path}:{device.Ready}"));

    private bool IsComparisonCurrent(IReadOnlyList<DeviceStatus> devices) =>
        _comparison is not null &&
        devices.Count == 2 &&
        devices.All(device => device.Ready) &&
        string.Equals(_comparedDevicesKey, GetDevicesKey(devices), StringComparison.Ordinal);

    private async Task<PowerShellResult> ReadDeviceStatusWithRetryAsync()
    {
        return await Engine.RunAsync(
            new[] { "-GuiStatusJson" },
            line => HandleEngineLine(line, includeProtocolLines: false),
            _closing.Token,
            TimeSpan.FromSeconds(30));
    }

    private async Task CompareAsync()
    {
        _statusLabel.Text = T(
            "Skaitomi ir lyginami abiejų įrenginių žemėlapių sąrašai...",
            "Reading and comparing map inventories on both devices...");
        ShowWorkProgress();
        SetProgressPercent(55);

        var result = await Engine.RunAsync(
            new[] { "-GuiCompareJson" },
            line => HandleEngineLine(line, includeProtocolLines: false),
            _closing.Token,
            TimeSpan.FromMinutes(15));

        var payload = result.FindPayload(JsonPrefix);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(payload))
        {
            throw new InvalidOperationException(BuildError(result, T("Nepavyko palyginti žemėlapių.", "Could not compare map inventories.")));
        }

        _comparison = JsonSerializer.Deserialize<ComparisonResult>(payload, _jsonOptions)
            ?? throw new InvalidOperationException(T("Gautas netinkamas palyginimo atsakymas.", "The comparison response was invalid."));

        UpdateComparison(_comparison);
        SetProgressPercent(95);
    }

    private async Task SynchronizeAsync()
    {
        if (_busy || _comparison is null || _comparison.Transfers == 0)
        {
            return;
        }

        SetBusy(true, T("Pradedamas sinchronizavimas...", "Starting synchronization..."));
        _timer.Stop();
        var incomingA = _comparison.DeviceA.MissingIncoming + _comparison.DeviceA.NewerIncoming;
        var incomingB = _comparison.DeviceB.MissingIncoming + _comparison.DeviceB.NewerIncoming;
        SetProgressRange(_progressA, incomingA, incomingA == 0 ? 1 : 0);
        SetProgressRange(_progressB, incomingB, incomingB == 0 ? 1 : 0);
        var completedA = 0;
        var completedB = 0;
        AppendLog(T(
            $"Sinchronizuojama {N(_comparison.Transfers)} failų ({FormatBytes(_comparison.TotalBytes)}).",
            $"Synchronizing {N(_comparison.Transfers)} files ({FormatBytes(_comparison.TotalBytes)})."), false);

        try
        {
            var result = await Engine.RunAsync(
                new[] { "-GuiProgress" },
                line =>
                {
                    HandleEngineLine(line, includeProtocolLines: false);
                    if (line.Text.StartsWith(ProgressPrefix, StringComparison.Ordinal))
                    {
                        var info = JsonSerializer.Deserialize<ProgressInfo>(line.Text[ProgressPrefix.Length..], _jsonOptions);
                        if (info is not null)
                        {
                            SafeUi(() =>
                            {
                                if (string.Equals(
                                        info.Destination,
                                        _comparison.DeviceA.Name,
                                        StringComparison.OrdinalIgnoreCase))
                                {
                                    _progressA.Value = Math.Clamp(completedA, 0, _progressA.Maximum);
                                    completedA++;
                                }
                                else if (string.Equals(
                                             info.Destination,
                                             _comparison.DeviceB.Name,
                                             StringComparison.OrdinalIgnoreCase))
                                {
                                    _progressB.Value = Math.Clamp(completedB, 0, _progressB.Maximum);
                                    completedB++;
                                }
                                _statusLabel.Text = $"{N(info.Current)}/{N(info.Total)}: {info.Path} → {info.Destination}";
                            });
                        }
                    }
                },
                _closing.Token);

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(BuildError(result, T("Sinchronizavimas nepavyko.", "Synchronization failed.")));
            }

            var donePayload = result.FindPayload(DonePrefix);
            var done = string.IsNullOrWhiteSpace(donePayload)
                ? null
                : JsonSerializer.Deserialize<DoneInfo>(donePayload, _jsonOptions);

            _progressA.Value = _progressA.Maximum;
            _progressB.Value = _progressB.Maximum;
            AppendLog(T("Sinchronizavimas baigtas sėkmingai.", "Synchronization completed successfully."), false);
            if (done is not null && !string.IsNullOrWhiteSpace(done.Audit))
            {
                AppendLog(T("Auditas: ", "Audit: ") + done.Audit, false);
            }

            MessageBox.Show(
                this,
                T(
                    "Žemėlapiai sinchronizuoti. Iš naujo paleiskite XCTrack abiejuose įrenginiuose.",
                    "Maps are synchronized. Restart XCTrack on both devices."),
                T("Sinchronizavimas baigtas", "Synchronization complete"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            // The application is closing.
        }
        catch (Exception ex)
        {
            AppendLog(T("KLAIDA: ", "ERROR: ") + ex.Message, true);
            MessageBox.Show(this, ex.Message, T("Sinchronizavimo klaida", "Synchronization error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false, T("Atnaujinama būsena po sinchronizavimo...", "Refreshing status after synchronization..."));
        }

        await RefreshAndCompareAsync(showErrors: false, forceCompare: true);
    }

    private async Task RestoreDeviceAsync(int deviceIndex)
    {
        if (_busy || deviceIndex < 0 || deviceIndex >= _lastDevices.Count)
        {
            return;
        }

        var device = _lastDevices[deviceIndex];
        var restoreCompleted = false;
        _timer.Stop();
        SetBusy(true, T("Ieškoma tinkamos atsarginės kopijos...", "Looking for a matching backup..."));
        try
        {
            var listResult = await Engine.RunAsync(
                new[] { "-GuiListRestoresJson", "-RestoreDevicePath", device.Path },
                line => HandleEngineLine(line, includeProtocolLines: false),
                _closing.Token,
                TimeSpan.FromSeconds(45));

            var pointsPayload = listResult.FindPayload(RestorePointsPrefix);
            if (listResult.ExitCode != 0 || string.IsNullOrWhiteSpace(pointsPayload))
            {
                throw new InvalidOperationException(BuildError(
                    listResult,
                    T("Nepavyko nuskaityti atkūrimo kopijų.", "Could not read restore points.")));
            }

            var points = JsonSerializer.Deserialize<List<RestorePointInfo>>(pointsPayload, _jsonOptions) ?? new();
            if (points.Count == 0)
            {
                MessageBox.Show(
                    this,
                    T(
                        "Šiam įrenginiui dar nėra atkūrimo kopijos. Ji bus sukurta po kito sėkmingo sinchronizavimo.",
                        "No restore point exists for this device yet. One will be created by the next successful synchronization."),
                    T("Nėra atkūrimo kopijos", "No restore point"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var point = points[0];
            var restoreDate = point.RunId;
            if (DateTimeOffset.TryParse(
                    point.CreatedUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var created))
            {
                restoreDate = created.ToLocalTime().ToString("g", UiCulture);
            }

            var confirmation = T(
                $"Atkurti {device.DisplayName} pagal {restoreDate} kopiją?{Environment.NewLine}{Environment.NewLine}" +
                $"Iš atsarginės kopijos bus grąžinta failų: {N(point.RestoreBackups)}.{Environment.NewLine}" +
                $"Pridėtų failų prieš pašalinimą bus nukopijuota į vietinį karantiną: {N(point.RemoveAddedFiles)}.{Environment.NewLine}{Environment.NewLine}" +
                "Uždarykite XCTrack ir iki pabaigos neatjunkite įrenginio.",
                $"Restore {device.DisplayName} to the {restoreDate} backup?{Environment.NewLine}{Environment.NewLine}" +
                $"Files restored from backup: {N(point.RestoreBackups)}.{Environment.NewLine}" +
                $"Added files copied to local quarantine before removal: {N(point.RemoveAddedFiles)}.{Environment.NewLine}{Environment.NewLine}" +
                "Close XCTrack and keep the device connected until the restore finishes.");

            if (MessageBox.Show(
                    this,
                    confirmation,
                    T("Patvirtinti atkūrimą", "Confirm restore"),
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                return;
            }

            var restoreProgress = GetDeviceProgressBar(deviceIndex);
            SetProgressRange(restoreProgress, point.TotalOperations);
            _statusLabel.Text = T(
                $"Atkuriamas {device.DisplayName}...",
                $"Restoring {device.DisplayName}...");
            AppendLog(T(
                $"Pradėtas {device.DisplayName} atkūrimas pagal {restoreDate} kopiją.",
                $"Started restoring {device.DisplayName} from the {restoreDate} backup."), false);

            var restoreResult = await Engine.RunAsync(
                new[]
                {
                    "-GuiRestore",
                    "-RestoreDevicePath", device.Path,
                    "-RestoreManifest", point.ManifestPath
                },
                line =>
                {
                    HandleEngineLine(line, includeProtocolLines: false);
                    if (line.Text.StartsWith(RestoreProgressPrefix, StringComparison.Ordinal))
                    {
                        var progress = JsonSerializer.Deserialize<ProgressInfo>(
                            line.Text[RestoreProgressPrefix.Length..],
                            _jsonOptions);
                        if (progress is not null)
                        {
                            SafeUi(() =>
                            {
                                restoreProgress.Maximum = Math.Max(1, progress.Total);
                                // Sent when an operation starts, so Current - 1 are done.
                                restoreProgress.Value = Math.Clamp(
                                    progress.Current - 1,
                                    0,
                                    restoreProgress.Maximum);
                                _statusLabel.Text = $"{N(progress.Current)}/{N(progress.Total)}: {progress.Path}";
                            });
                        }
                    }
                },
                _closing.Token);

            if (restoreResult.ExitCode != 0)
            {
                throw new InvalidOperationException(BuildError(
                    restoreResult,
                    T("Atkūrimas nepavyko.", "Restore failed.")));
            }

            var donePayload = restoreResult.FindPayload(RestoreDonePrefix);
            var done = string.IsNullOrWhiteSpace(donePayload)
                ? null
                : JsonSerializer.Deserialize<RestoreDoneInfo>(donePayload, _jsonOptions);
            restoreProgress.Value = restoreProgress.Maximum;
            restoreCompleted = true;

            var resultMessage = done is null
                ? T("Atkūrimas baigtas.", "Restore completed.")
                : T(
                    $"Atkūrimas baigtas. Atkurta: {N(done.Restored)}, praleista: {N(done.Skipped)}, klaidų: {N(done.Failures)}.{Environment.NewLine}{Environment.NewLine}Auditas: {done.Audit}",
                    $"Restore completed. Restored: {N(done.Restored)}, skipped: {N(done.Skipped)}, failures: {N(done.Failures)}.{Environment.NewLine}{Environment.NewLine}Audit: {done.Audit}");
            AppendLog(resultMessage, false);
            MessageBox.Show(
                this,
                resultMessage + Environment.NewLine + Environment.NewLine +
                T("Iš naujo paleiskite XCTrack.", "Restart XCTrack."),
                T("Atkūrimas baigtas", "Restore complete"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            // The application is closing.
        }
        catch (Exception ex)
        {
            AppendLog(T("ATKŪRIMO KLAIDA: ", "RESTORE ERROR: ") + ex.Message, true);
            MessageBox.Show(
                this,
                ex.Message,
                T("Atkūrimo klaida", "Restore error"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false, restoreCompleted
                ? T("Atkūrimas baigtas.", "Restore completed.")
                : GetWaitingStatus(_lastDevices));
        }

        if (restoreCompleted)
        {
            await RefreshAndCompareAsync(showErrors: false, forceCompare: true);
        }
    }

    private void UpdateDeviceState(IReadOnlyList<DeviceStatus> devices)
    {
        _lastDevices = devices;
        if (IsComparisonCurrent(devices))
        {
            // Keep the file statistics on the cards instead of flashing them
            // back to the plain "connected" view on every background check.
            UpdateComparison(_comparison!);
        }
        else if (devices.Count > 0)
        {
            if (devices[0].Ready)
            {
                ShowReadyDevice(_deviceA, devices[0]);
            }
            else
            {
                _deviceA.SetDetected(
                    devices[0].DisplayName,
                    T("USB / MTP prijungtas", "USB / MTP connected"),
                    T("Tikrinami XCTrack žemėlapių katalogai...", "Checking XCTrack map folders..."));
            }
        }
        else
        {
            _deviceA.SetDisconnected();
        }

        if (IsComparisonCurrent(devices))
        {
            // Card B was updated together with card A above.
        }
        else if (devices.Count > 1)
        {
            if (devices[1].Ready)
            {
                ShowReadyDevice(_deviceB, devices[1]);
            }
            else
            {
                _deviceB.SetDetected(
                    devices[1].DisplayName,
                    T("USB / MTP prijungtas", "USB / MTP connected"),
                    T("Tikrinami XCTrack žemėlapių katalogai...", "Checking XCTrack map folders..."));
            }
        }
        else
        {
            _deviceB.SetDisconnected(T("Laukiama antro USB / MTP įrenginio", "Waiting for the second USB / MTP device"));
        }

        var stateKey = string.Join("|", devices.Select(device => $"{device.DisplayName}:{device.Path}:{device.Ready}"));
        if (!string.Equals(stateKey, _lastLoggedDeviceStateKey, StringComparison.Ordinal))
        {
            if (devices.Count == 1)
            {
                AppendLog(devices[0].Ready
                    ? T(
                        $"{devices[0].DisplayName} prijungtas ir paruoštas. Laukiama antro įrenginio.",
                        $"{devices[0].DisplayName} is connected and ready. Waiting for the second device.")
                    : T(
                        $"{devices[0].DisplayName} USB / MTP prijungtas. Tikrinami XCTrack katalogai.",
                        $"{devices[0].DisplayName} USB / MTP is connected. Checking XCTrack folders."), false);
            }
            else if (devices.Count == 2)
            {
                AppendLog(devices.All(device => device.Ready)
                    ? T(
                        "Abu įrenginiai prijungti ir paruošti.",
                        "Both devices are connected and ready.")
                    : T(
                        "Abu USB / MTP įrenginiai prijungti. Tikrinami XCTrack katalogai.",
                        "Both USB / MTP devices are connected. Checking XCTrack folders."), false);
            }
            else if (devices.Count == 0 && !string.IsNullOrEmpty(_lastLoggedDeviceStateKey))
            {
                AppendLog(T(
                    "Paruošti įrenginiai atjungti. Laukiama USB / MTP įrenginių.",
                    "Ready devices were disconnected. Waiting for USB / MTP devices."), false);
            }

            _lastLoggedDeviceStateKey = stateKey;
        }

        if (!_busy)
        {
            _restoreAButton.Enabled = devices.Count > 0 && devices[0].Ready;
            _restoreBButton.Enabled = devices.Count > 1 && devices[1].Ready;
        }
    }

    private void UpdateComparison(ComparisonResult comparison)
    {
        var statsA = FormatStats(comparison.DeviceA);
        _deviceA.SetConnected(
            comparison.DeviceA.Name,
            comparison.DeviceA.Layout,
            statsA.RoadMaps,
            T(
                $"Gaus: {N(comparison.DeviceA.MissingIncoming + comparison.DeviceA.NewerIncoming)} failus, {FormatBytes(comparison.DeviceA.BytesIncoming)}",
                $"Will receive: {N(comparison.DeviceA.MissingIncoming + comparison.DeviceA.NewerIncoming)} files, {FormatBytes(comparison.DeviceA.BytesIncoming)}"),
            statsA.Terrain);
        var statsB = FormatStats(comparison.DeviceB);
        _deviceB.SetConnected(
            comparison.DeviceB.Name,
            comparison.DeviceB.Layout,
            statsB.RoadMaps,
            T(
                $"Gaus: {N(comparison.DeviceB.MissingIncoming + comparison.DeviceB.NewerIncoming)} failus, {FormatBytes(comparison.DeviceB.BytesIncoming)}",
                $"Will receive: {N(comparison.DeviceB.MissingIncoming + comparison.DeviceB.NewerIncoming)} files, {FormatBytes(comparison.DeviceB.BytesIncoming)}"),
            statsB.Terrain);

        _summaryTitle.Text = comparison.Transfers == 0
            ? T("Abu įrenginiai jau sinchronizuoti", "Both devices are already synchronized")
            : T(
                $"Reikia perkelti {N(comparison.Transfers)} failų ({FormatBytes(comparison.TotalBytes)})",
                $"{N(comparison.Transfers)} files need to be transferred ({FormatBytes(comparison.TotalBytes)})");
        _toALabel.Text = T(
            $"→ {comparison.DeviceA.Name}: {N(comparison.DeviceA.MissingIncoming)} trūksta, {N(comparison.DeviceA.NewerIncoming)} senesni",
            $"→ {comparison.DeviceA.Name}: {N(comparison.DeviceA.MissingIncoming)} missing, {N(comparison.DeviceA.NewerIncoming)} older");
        _toBLabel.Text = T(
            $"→ {comparison.DeviceB.Name}: {N(comparison.DeviceB.MissingIncoming)} trūksta, {N(comparison.DeviceB.NewerIncoming)} senesni",
            $"→ {comparison.DeviceB.Name}: {N(comparison.DeviceB.MissingIncoming)} missing, {N(comparison.DeviceB.NewerIncoming)} older");
        _equalLabel.Text = T($"Vienodi failai: {N(comparison.Same)}", $"Matching files: {N(comparison.Same)}");
        _conflictsLabel.Text = comparison.Conflicts == 0
            ? T("Neaiškių konfliktų: 0", "Unresolved conflicts: 0")
            : T(
                $"Neaiškių konfliktų: {N(comparison.Conflicts)} (bus praleisti)",
                $"Unresolved conflicts: {N(comparison.Conflicts)} (will be skipped)");
        _conflictsLabel.ForeColor = comparison.Conflicts == 0
            ? Color.FromArgb(62, 72, 88)
            : Color.FromArgb(185, 105, 20);

        _syncButton.Enabled = !_busy && comparison.Transfers > 0;
        _syncButton.Text = comparison.Transfers > 0
            ? T($"Sinchronizuoti ({N(comparison.Transfers)})", $"Synchronize ({N(comparison.Transfers)})")
            : T("Sinchronizuota", "Synchronized");
        _statusLabel.Text = comparison.Transfers == 0
            ? T("Palyginimas baigtas – veiksmų nereikia.", "Comparison complete. No action is required.")
            : T("Palyginimas baigtas – galima sinchronizuoti.", "Comparison complete. Ready to synchronize.");
    }

    private void ApplyDemoState(string mode)
    {
        var comparison = new ComparisonResult
        {
            DeviceA = new DeviceComparison
            {
                Name = "AIR3-7.3+ #1",
                Layout = "Android-data",
                FileCount = 5_340,
                RoadMapCount = 48,
                GecCount = 5_292,
                RoadMapBytes = 3_221_225_472,
                GecBytes = 21_474_836_480,
                MissingIncoming = 2,
                NewerIncoming = 1,
                BytesIncoming = 524_288_000
            },
            DeviceB = new DeviceComparison
            {
                Name = "AIR3-7.3+ #2",
                Layout = "Android-data",
                FileCount = 1_094,
                RoadMapCount = 18,
                GecCount = 1_076,
                RoadMapBytes = 1_073_741_824,
                GecBytes = 4_294_967_296,
                MissingIncoming = 4_246,
                NewerIncoming = 3,
                BytesIncoming = 9_032_679_424
            },
            Transfers = 4_252,
            Same = 1_091,
            Conflicts = 0,
            TotalBytes = 9_556_967_424
        };

        _lastDevices = new[]
        {
            new DeviceStatus
            {
                DisplayName = "AIR3-7.3+ #1",
                Name = "AIR3-7.3+",
                Layout = "Android-data",
                Path = "demo-device-a",
                Ready = true
            },
            new DeviceStatus
            {
                DisplayName = "AIR3-7.3+ #2",
                Name = "AIR3-7.3+",
                Layout = "Android-data",
                Path = "demo-device-b",
                Ready = true
            }
        };
        _busy = false;
        _restoreAButton.Enabled = true;
        _restoreBButton.Enabled = true;
        SetProgressPercent(100);
        _comparison = comparison;
        UpdateComparison(comparison);

        switch (mode.ToLowerInvariant())
        {
            case "ready":
                AppendLog(T("Aptikti 2 XCTrack įrenginiai.", "Two XCTrack devices detected."), false);
                AppendLog(T("Palyginimas baigtas: 4 252 failai bus sinchronizuoti.", "Comparison complete: 4,252 files will be synchronized."), false);
                AppendLog(T("Įrenginys #1 gaus 3 failus, įrenginys #2 gaus 4 249 failus.", "Device #1 will receive 3 files; device #2 will receive 4,249 files."), false);
                break;

            case "syncing":
                _comparison = comparison;
                _busy = true;
                _refreshButton.Enabled = false;
                _restoreAButton.Enabled = false;
                _restoreBButton.Enabled = false;
                _syncButton.Enabled = false;
                _syncButton.Text = T("Sinchronizuojama...", "Synchronizing...");
                SetProgressRange(_progressA, 3, 3);
                SetProgressRange(_progressB, 4_249, 1_829);
                _statusLabel.Text = $"{N(1_832)}/{N(4_252)}: gec\\N46E007.gec → AIR3-7.3+ #2";
                AppendLog(T("Sinchronizuojama 4 252 failų (8.9 GB).", "Synchronizing 4,252 files (8.9 GB)."), false);
                AppendLog("BACKUP RoadMap\\Lithuania_ML_DK.map", false);
                AppendLog("COPY 1 831/4 252: gec\\N46E006.gec → AIR3-7.3+ #2", false);
                AppendLog("COPY 1 832/4 252: gec\\N46E007.gec → AIR3-7.3+ #2", false);
                break;

            case "done":
                var completed = new ComparisonResult
                {
                    DeviceA = new DeviceComparison
                    {
                        Name = "AIR3-7.3+ #1",
                        Layout = "Android-data",
                        FileCount = 5_343,
                        RoadMapCount = 51,
                        GecCount = 5_292,
                        RoadMapBytes = 3_745_513_472,
                        GecBytes = 21_474_836_480
                    },
                    DeviceB = new DeviceComparison
                    {
                        Name = "AIR3-7.3+ #2",
                        Layout = "Android-data",
                        FileCount = 5_343,
                        RoadMapCount = 51,
                        GecCount = 5_292,
                        RoadMapBytes = 3_745_513_472,
                        GecBytes = 21_474_836_480
                    },
                    Transfers = 0,
                    Same = 5_343,
                    Conflicts = 0,
                    TotalBytes = 0
                };
                _comparison = completed;
                UpdateComparison(completed);
                SetProgressBarPercent(_progressA, 100);
                SetProgressBarPercent(_progressB, 100);
                AppendLog(T("Sinchronizavimas baigtas sėkmingai.", "Synchronization completed successfully."), false);
                AppendLog(T("Perkelti 4 252 failai; klaidų: 0.", "Transferred 4,252 files; errors: 0."), false);
                AppendLog(T("Auditas išsaugotas Documents\\XCTrackMapSync kataloge.", "Audit saved in the Documents\\XCTrackMapSync folder."), false);
                break;

            default:
                _statusLabel.Text = T("Nežinomas demonstracinis režimas.", "Unknown demonstration mode.");
                break;
        }
    }

    private void ResetComparison(string message)
    {
        _comparison = null;
        _comparedDevicesKey = string.Empty;
        _summaryTitle.Text = T("Laukiama abiejų įrenginių", "Waiting for both devices");
        _toALabel.Text = string.Empty;
        _toBLabel.Text = string.Empty;
        _equalLabel.Text = string.Empty;
        _conflictsLabel.Text = string.Empty;
        _syncButton.Enabled = false;
        _syncButton.Text = T("Sinchronizuoti", "Synchronize");
        if (!_busy)
        {
            SetProgressBarPercent(_progressA, 0);
            SetProgressBarPercent(_progressB, 0);
        }
        _statusLabel.Text = message;
    }

    private void SetProgressPercent(int percent, int? deviceIndex = null)
    {
        if (_quietRefresh)
        {
            return;
        }

        if (deviceIndex is null or 0)
        {
            SetProgressBarPercent(_progressA, _lastDevices.Count > 0 ? percent : 0);
        }
        if (deviceIndex is null or 1)
        {
            SetProgressBarPercent(_progressB, _lastDevices.Count > 1 ? percent : 0);
        }
    }

    private static void SetProgressBarPercent(ProgressBar progressBar, int percent)
    {
        progressBar.Style = ProgressBarStyle.Continuous;
        progressBar.Minimum = 0;
        progressBar.Maximum = 100;
        progressBar.Value = Math.Clamp(percent, 0, 100);
    }

    private ProgressBar GetDeviceProgressBar(int deviceIndex) =>
        deviceIndex == 0 ? _progressA : _progressB;

    private static void SetProgressRange(ProgressBar progressBar, int maximum, int value = 0)
    {
        progressBar.Style = ProgressBarStyle.Continuous;
        progressBar.Minimum = 0;
        progressBar.Maximum = Math.Max(1, maximum);
        progressBar.Value = Math.Clamp(value, 0, progressBar.Maximum);
    }

    private void SetBusy(bool busy, string status)
    {
        var wasBusy = _busy;
        _busy = busy;
        _refreshButton.Enabled = !busy;
        _languageCombo.Enabled = !busy;
        _restoreAButton.Enabled = !busy && _lastDevices.Count > 0 && _lastDevices[0].Ready;
        _restoreBButton.Enabled = !busy && _lastDevices.Count > 1 && _lastDevices[1].Ready;
        _syncButton.Enabled = !busy && _comparison is { Transfers: > 0 };
        _statusLabel.Text = status;
        UseWaitCursor = busy && !_quietRefresh;
        if (busy && !wasBusy)
        {
            SetProgressPercent(0);
        }
        else if (!busy && wasBusy && !_quietRefresh)
        {
            if (_lastDevices.Count > 0)
            {
                _progressA.Value = _progressA.Maximum;
            }
            if (_lastDevices.Count > 1)
            {
                _progressB.Value = _progressB.Maximum;
            }
        }
    }

    private void HandleEngineLine(PowerShellLine line, bool includeProtocolLines)
    {
        if (line.Text.EndsWith(
                "Searching for connected XCTrack MTP devices...",
                StringComparison.Ordinal))
        {
            return;
        }

        if (!includeProtocolLines &&
            (line.Text.StartsWith(JsonPrefix, StringComparison.Ordinal) ||
             line.Text.StartsWith(ProgressPrefix, StringComparison.Ordinal) ||
             line.Text.StartsWith(DonePrefix, StringComparison.Ordinal) ||
             line.Text.StartsWith(RestorePointsPrefix, StringComparison.Ordinal) ||
             line.Text.StartsWith(RestoreProgressPrefix, StringComparison.Ordinal) ||
             line.Text.StartsWith(RestoreDonePrefix, StringComparison.Ordinal)))
        {
            return;
        }

        AppendLog(line.Text, line.IsError);
    }

    private void AppendLog(string text, bool isError)
    {
        SafeUi(() =>
        {
            _log.SelectionStart = _log.TextLength;
            _log.SelectionColor = isError ? Color.FromArgb(180, 45, 45) : Color.FromArgb(74, 84, 98);
            _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
        });
    }

    private void ApplyLanguage()
    {
        _titleLabel.Text = T("XCTrack žemėlapių sinchronizavimas", "XCTrack map synchronization");
        _subtitleLabel.Text = T(
            "Prijunkite ir atrakinkite abu AIR³ įrenginius USB File transfer režimu.",
            "Connect and unlock both AIR³ devices in USB File transfer mode.");
        _refreshButton.Text = T("Atnaujinti", "Refresh");
        _restoreAButton.Text = T("Atkurti A", "Restore A");
        _restoreBButton.Text = T("Atkurti B", "Restore B");
        _footerLabel.Text = T(
            "Sinchronizuojant failai netrinami • Keičiami failai išsaugomi • Kuriamas auditas",
            "Synchronization never deletes files • Replaced files are backed up • An audit is created");

        _deviceA.SetLanguage(
            T("Įrenginys A", "Device A"),
            T("Neprijungtas", "Disconnected"),
            T("Laukiama USB / MTP įrenginio", "Waiting for a USB / MTP device"),
            T("Atrakinkite įrenginį ir pasirinkite File transfer.", "Unlock the device and select File transfer."),
            T("Prijungtas ir paruoštas", "Connected and ready"));
        _deviceB.SetLanguage(
            T("Įrenginys B", "Device B"),
            T("Neprijungtas", "Disconnected"),
            T("Laukiama USB / MTP įrenginio", "Waiting for a USB / MTP device"),
            T("Atrakinkite įrenginį ir pasirinkite File transfer.", "Unlock the device and select File transfer."),
            T("Prijungtas ir paruoštas", "Connected and ready"));

        var desiredIndex = _language == UiLanguage.Lithuanian ? 0 : 1;
        if (_languageCombo.SelectedIndex != desiredIndex)
        {
            _languageCombo.SelectedIndex = desiredIndex;
        }

        UpdateDeviceState(_lastDevices);
        if (_comparison is not null && !_busy)
        {
            UpdateComparison(_comparison);
        }
        else if (!_busy)
        {
            ResetComparison(GetWaitingStatus(_lastDevices));
        }
    }

    private string GetWaitingStatus(IReadOnlyList<DeviceStatus> devices)
    {
        if (devices.Count > 0 && devices.Any(device => !device.Ready))
        {
            return T(
                "AIR³ USB / MTP prijungtas – tikrinami XCTrack katalogai.",
                "AIR³ USB / MTP is connected. Checking XCTrack folders.");
        }

        return devices.Count switch
        {
            0 => T(
                "Neaptiktas nė vienas paruoštas XCTrack įrenginys.",
                "No ready XCTrack device was detected."),
            1 => T(
                "Pirmas įrenginys paruoštas – prijunkite antrą.",
                "The first device is ready. Connect the second device."),
            > 2 => T(
                "Aptikta daugiau nei du XCTrack įrenginiai. Atjunkite nereikalingus.",
                "More than two XCTrack devices were found. Disconnect the extra devices."),
            _ => T("Abu įrenginiai paruošti.", "Both devices are ready.")
        };
    }

    private string T(string lithuanian, string english) =>
        _language == UiLanguage.Lithuanian ? lithuanian : english;

    private CultureInfo UiCulture => _language == UiLanguage.Lithuanian
        ? CultureInfo.GetCultureInfo("lt-LT")
        : CultureInfo.GetCultureInfo("en-US");

    private string N(long value) => value.ToString("N0", UiCulture);

    private static UiLanguage LoadLanguage(string? languageOverride)
    {
        if (!string.IsNullOrWhiteSpace(languageOverride))
        {
            return languageOverride.StartsWith("lt", StringComparison.OrdinalIgnoreCase)
                ? UiLanguage.Lithuanian
                : UiLanguage.English;
        }

        try
        {
            var path = GetLanguageFilePath();
            if (File.Exists(path))
            {
                return File.ReadAllText(path).Trim().StartsWith("lt", StringComparison.OrdinalIgnoreCase)
                    ? UiLanguage.Lithuanian
                    : UiLanguage.English;
            }
        }
        catch
        {
            // Fall back to the Windows UI culture.
        }

        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("lt", StringComparison.OrdinalIgnoreCase)
            ? UiLanguage.Lithuanian
            : UiLanguage.English;
    }

    private void SaveLanguage()
    {
        try
        {
            var path = GetLanguageFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, _language == UiLanguage.Lithuanian ? "lt" : "en");
        }
        catch
        {
            // Language selection still applies to the current session.
        }
    }

    private static string GetLanguageFilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XCTrackMapSync",
        "language.txt");

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);

        if (message.Msg != WmDeviceChange ||
            !string.IsNullOrWhiteSpace(_demoMode) ||
            _closing.IsCancellationRequested)
        {
            return;
        }

        var change = message.WParam.ToInt32();
        if (change == DbtDeviceArrival || change == DbtDeviceRemoveComplete)
        {
            // Restart the one-shot timer so Windows can finish mounting or
            // removing the MTP device before the folder scan begins.
            _timer.Stop();
            _timer.Start();
        }
    }

    private void SafeUi(Action action)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(action);
        }
        else
        {
            action();
        }
    }

    private static string BuildError(PowerShellResult result, string fallback)
    {
        var errors = result.Lines.Where(line => line.IsError).Select(line => line.Text).ToArray();
        if (errors.Length > 0)
        {
            return string.Join(Environment.NewLine, errors.TakeLast(5));
        }

        var meaningful = result.Lines
            .Select(line => line.Text)
            .Where(text => !text.StartsWith("[", StringComparison.Ordinal))
            .TakeLast(3)
            .ToArray();
        return meaningful.Length > 0 ? string.Join(Environment.NewLine, meaningful) : fallback;
    }

    private string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return value.ToString("0.##", UiCulture) + " " + units[unit];
    }

    private static void StylePrimaryButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = Color.FromArgb(36, 99, 235);
        button.ForeColor = Color.White;
        button.Font = new Font("Segoe UI Semibold", 10f);
        button.Cursor = Cursors.Hand;
    }

    private static void StyleSecondaryButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Color.FromArgb(205, 212, 223);
        button.FlatAppearance.BorderSize = 1;
        button.BackColor = Color.White;
        button.ForeColor = Color.FromArgb(55, 65, 81);
        button.Font = new Font("Segoe UI Semibold", 9.5f);
        button.Cursor = Cursors.Hand;
    }
}
