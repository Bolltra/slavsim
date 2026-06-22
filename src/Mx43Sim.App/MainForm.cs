using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows.Forms;
using Mx43Sim.Core.Cfg;
using Mx43Sim.Core.Domain;
using Mx43Sim.Core.Modbus;
using Mx43Sim.Core.Sim;

namespace Mx43Sim.App;

public sealed class MainForm : Form
{
    private readonly Mx43RegisterStore _store = new();
    private readonly Mx43Simulator _sim;
    private Mx43ModbusServer? _server;
    private Mx43Config? _config;
    private bool _syncingMeasurementEditor;
    private bool _updatingGrid;

    private readonly TextBox _log = new();
    private readonly DataGridView _grid = new();
    private readonly Button _btnLoadCfg = new() { Text = "Ladda .cfg..." };
    private readonly Button _btnStart = new() { Text = "Starta server", Enabled = false };
    private readonly Button _btnStop = new() { Text = "Stoppa server", Enabled = false };
    private readonly NumericUpDown _numPort = new() { Minimum = 1, Maximum = 65535, Value = 502 };
    private readonly Label _lblIp = new() { Text = "IP: 127.0.0.1", AutoSize = true };
    private readonly Label _lblStatus = new() { Text = "Inget laddat", AutoSize = true };
    private readonly Label _lblPort = new() { Text = "Port:", AutoSize = true };

    // Right-hand editor for the selected detector
    private readonly GroupBox _grpEditor = new() { Text = "Detektor", Width = 360, Dock = DockStyle.Right };
    private readonly Label _lblSel = new() { Text = "— välj detektor —", AutoSize = true };
    private readonly Label _lblSelGas = new() { AutoSize = true };
    private readonly Label _lblSelRange = new() { AutoSize = true };
    private readonly Label _lblSelThresh = new() { AutoSize = true };
    private readonly NumericUpDown _numMeas = new() { Minimum = -32768, Maximum = 32767, Value = 0 };
    private readonly TrackBar _trkMeas = new() { Minimum = -32768, Maximum = 32767, TickFrequency = 1000 };
    private readonly Button _btnApply = new() { Text = "Sätt värde (larm räknas ut)" };
    private readonly Button _btnClear = new() { Text = "Nollställ larm" };
    private readonly Label _lblAlarms = new() { Text = "Aktiva larm: —", AutoSize = true };

    public MainForm()
    {
        _sim = new Mx43Simulator(_store);
        Text = "MX43 Simulator";
        Size = new Size(1300, 720);
        StartPosition = FormStartPosition.CenterScreen;

        // Top toolbar
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 50, Padding = new Padding(8) };
        top.Controls.AddRange(new Control[] {
            _btnLoadCfg, _lblIp, _lblPort, _numPort, _btnStart, _btnStop, _lblStatus
        });
        Controls.Add(top);

        // Right editor
        BuildEditor();
        Controls.Add(_grpEditor);

