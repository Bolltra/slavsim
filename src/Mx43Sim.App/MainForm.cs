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
        _grid.ReadOnly = true;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.Columns.Add("addr", "Mät-adress");
        _grid.Columns.Add("line", "Line");
        _grid.Columns.Add("det", "Det");
        _grid.Columns.Add("label", "Label");
        _grid.Columns.Add("gas", "Gas");
        _grid.Columns.Add("unit", "Enhet");
        _grid.Columns.Add("range", "Range");
        _grid.Columns.Add("inst1", "Alarm 1");
        _grid.Columns.Add("inst2", "Alarm 2");
        _grid.Columns.Add("inst3", "Alarm 3");
        _grid.Columns.Add("meas", "Mätning");
        _grid.Columns.Add("alarms", "Aktiva larm");
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
        _trkMeas.Scroll += (_, _) => _numMeas.Value = _trkMeas.Value;
        _numMeas.ValueChanged += (_, _) => _trkMeas.Value = (int)_numMeas.Value;
        _grid.SelectionChanged += (_, _) => OnSelectionChanged();

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
        _grid.Rows.Clear();
        if (_config is null) return;
        foreach (var s in _config.Sensors)
        {
            int idx = s.Index;
            var d = _sim.Store.Detectors[idx];
            int addr = Mx43AddressMap.MeasurementRegFor(s.Line, s.Detector);
            _grid.Rows.Add(addr, s.Line, s.Detector, s.Label, s.ShortGasName, s.Unit, s.Range,
                s.Thresholds.Inst1, s.Thresholds.Inst2, s.Thresholds.Inst3,
                d.Measurement, d.ActiveAlarms.ToString());
        }
    }

    private void OnSelectionChanged()
    {
        if (_grid.SelectedRows.Count == 0) { _grpEditor.Enabled = false; return; }
        _grpEditor.Enabled = true;
        var row = _grid.SelectedRows[0];
        int line = (int)row.Cells["line"].Value;
        int det = (int)row.Cells["det"].Value;
        var s = _config?.Sensors.FirstOrDefault(x => x.Line == line && x.Detector == det);
        if (s is null) return;
        _lblSel.Text = $"Detektor L{s.Line}D{s.Detector} — {s.Label}";
        _lblSelGas.Text = $"Gas: {s.ShortGasName}   Enhet: {s.Unit}";
        _lblSelRange.Text = $"Range: {s.Range}    DisplayFormat: {s.DisplayFormat}";
        _lblSelThresh.Text = $"Tröskelvärden:  A1={s.Thresholds.Inst1}   A2={s.Thresholds.Inst2}   A3={s.Thresholds.Inst3}   " +
                             $"Under={s.Thresholds.Underscale}   Över={s.Thresholds.Overscale}   OOR={s.Thresholds.OutOfRange}";
        var d = _sim.Store.Detectors[s.Index];
        _numMeas.Value = d.Measurement;
        _trkMeas.Value = d.Measurement;
        // Make the trackbar range cover the relevant domain
        _trkMeas.Minimum = Math.Min(-100, Math.Max(s.Thresholds.Underscale - 50, -32768));
        _trkMeas.Maximum = Math.Max(s.Thresholds.Overscale + 50, 100);
        _lblAlarms.Text = "Aktiva larm: " + d.ActiveAlarms;
        _lblAlarms.ForeColor = d.ActiveAlarms == AlarmBits.None ? Color.Black : Color.Red;
    }

    private void ApplyMeasurement()
    {
        if (_grid.SelectedRows.Count == 0 || _config is null) return;
        var row = _grid.SelectedRows[0];
        int line = (int)row.Cells["line"].Value;
        int det = (int)row.Cells["det"].Value;
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
        if (_grid.SelectedRows.Count == 0 || _config is null) return;
        var row = _grid.SelectedRows[0];
        int line = (int)row.Cells["line"].Value;
        int det = (int)row.Cells["det"].Value;
        _sim.SetAlarm(line, det, AlarmBits.None);
        row.Cells["alarms"].Value = AlarmBits.None.ToString();
        row.DefaultCellStyle.BackColor = Color.Empty;
        _lblAlarms.Text = "Aktiva larm: None";
        _lblAlarms.ForeColor = Color.Black;
        Log($"L{line}D{det}: alarms cleared");
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
