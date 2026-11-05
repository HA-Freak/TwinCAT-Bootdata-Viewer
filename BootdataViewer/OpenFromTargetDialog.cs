namespace BootdataViewer;

// How the viewer was started; the Visual Studio extension passes the active TwinCAT target and PLC port.
public sealed record StartOptions(string? Path, string? Target, int? Port, bool Old, bool WriteDialog);

// Picks a target (from the local ADS routes), a PLC runtime port and the file to read.
public sealed class OpenFromTargetDialog : Form
{
    readonly ComboBox _target = new() { Width = 330, DropDownStyle = ComboBoxStyle.DropDown };
    readonly ComboBox _port = new() { Width = 120, DropDownStyle = ComboBoxStyle.DropDown };
    readonly ComboBox _file = new() { Width = 330, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly Label _status = new() { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(460, 0) };
    int _probe;

    public string NetId { get; private set; } = "";
    public int Port { get; private set; }
    public bool Old => _file.SelectedIndex == 1;

    public OpenFromTargetDialog(string? target, int? port)
    {
        Text = "Open bootdata from target";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        _target.Text = target ?? "local";
        _port.Text = (port ?? 851).ToString();
        _file.Items.AddRange(new object[] { "Port_xxx.bootdata (current)", "Port_xxx.bootdata-old (previous)" });
        _file.SelectedIndex = 0;

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label { Text = "Target:", AutoSize = true, Padding = new Padding(0, 4, 0, 0) }, 0, 0);
        layout.Controls.Add(_target, 1, 0);
        layout.Controls.Add(new Label { Text = "PLC port:", AutoSize = true, Padding = new Padding(0, 4, 0, 0) }, 0, 1);
        layout.Controls.Add(_port, 1, 1);
        layout.Controls.Add(new Label { Text = "File:", AutoSize = true, Padding = new Padding(0, 4, 0, 0) }, 0, 2);
        layout.Controls.Add(_file, 1, 2);
        layout.Controls.Add(_status, 1, 3);

        var ok = new Button { Text = "Open", Width = 90 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        layout.Controls.Add(buttons, 1, 4);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;

        ok.Click += (_, _) =>
        {
            NetId = TargetNetId();
            var digits = new string(_port.Text.TakeWhile(char.IsDigit).ToArray());
            if (NetId.Length == 0 || !int.TryParse(digits, out int p)) { _status.Text = "Enter a target and a port."; return; }
            Port = p;
            DialogResult = DialogResult.OK;
        };
        _target.SelectionChangeCommitted += async (_, _) => await ProbeAsync((_target.SelectedItem as AdsTarget)?.NetId);
        Shown += async (_, _) => await LoadTargetsAsync();
    }

    string TargetNetId() =>
        _target.Items.OfType<AdsTarget>().FirstOrDefault(t => t.ToString() == _target.Text)?.NetId ?? _target.Text.Trim();

    async Task LoadTargetsAsync()
    {
        _status.Text = "Reading ADS routes...";
        try
        {
            string previous = TargetNetId();
            var targets = await Task.Run(AdsRoutes.ReadTargets);
            _target.Items.Clear();
            foreach (var t in targets) _target.Items.Add(t);
            var match = targets.FirstOrDefault(t => t.NetId == previous || previous == "local" && t.IsLocal);
            if (match != null) _target.SelectedItem = match;
            else _target.Text = previous;
            _status.Text = "";
        }
        catch (Exception ex) { _status.Text = "Could not read the ADS routes: " + ex.Message; }
        await ProbeAsync(TargetNetId());
    }

    async Task ProbeAsync(string? netId)
    {
        if (string.IsNullOrEmpty(netId)) return;
        int version = ++_probe;
        string current = _port.Text;
        _status.Text = $"Searching for PLC runtimes on {netId}...";
        try
        {
            var runtimes = await Task.Run(() => AdsRoutes.FindPlcRuntimes(netId!));
            if (version != _probe) return;
            _port.Items.Clear();
            foreach (var r in runtimes) _port.Items.Add(r);
            var keep = runtimes.FirstOrDefault(r => current.StartsWith(r.Port.ToString()));
            if (keep != null) _port.SelectedItem = keep;
            else _port.Text = current;
            _status.Text = runtimes.Count > 0 ? "PLC runtimes: " + string.Join(", ", runtimes) : "No PLC runtime answers; the file can still be read.";
        }
        catch (Exception ex) { if (version == _probe) _status.Text = ex.Message; }
    }
}
