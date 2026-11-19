using System.Text.Json;
using System.Globalization;

namespace XCTrackMapSync;

internal sealed class MainForm : Form
{
    private const string JsonPrefix = "XCT_JSON:";
    private const string ProgressPrefix = "XCT_PROGRESS:";
    private const string DonePrefix = "XCT_DONE:";

    private readonly PowerShellEngine _engine = new();
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
    private readonly ProgressBar _progress = new();
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
    private string _lastLoggedDeviceStateKey = string.Empty;
    private UiLanguage _language;

    public MainForm(string? demoMode = null, string? languageOverride = null)
    {
        _demoMode = demoMode;
        _language = LoadLanguage(languageOverride);
        Text = "XCTrack Map Sync";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(880, 680);
        Size = new Size(960, 760);
        BackColor = Color.FromArgb(246, 248, 251);
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;

        BuildInterface();
        ApplyLanguage();

        _timer.Interval = 10_000;
        _timer.Tick += async (_, _) =>
        {
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
            _timer.Start();
            await RefreshAndCompareAsync(showErrors: true);
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
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 188));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 162));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));

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
        _refreshButton.Click += async (_, _) => await RefreshAndCompareAsync(showErrors: true);
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
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        _progress.Dock = DockStyle.Fill;
        _progress.Height = 10;
        _progress.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _progress.Margin = new Padding(0, 23, 20, 23);

        _syncButton.Enabled = false;
        _syncButton.Size = new Size(210, 42);
        _syncButton.Anchor = AnchorStyles.Right;
        _syncButton.Margin = Padding.Empty;
        StylePrimaryButton(_syncButton);
        _syncButton.Click += async (_, _) => await SynchronizeAsync();
        actions.Controls.Add(_progress, 0, 0);
        actions.Controls.Add(_syncButton, 1, 0);

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

    private async Task RefreshAndCompareAsync(bool showErrors)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true, T("Tikrinami prijungti įrenginiai...", "Checking connected devices..."));
        try
        {
            var result = await ReadDeviceStatusWithRetryAsync();

            var payload = result.FindPayload(JsonPrefix);
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(payload))
            {
                throw new InvalidOperationException(BuildError(result, T("Nepavyko nuskaityti įrenginių.", "Could not read connected devices.")));
            }

            var devices = JsonSerializer.Deserialize<List<DeviceStatus>>(payload, _jsonOptions) ?? new();
            UpdateDeviceState(devices);

            if (devices.Count == 2)
            {
                await CompareAsync();
            }
            else if (devices.Count > 2)
            {
                ResetComparison(T(
                    "Aptikta daugiau nei du XCTrack įrenginiai. Atjunkite nereikalingus.",
                    "More than two XCTrack devices were found. Disconnect the extra devices."));
            }
            else
            {
                ResetComparison(GetWaitingStatus(devices.Count));
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
        }
    }

    private async Task<PowerShellResult> ReadDeviceStatusWithRetryAsync()
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                return await _engine.RunAsync(
                    new[] { "-GuiStatusJson" },
                    line => HandleEngineLine(line, includeProtocolLines: false),
                    _closing.Token,
                    TimeSpan.FromSeconds(12));
            }
            catch (TimeoutException) when (attempt == 1)
            {
                var message = T(
                    "MTP patikra užtruko. Bandoma dar kartą...",
                    "The MTP check timed out. Retrying...");
                _statusLabel.Text = message;
                AppendLog(message, true);
                await Task.Delay(500, _closing.Token);
            }
        }

        throw new TimeoutException("The MTP check timed out twice.");
    }

    private async Task CompareAsync()
    {
        _statusLabel.Text = T(
            "Skaitomi ir lyginami abiejų įrenginių žemėlapių sąrašai...",
            "Reading and comparing map inventories on both devices...");
        _progress.Style = ProgressBarStyle.Marquee;

        var result = await _engine.RunAsync(
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
        _progress.Style = ProgressBarStyle.Blocks;
        _progress.Value = 0;
    }

    private async Task SynchronizeAsync()
    {
        if (_busy || _comparison is null || _comparison.Transfers == 0)
        {
            return;
        }

        SetBusy(true, T("Pradedamas sinchronizavimas...", "Starting synchronization..."));
        _timer.Stop();
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Minimum = 0;
        _progress.Maximum = Math.Max(1, _comparison.Transfers);
        _progress.Value = 0;
        AppendLog(T(
            $"Sinchronizuojama {N(_comparison.Transfers)} failų ({FormatBytes(_comparison.TotalBytes)}).",
            $"Synchronizing {N(_comparison.Transfers)} files ({FormatBytes(_comparison.TotalBytes)})."), false);

        try
        {
            var result = await _engine.RunAsync(
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
                                _progress.Maximum = Math.Max(1, info.Total);
                                _progress.Value = Math.Clamp(info.Current - 1, 0, _progress.Maximum);
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

            _progress.Value = _progress.Maximum;
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
            _timer.Start();
        }

        await RefreshAndCompareAsync(showErrors: false);
    }

    private void UpdateDeviceState(IReadOnlyList<DeviceStatus> devices)
    {
        _lastDevices = devices;
        if (devices.Count > 0)
        {
            _deviceA.SetConnected(devices[0].DisplayName, devices[0].Layout, devices[0].Layout);
        }
        else
        {
            _deviceA.SetDisconnected();
        }

        if (devices.Count > 1)
        {
            _deviceB.SetConnected(devices[1].DisplayName, devices[1].Layout, devices[1].Layout);
        }
        else
        {
            _deviceB.SetDisconnected(T("Laukiama antro USB / MTP įrenginio", "Waiting for the second USB / MTP device"));
        }

        var stateKey = string.Join("|", devices.Select(device => $"{device.DisplayName}:{device.Path}"));
        if (!string.Equals(stateKey, _lastLoggedDeviceStateKey, StringComparison.Ordinal))
        {
            if (devices.Count == 1)
            {
                AppendLog(T(
                    $"{devices[0].DisplayName} prijungtas ir paruoštas. Laukiama antro įrenginio.",
                    $"{devices[0].DisplayName} is connected and ready. Waiting for the second device."), false);
            }
            else if (devices.Count == 2)
            {
                AppendLog(T(
                    "Abu įrenginiai prijungti ir paruošti.",
                    "Both devices are connected and ready."), false);
            }
            else if (devices.Count == 0 && !string.IsNullOrEmpty(_lastLoggedDeviceStateKey))
            {
                AppendLog(T(
                    "Paruošti įrenginiai atjungti. Laukiama USB / MTP įrenginių.",
                    "Ready devices were disconnected. Waiting for USB / MTP devices."), false);
            }

            _lastLoggedDeviceStateKey = stateKey;
        }
    }

    private void UpdateComparison(ComparisonResult comparison)
    {
        _deviceA.SetConnected(
            comparison.DeviceA.Name,
            comparison.DeviceA.Layout,
            T(
                $"Kelių (RoadMap): {N(comparison.DeviceA.RoadMapCount)} • Reljefo (GEC): {N(comparison.DeviceA.GecCount)}",
                $"Road maps: {N(comparison.DeviceA.RoadMapCount)} • Terrain (GEC): {N(comparison.DeviceA.GecCount)}"),
            T(
                $"Gaus: {N(comparison.DeviceA.MissingIncoming + comparison.DeviceA.NewerIncoming)}",
                $"Will receive: {N(comparison.DeviceA.MissingIncoming + comparison.DeviceA.NewerIncoming)}"));
        _deviceB.SetConnected(
            comparison.DeviceB.Name,
            comparison.DeviceB.Layout,
            T(
                $"Kelių (RoadMap): {N(comparison.DeviceB.RoadMapCount)} • Reljefo (GEC): {N(comparison.DeviceB.GecCount)}",
                $"Road maps: {N(comparison.DeviceB.RoadMapCount)} • Terrain (GEC): {N(comparison.DeviceB.GecCount)}"),
            T(
                $"Gaus: {N(comparison.DeviceB.MissingIncoming + comparison.DeviceB.NewerIncoming)}",
                $"Will receive: {N(comparison.DeviceB.MissingIncoming + comparison.DeviceB.NewerIncoming)}"));

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
                MissingIncoming = 4_246,
                NewerIncoming = 3,
                BytesIncoming = 9_032_679_424
            },
            Transfers = 4_252,
            Same = 1_091,
            Conflicts = 0,
            TotalBytes = 9_556_967_424
        };

        _busy = false;
        _progress.Style = ProgressBarStyle.Continuous;
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
                _syncButton.Enabled = false;
                _syncButton.Text = T("Sinchronizuojama...", "Synchronizing...");
                _progress.Minimum = 0;
                _progress.Maximum = comparison.Transfers;
                _progress.Value = 1_832;
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
                        GecCount = 5_292
                    },
                    DeviceB = new DeviceComparison
                    {
                        Name = "AIR3-7.3+ #2",
                        Layout = "Android-data",
                        FileCount = 5_343,
                        RoadMapCount = 51,
                        GecCount = 5_292
                    },
                    Transfers = 0,
                    Same = 5_343,
                    Conflicts = 0,
                    TotalBytes = 0
                };
                _comparison = completed;
                UpdateComparison(completed);
                _progress.Minimum = 0;
                _progress.Maximum = 1;
                _progress.Value = 1;
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
        _summaryTitle.Text = T("Laukiama abiejų įrenginių", "Waiting for both devices");
        _toALabel.Text = string.Empty;
        _toBLabel.Text = string.Empty;
        _equalLabel.Text = string.Empty;
        _conflictsLabel.Text = string.Empty;
        _syncButton.Enabled = false;
        _syncButton.Text = T("Sinchronizuoti", "Synchronize");
        _progress.Style = ProgressBarStyle.Blocks;
        _progress.Value = 0;
        _statusLabel.Text = message;
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        _refreshButton.Enabled = !busy;
        _languageCombo.Enabled = !busy;
        _syncButton.Enabled = !busy && _comparison is { Transfers: > 0 };
        _statusLabel.Text = status;
        UseWaitCursor = busy;
        if (!busy && _progress.Style == ProgressBarStyle.Marquee)
        {
            _progress.Style = ProgressBarStyle.Blocks;
        }
    }

    private void HandleEngineLine(PowerShellLine line, bool includeProtocolLines)
    {
        if (!includeProtocolLines &&
            (line.Text.StartsWith(JsonPrefix, StringComparison.Ordinal) ||
             line.Text.StartsWith(ProgressPrefix, StringComparison.Ordinal) ||
             line.Text.StartsWith(DonePrefix, StringComparison.Ordinal)))
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
        _footerLabel.Text = T(
            "Failai netrinami • Keičiami failai išsaugomi atsarginėje kopijoje • Kuriamas auditas",
            "Files are never deleted • Replaced files are backed up • An audit is created");

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
            ResetComparison(GetWaitingStatus(_lastDevices.Count));
        }
    }

    private string GetWaitingStatus(int deviceCount)
    {
        return deviceCount switch
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