        // Center: grid + log
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 450 };
        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.ReadOnly = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.Columns.Add("addr", "Mät-adress");
        _grid.Columns.Add("line", "Line");
        _grid.Columns.Add("det", "Det");
        _grid.Columns.Add("label", "Label");
        _grid.Columns.Add("gas", "Gas");
        _grid.Columns.Add("unit", "Enhet");
        _grid.Columns.Add("range", "Range");
        _grid.Columns.Add("format", "Format");
        _grid.Columns.Add("inst1", "Alarm 1");
        _grid.Columns.Add("inst2", "Alarm 2");
        _grid.Columns.Add("inst3", "Alarm 3");
        _grid.Columns.Add("under", "Under");
        _grid.Columns.Add("over", "Över");
        _grid.Columns.Add("oor", "OOR");
        _grid.Columns.Add("meas", "Mätning");
        _grid.Columns.Add("alarms", "Aktiva larm");
        var addrColumn = _grid.Columns["addr"] ?? throw new InvalidOperationException("Grid address column missing.");
        addrColumn.ReadOnly = true;
        addrColumn.DefaultCellStyle.BackColor = SystemColors.Control;
        split.Panel1.Controls.Add(_grid);
        _log.Dock = DockStyle.Fill;
        _log.Multiline = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.ReadOnly = true;
        _log.BackColor = Color.Black;
        _log.ForeColor = Color.LightGreen;
        _log.Font = new Font(FontFamily.GenericMonospace, 9);
        split.Panel2.Controls.Add(_log);
        Controls.Add(split);
        split.BringToFront();

        // Wire up events
        _btnLoadCfg.Click += (_, _) => LoadCfg();
        _btnStart.Click += async (_, _) => await StartServerAsync();
        _btnStop.Click += async (_, _) => await StopServerAsync();
        _btnApply.Click += (_, _) => ApplyMeasurement();
        _btnClear.Click += (_, _) => ClearAlarms();
        _trkMeas.Scroll += (_, _) => SyncMeasurementNumberFromTrackBar();
        _numMeas.ValueChanged += (_, _) => SyncTrackBarFromMeasurementNumber();
        _grid.SelectionChanged += (_, _) => OnSelectionChanged();
        _grid.CellValidating += OnGridCellValidating;
        _grid.CellEndEdit += OnGridCellEndEdit;
        _grid.DataError += (_, e) => { e.ThrowException = false; };

        // Self-update check (fire-and-forget)
        Shown += async (_, _) => await SelfUpdater.CheckForUpdateAsync(this);
    }

    private void BuildEditor()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Padding = new Padding(8) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(_lblSel, 0, 0);       layout.SetColumnSpan(_lblSel, 2);
        layout.Controls.Add(_lblSelGas, 0, 1);    layout.SetColumnSpan(_lblSelGas, 2);
        layout.Controls.Add(_lblSelRange, 0, 2);  layout.SetColumnSpan(_lblSelRange, 2);
        layout.Controls.Add(_lblSelThresh, 0, 3); layout.SetColumnSpan(_lblSelThresh, 2);
        layout.Controls.Add(new Label { Text = "Mätvärde:", AutoSize = true }, 0, 4);
        layout.Controls.Add(_numMeas, 1, 4);
        layout.Controls.Add(_trkMeas, 0, 5);       layout.SetColumnSpan(_trkMeas, 2);
        layout.Controls.Add(_btnApply, 0, 6);
        layout.Controls.Add(_btnClear, 1, 6);
        layout.Controls.Add(_lblAlarms, 0, 7);    layout.SetColumnSpan(_lblAlarms, 2);

        _grpEditor.Controls.Add(layout);
    }

    private void Log(string msg)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => Log(msg))); return; }
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\r\n");
    }

    private void LoadCfg()
    {
        using var ofd = new OpenFileDialog { Filter = "MX43 config|*.cfg|All|*.*" };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            _config = new Mx43CfgParser(ofd.FileName).Parse();
            _sim.Load(_config, AddressList.Default);
            _lblStatus.Text = $"Cfg: {Path.GetFileName(ofd.FileName)} — {_config.Sensors.Count} sensorer, {_config.Zones.Count} zoner, {_config.Modules.Count} moduler";
            _btnStart.Enabled = true;
            PopulateGrid();
            Log($"Loaded {_config.Sensors.Count} sensors, {_config.OnBoardRelays.Count} on-board relays, {_config.Modules.Count} modules, {_config.Zones.Count} zones");
            if (_config.Modules.Count > 0)
            {
                foreach (var m in _config.Modules)
                    Log($"  module {m.Tag} = {m.Channels.Count} channels");
            }
            _grid.ClearSelection();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Kunde inte läsa cfg: " + ex.Message, "Fel", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void PopulateGrid()
    {
        _updatingGrid = true;
        _grid.Rows.Clear();
        try
        {
            if (_config is null) return;
            foreach (var s in _config.Sensors)
            {
                int rowIndex = _grid.Rows.Add();
                var row = _grid.Rows[rowIndex];
                row.Tag = s;
                RefreshGridRow(row, s);
            }
        }
        finally
        {
            _updatingGrid = false;
        }
    }

    private void OnSelectionChanged()
    {
        var row = CurrentGridRow();
        if (row is null) { _grpEditor.Enabled = false; return; }
        _grpEditor.Enabled = true;
        var s = row.Tag as Sensor;
        if (s is null) return;
        RefreshDetectorEditor(row, s);
    }

    private void RefreshDetectorEditor(DataGridViewRow row, Sensor s)
    {
        _lblSel.Text = $"Detektor L{s.Line}D{s.Detector} — {s.Label}";
        _lblSelGas.Text = $"Gas: {s.ShortGasName}   Enhet: {s.Unit}";
        _lblSelRange.Text = $"Range: {s.Range}    DisplayFormat: {s.DisplayFormat}";
        _lblSelThresh.Text = $"Tröskelvärden:  A1={s.Thresholds.Inst1}   A2={s.Thresholds.Inst2}   A3={s.Thresholds.Inst3}   " +
                             $"Under={s.Thresholds.Underscale}   Över={s.Thresholds.Overscale}   OOR={s.Thresholds.OutOfRange}";
        var d = _sim.Store.Detectors[s.Index];
        ConfigureMeasurementEditorRange(s, d.Measurement);
        _lblAlarms.Text = "Aktiva larm: " + d.ActiveAlarms;
        _lblAlarms.ForeColor = d.ActiveAlarms == AlarmBits.None ? Color.Black : Color.Red;
    }

    private void ApplyMeasurement()
    {
        var row = CurrentGridRow();
        if (row is null || row.Tag is not Sensor s) return;
        int line = s.Line;
        int det = s.Detector;
        short v = (short)_numMeas.Value;
        _sim.SetMeasurement(line, det, v);
        // Refresh the grid row + the right-pane summary
        var d = _sim.Store.Detectors[(line - 1) * 32 + (det - 1)];
        row.Cells["meas"].Value = d.Measurement;
        row.Cells["alarms"].Value = d.ActiveAlarms.ToString();
        row.DefaultCellStyle.BackColor = d.ActiveAlarms == AlarmBits.None ? Color.Empty : Color.MistyRose;
        _lblAlarms.Text = "Aktiva larm: " + d.ActiveAlarms;
        _lblAlarms.ForeColor = d.ActiveAlarms == AlarmBits.None ? Color.Black : Color.Red;
        Log($"L{line}D{det} = {v}  alarms=0x{(ushort)d.ActiveAlarms:X4}");
    }

    private void ClearAlarms()
    {
        var row = CurrentGridRow();
        if (row is null || row.Tag is not Sensor s) return;
        int line = s.Line;
        int det = s.Detector;
        _sim.SetAlarm(line, det, AlarmBits.None);
        row.Cells["alarms"].Value = AlarmBits.None.ToString();
        row.DefaultCellStyle.BackColor = Color.Empty;
        _lblAlarms.Text = "Aktiva larm: None";
        _lblAlarms.ForeColor = Color.Black;
        Log($"L{line}D{det}: alarms cleared");
    }

    private DataGridViewRow? CurrentGridRow()
    {
        if (_grid.CurrentRow is not null && !_grid.CurrentRow.IsNewRow) return _grid.CurrentRow;
        return _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0] : null;
    }

    private void RefreshGridRow(DataGridViewRow row, Sensor s)
    {
        _updatingGrid = true;
        try
        {
            var d = _sim.Store.Detectors[s.Index];
            row.Cells["addr"].Value = Mx43AddressMap.MeasurementRegFor(s.Line, s.Detector);
            row.Cells["line"].Value = s.Line;
            row.Cells["det"].Value = s.Detector;
            row.Cells["label"].Value = s.Label;
            row.Cells["gas"].Value = s.ShortGasName;
            row.Cells["unit"].Value = s.Unit;
            row.Cells["range"].Value = s.Range;
            row.Cells["format"].Value = s.DisplayFormat;
            row.Cells["inst1"].Value = s.Thresholds.Inst1;
            row.Cells["inst2"].Value = s.Thresholds.Inst2;
            row.Cells["inst3"].Value = s.Thresholds.Inst3;
            row.Cells["under"].Value = s.Thresholds.Underscale;
            row.Cells["over"].Value = s.Thresholds.Overscale;
            row.Cells["oor"].Value = s.Thresholds.OutOfRange;
            row.Cells["meas"].Value = d.Measurement;
            row.Cells["alarms"].Value = d.ActiveAlarms.ToString();
            row.DefaultCellStyle.BackColor = d.ActiveAlarms == AlarmBits.None ? Color.Empty : Color.MistyRose;
        }
        finally
        {
            _updatingGrid = false;
        }
    }

    private void ConfigureMeasurementEditorRange(Sensor s, short value)
    {
        int min = new[] { -100, value, s.Thresholds.Underscale - 50 }.Min();
        int max = new[] { 100, value, s.Range, s.Thresholds.Overscale + 50, s.Thresholds.OutOfRange + 50, s.Thresholds.Fault + 50 }.Max();
        min = Math.Max(short.MinValue, min);
        max = Math.Min(short.MaxValue, max);
        if (min >= max) { min = short.MinValue; max = short.MaxValue; }

        _syncingMeasurementEditor = true;
        try
        {
            _trkMeas.Minimum = min;
            _trkMeas.Maximum = max;
            _numMeas.Minimum = short.MinValue;
            _numMeas.Maximum = short.MaxValue;
            _numMeas.Value = value;
            _trkMeas.Value = Math.Clamp((int)value, _trkMeas.Minimum, _trkMeas.Maximum);
        }
        finally
        {
            _syncingMeasurementEditor = false;
        }
    }

    private void SyncMeasurementNumberFromTrackBar()
    {
        if (_syncingMeasurementEditor) return;
        _syncingMeasurementEditor = true;
        try
        {
            _numMeas.Value = Math.Clamp(_trkMeas.Value, (int)_numMeas.Minimum, (int)_numMeas.Maximum);
        }
        finally
        {
            _syncingMeasurementEditor = false;
        }
    }

    private void SyncTrackBarFromMeasurementNumber()
    {
        if (_syncingMeasurementEditor) return;
        _syncingMeasurementEditor = true;
        try
        {
            int value = (int)_numMeas.Value;
            _trkMeas.Value = Math.Clamp(value, _trkMeas.Minimum, _trkMeas.Maximum);
        }
        finally
        {
            _syncingMeasurementEditor = false;
        }
    }

    private void OnGridCellValidating(object? sender, DataGridViewCellValidatingEventArgs e)
    {
        if (_updatingGrid || e.RowIndex < 0) return;
        string column = _grid.Columns[e.ColumnIndex].Name;
        if (column == "addr") return;

        string text = Convert.ToString(e.FormattedValue)?.Trim() ?? "";
        if (column == "alarms")
        {
            if (!TryParseAlarmBits(text, out _)) RejectGridEdit(e, "Aktiva larm måste vara t.ex. None, Inst1, Inst1, Inst2 eller 0x0008.");
            return;
        }

        if (!IsIntegerColumn(column)) return;
        if (!int.TryParse(text, out int value))
        {
            RejectGridEdit(e, "Värdet måste vara ett heltal.");
            return;
        }

        if (column == "line" && (value < 1 || value > 8)) RejectGridEdit(e, "Line måste vara 1..8.");
        else if (column == "det" && (value < 1 || value > 32)) RejectGridEdit(e, "Detektor måste vara 1..32.");
        else if (column == "meas" && (value < short.MinValue || value > short.MaxValue)) RejectGridEdit(e, "Mätvärde måste vara -32768..32767.");
        else if (column == "range" && (value < 0 || value > ushort.MaxValue)) RejectGridEdit(e, "Range måste vara 0..65535.");
        else if (column == "format" && (value < 0 || value > 2)) RejectGridEdit(e, "Format måste vara 0, 1 eller 2.");
        else if (column is "inst1" or "inst2" or "inst3" or "under" or "over" or "oor")
        {
            if (value < short.MinValue || value > short.MaxValue) RejectGridEdit(e, "Larmgräns måste vara -32768..32767.");
        }

        if (!e.Cancel && column is "line" or "det")
        {
            var row = _grid.Rows[e.RowIndex];
            var sensor = row.Tag as Sensor;
            if (sensor is null) return;
            int newLine = column == "line" ? value : ReadIntCell(row, "line", sensor.Line);
            int newDet = column == "det" ? value : ReadIntCell(row, "det", sensor.Detector);
            bool duplicate = _config?.Sensors.Any(s => !ReferenceEquals(s, sensor) && s.Line == newLine && s.Detector == newDet) == true;
            if (duplicate) RejectGridEdit(e, $"L{newLine}D{newDet} används redan av en annan kanal.");
        }
    }

    private void RejectGridEdit(DataGridViewCellValidatingEventArgs e, string message)
    {
        e.Cancel = true;
        _grid.Rows[e.RowIndex].ErrorText = message;
        MessageBox.Show(this, message, "Ogiltigt värde", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void OnGridCellEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        if (_updatingGrid || e.RowIndex < 0) return;
        var row = _grid.Rows[e.RowIndex];
        row.ErrorText = "";
        if (row.Tag is not Sensor s) return;

        try
        {
            ApplyGridEdit(row, s, _grid.Columns[e.ColumnIndex].Name);
            RefreshGridRow(row, s);
            if (ReferenceEquals(row, CurrentGridRow())) RefreshDetectorEditor(row, s);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Kunde inte uppdatera värdet: " + ex.Message, "Fel", MessageBoxButtons.OK, MessageBoxIcon.Error);
            RefreshGridRow(row, s);
        }
    }

    private void ApplyGridEdit(DataGridViewRow row, Sensor s, string column)
    {
        if (column == "addr") return;

        switch (column)
        {
            case "line":
            case "det":
                RebindSensor(s, ReadIntCell(row, "line", s.Line), ReadIntCell(row, "det", s.Detector));
                break;
            case "label":
                s.Label = ReadTextCell(row, "label");
                break;
            case "gas":
                s.ShortGasName = ReadTextCell(row, "gas");
                break;
            case "unit":
                s.Unit = ReadTextCell(row, "unit");
                break;
            case "range":
                s.Range = ReadIntCell(row, "range", s.Range);
                break;
            case "format":
                s.DisplayFormat = ReadIntCell(row, "format", s.DisplayFormat);
                break;
            case "inst1":
                s.Thresholds.Inst1 = ReadIntCell(row, "inst1", s.Thresholds.Inst1);
                RecomputeCurrentAlarm(s);
                break;
            case "inst2":
                s.Thresholds.Inst2 = ReadIntCell(row, "inst2", s.Thresholds.Inst2);
                RecomputeCurrentAlarm(s);
                break;
            case "inst3":
                s.Thresholds.Inst3 = ReadIntCell(row, "inst3", s.Thresholds.Inst3);
                RecomputeCurrentAlarm(s);
                break;
            case "under":
                s.Thresholds.Underscale = ReadIntCell(row, "under", s.Thresholds.Underscale);
                RecomputeCurrentAlarm(s);
                break;
            case "over":
                s.Thresholds.Overscale = ReadIntCell(row, "over", s.Thresholds.Overscale);
                RecomputeCurrentAlarm(s);
                break;
            case "oor":
                s.Thresholds.OutOfRange = ReadIntCell(row, "oor", s.Thresholds.OutOfRange);
                RecomputeCurrentAlarm(s);
                break;
            case "meas":
                _sim.SetMeasurement(s.Line, s.Detector, (short)ReadIntCell(row, "meas", _sim.GetMeasurement(s.Line, s.Detector)));
                break;
            case "alarms":
                if (TryParseAlarmBits(ReadTextCell(row, "alarms"), out var bits)) _sim.SetAlarm(s.Line, s.Detector, bits);
                break;
        }
    }

    private void RebindSensor(Sensor s, int newLine, int newDetector)
    {
        if (s.Line == newLine && s.Detector == newDetector) return;
        int oldIndex = s.Index;
        short measurement = _sim.GetMeasurement(s.Line, s.Detector);
        AlarmBits alarms = _sim.GetAlarm(s.Line, s.Detector);

        if (oldIndex >= 0 && oldIndex < _sim.Store.Detectors.Length)
        {
            var oldState = _sim.Store.Detectors[oldIndex];
            oldState.Config = null;
            oldState.Measurement = 0;
            oldState.ActiveAlarms = AlarmBits.None;
        }

        s.Line = newLine;
        s.Detector = newDetector;
        int newIndex = s.Index;
        var newState = _sim.Store.Detectors[newIndex];
        newState.Config = s;
        newState.Measurement = measurement;
        newState.ActiveAlarms = alarms;
        _sim.Repack();
    }

    private void RecomputeCurrentAlarm(Sensor s)
    {
        short current = _sim.GetMeasurement(s.Line, s.Detector);
        _sim.SetMeasurement(s.Line, s.Detector, current);
    }

    private static string ReadTextCell(DataGridViewRow row, string column)
        => Convert.ToString(row.Cells[column].Value)?.Trim() ?? "";

    private static int ReadIntCell(DataGridViewRow row, string column, int fallback)
        => int.TryParse(Convert.ToString(row.Cells[column].Value), out int value) ? value : fallback;

    private static bool IsIntegerColumn(string column)
        => column is "line" or "det" or "range" or "format" or "inst1" or "inst2" or "inst3" or "under" or "over" or "oor" or "meas";

    private static bool TryParseAlarmBits(string text, out AlarmBits bits)
    {
        text = text.Trim();
        if (text.Length == 0 || text.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            bits = AlarmBits.None;
            return true;
        }
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && ushort.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out ushort hex))
        {
            bits = (AlarmBits)hex;
            return true;
        }
        if (ushort.TryParse(text, out ushort numeric))
        {
            bits = (AlarmBits)numeric;
            return true;
        }
        return Enum.TryParse(text, true, out bits);
    }

    private async Task StartServerAsync()
    {
        int port = (int)_numPort.Value;
        _server = new Mx43ModbusServer(_sim.Store, port);
        _server.Log += Log;
        try
        {
            await _server.StartAsync();
            var addresses = GetListenAddressText();
            _lblIp.Text = $"IP: {addresses}";
            Log($"Server address: {addresses}:{port}");
            _btnStart.Enabled = false;
            _btnStop.Enabled = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Kunde inte starta: " + ex.Message, "Fel", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task StopServerAsync()
    {
        if (_server is not null) await _server.StopAsync();
        _server = null;
        _lblIp.Text = "IP: 127.0.0.1";
        _btnStart.Enabled = true;
        _btnStop.Enabled = false;
    }

    private static string GetListenAddressText()
    {
        try
        {
            var lanAddresses = Dns.GetHostEntry(Dns.GetHostName()).AddressList
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .Select(a => a.ToString())
                .Distinct()
                .OrderBy(a => a)
                .ToArray();

            return lanAddresses.Length == 0
                ? "127.0.0.1"
                : "127.0.0.1, " + string.Join(", ", lanAddresses);
        }
        catch
        {
            return "127.0.0.1";
        }
    }
}
