using System.Runtime.InteropServices;

namespace BootdataViewer;

// Compares the bootdata values with a running PLC and writes the checked ones by symbol name.
// Each variable is a checkable top-level row; structs and arrays expand into their members,
// showing PLC and file value side by side.
public sealed class WriteDialog : Form
{
    static readonly (string Title, int Width)[] Columns =
    {
        ("Variable", 400), ("Status", 130), ("Type", 180), ("PLC value", 230), ("File value / message", 320),
    };

    readonly BootdataFile _file;
    readonly ValueDecoder _decoder;
    // Targets come from the routes of the local TwinCAT system service; both boxes also accept typed input.
    readonly ComboBox _target = new() { Width = 330, DropDownStyle = ComboBoxStyle.DropDown };
    readonly ComboBox _port = new() { Width = 110, DropDownStyle = ComboBoxStyle.DropDown };
    readonly Button _refresh = new() { Text = "↻", Width = 30 };
    int _defaultPort;
    int _probeVersion;
    readonly TextBox _filter = new() { Width = 260 };
    readonly Button _compare = new() { Text = "Connect && compare", AutoSize = true };
    readonly Button _write = new() { Text = "Write checked", AutoSize = true, Enabled = false };
    // Live PLC state while connected; writing is only enabled in Run.
    readonly Label _state = new() { AutoSize = true, Padding = new Padding(8, 6, 0, 0), Font = new Font(DefaultFont, FontStyle.Bold) };
    readonly System.Windows.Forms.Timer _statePoll = new() { Interval = 2000 };
    bool _busy, _polling, _stateKnown;
    readonly TreeView _tree = new()
    {
        Dock = DockStyle.Fill, CheckBoxes = true, FullRowSelect = true, HideSelection = false, ShowLines = false,
        DrawMode = TreeViewDrawMode.OwnerDrawText, Font = new Font("Consolas", 9.5f), ItemHeight = 20,
    };
    readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 24, TextAlign = ContentAlignment.MiddleLeft };
    // Shown when the PLC program has no FB_S_UPS / FB_WritePersistentData instance.
    readonly Label _saveWarning = new()
    {
        Dock = DockStyle.Top, AutoSize = false, Height = 40, Visible = false, Padding = new Padding(8, 0, 8, 0),
        BackColor = Color.FromArgb(255, 235, 156), ForeColor = Color.FromArgb(90, 60, 0), TextAlign = ContentAlignment.MiddleLeft,
    };
    AdsWriter? _writer;
    List<WriteItem> _items = new();

    // One row of the tree: a variable (Item set) or a member inside it.
    sealed class Row
    {
        public WriteItem Item = null!;
        public ValueNode File = null!;
        public ValueNode? Plc;
        public bool IsTop;
        public bool Differs;
        public int DifferingChildren;   // for structs/arrays: how many direct members differ
    }

    static readonly Color DiffBack = Color.FromArgb(255, 241, 204);
    static readonly Color PlcColor = Color.FromArgb(176, 0, 0);
    static readonly Color FileColor = Color.FromArgb(0, 112, 0);

    // target/port preselect the PLC, e.g. the active target of the TwinCAT project in Visual Studio.
    public WriteDialog(BootdataFile file, string? filter, string? target = null, int? port = null)
    {
        _file = file;
        _decoder = new ValueDecoder(file);
        Text = "Write bootdata values to PLC";
        Width = 1320;
        Height = 760;
        StartPosition = FormStartPosition.CenterParent;
        _defaultPort = port ?? Program.DefaultPort(file.Path);
        _target.Text = string.IsNullOrWhiteSpace(target) ? "local" : target;
        _port.Text = _defaultPort.ToString();
        _filter.Text = filter ?? "";
        // .NET Framework WinForms has no placeholder text; the hint is a tooltip instead.
        new ToolTip().SetToolTip(_filter, "Only variables whose name starts with this text, e.g. MAIN.fbConveyor");

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(4), WrapContents = false };
        bar.Controls.AddRange(new Control[]
        {
            Caption("Target:"), _target, _refresh, Caption("PLC port:"), _port, Caption("Name filter:"), _filter, _compare, _write, _state,
        });
        _statePoll.Tick += async (_, _) => await PollStateAsync();
        new ToolTip().SetToolTip(_refresh, "Reload the ADS routes from the TwinCAT system service");

        _refresh.Click += async (_, _) => await LoadTargetsAsync();
        // Text still holds the previous entry while SelectionChangeCommitted runs, so pass the item explicitly.
        _target.SelectionChangeCommitted += async (_, _) => await ProbePortsAsync((_target.SelectedItem as AdsTarget)?.NetId);
        _target.Leave += async (_, _) => { if (_target.SelectedItem == null) await ProbePortsAsync(); };
        Shown += async (_, _) => await LoadTargetsAsync();

        var header = new Panel { Dock = DockStyle.Top, Height = 22 };
        header.Paint += (_, e) =>
        {
            e.Graphics.Clear(SystemColors.Control);
            int x = 0;
            foreach (var (title, width) in Columns)
            {
                TextRenderer.DrawText(e.Graphics, title, Font, new Rectangle(x + 4, 0, width - 8, header.Height),
                                      SystemColors.ControlText, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                x += width;
                e.Graphics.DrawLine(SystemPens.ControlDark, x - 1, 3, x - 1, header.Height - 3);
            }
        };

        _tree.DrawNode += DrawRow;
        _tree.BeforeExpand += (_, e) => AddChildren(e.Node!);
        _tree.BeforeCheck += (_, e) =>
        {
            if (e.Node?.Tag is not Row { IsTop: true, Item.CanWrite: true }) e.Cancel = true;
        };
        _tree.AfterCheck += (_, _) => UpdateWriteButton();

        Controls.Add(_tree);
        Controls.Add(header);
        Controls.Add(_saveWarning);
        Controls.Add(bar);
        Controls.Add(_status);
        _status.Text = "Connect to compare the file with the PLC. Nothing is written until you press 'Write checked'.";

        _compare.Click += async (_, _) => await CompareAsync();
        _write.Click += async (_, _) => await WriteAsync();
        FormClosed += (_, _) => { _statePoll.Stop(); _writer?.Dispose(); };
    }

    // Returns a note for the status line when the check passed.
    string ShowSaveCheck(PersistentSaveCheck? check, string? error)
    {
        if (check is { HasPersistentSave: true })
        {
            _saveWarning.Visible = false;
            return " Persistent data saved by " + string.Join(", ", check.Instances.Take(3).Select(i => $"{i.Path} ({i.Type})"))
                   + (check.Instances.Count > 3 ? $" and {check.Instances.Count - 3} more." : ".");
        }
        _saveWarning.Text = "⚠  " + (check?.Warning ??
            "Could not check the PLC program for FB_S_UPS / FB_WritePersistentData: " + error);
        _saveWarning.Visible = true;
        return "";
    }

    void ShowState()
    {
        if (_writer == null) { _state.Text = ""; return; }
        _state.Text = _writer.IsRunning ? "● PLC in Run" : $"● PLC in {_writer.PlcState} - writing needs Run";
        _state.ForeColor = _writer.IsRunning ? Color.DarkGreen : Color.DarkRed;
        UpdateWriteButton();
    }

    async Task PollStateAsync()
    {
        if (_writer == null || _busy || _polling) return;
        _polling = true;
        var writer = _writer;
        try
        {
            await Task.Run(writer.ReadState);
            _stateKnown = true;
            if (writer == _writer) ShowState();
        }
        catch (Exception ex)
        {
            _stateKnown = false;
            if (writer != _writer) return;
            _state.Text = "● PLC state unknown: " + ex.Message;
            _state.ForeColor = Color.DarkRed;
            UpdateWriteButton();
        }
        finally { _polling = false; }
    }

    static Label Caption(string text) =>
        new() { Text = text, AutoSize = true, Padding = new Padding(6, 6, 0, 0) };

    // A listed route, or whatever Net ID was typed in.
    string TargetNetId() =>
        _target.Items.OfType<AdsTarget>().FirstOrDefault(t => t.ToString() == _target.Text)?.NetId ?? _target.Text.Trim();

    int? Port()
    {
        var digits = new string(_port.Text.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out int p) && p is > 0 and < 65536 ? p : null;
    }

    async Task LoadTargetsAsync()
    {
        _status.Text = "Reading ADS routes from the TwinCAT system service...";
        try
        {
            var targets = await Task.Run(AdsRoutes.ReadTargets);
            string previous = TargetNetId();
            _target.Items.Clear();
            foreach (var t in targets) _target.Items.Add(t);
            _target.SelectedItem = targets.FirstOrDefault(t => t.NetId == previous) ?? targets[0];
            _status.Text = $"{targets.Count - 1} ADS route(s) found. Select a target and press 'Connect & compare'.";
        }
        catch (Exception ex)
        {
            _status.Text = "Could not read the ADS routes: " + ex.Message;
        }
        await ProbePortsAsync();
    }

    // Lists the PLC runtimes (ports 851..870) that answer on the selected target.
    async Task ProbePortsAsync(string? netId = null)
    {
        netId ??= TargetNetId();
        if (netId.Length == 0) return;
        int version = ++_probeVersion;
        string current = Port()?.ToString() ?? _defaultPort.ToString();
        _port.Items.Clear();
        _port.Text = current;
        _status.Text = $"Searching for PLC runtimes on {netId}...";
        try
        {
            var runtimes = await Task.Run(() => AdsRoutes.FindPlcRuntimes(netId));
            if (version != _probeVersion) return;   // a newer selection superseded this probe
            foreach (var r in runtimes) _port.Items.Add(r);
            var preferred = runtimes.FirstOrDefault(r => r.Port == Port()) ?? runtimes.FirstOrDefault(r => r.Port == _defaultPort)
                            ?? runtimes.FirstOrDefault();
            if (preferred != null) _port.SelectedItem = preferred;
            _status.Text = runtimes.Count > 0
                ? $"{netId}: PLC runtime(s) on " + string.Join(", ", runtimes) + "."
                : $"{netId}: no PLC runtime found on ports 851-870.";
        }
        catch (Exception ex)
        {
            if (version == _probeVersion) _status.Text = $"{netId}: " + ex.Message;
        }
    }

    async Task CompareAsync()
    {
        if (Port() is not int port)
        {
            _status.Text = "Enter a valid PLC port.";
            return;
        }
        SetBusy(true, "Connecting...");
        try
        {
            _statePoll.Stop();
            _writer?.Dispose();
            _writer = null;
            ShowState();
            _saveWarning.Visible = false;
            string target = TargetNetId();
            string filter = _filter.Text.Trim();
            var (writer, items, saveCheck) = await Task.Run(() =>
            {
                var w = new AdsWriter(_file, target, port);
                PersistentSaveCheck? check = null;
                string? checkError = null;
                try { check = w.CheckPersistentSave(); }
                catch (Exception ex) { checkError = ex.Message; }
                return (w, w.Compare(AdsWriter.Leaves(_file, filter)), (check, checkError));
            });
            string saveNote = ShowSaveCheck(saveCheck.check, saveCheck.checkError);
            _writer = writer;
            _stateKnown = true;
            _items = items;
            Fill();
            ShowState();
            _statePoll.Start();
            _status.Text = $"Connected to {target}:{port}, PLC in {writer.PlcState}. " + Summary() +
                           (writer.IsRunning ? "" : " Start the PLC to write values.") + saveNote;
        }
        catch (Exception ex)
        {
            _status.Text = "Connection failed: " + ex.Message;
        }
        finally { SetBusy(false); }
    }

    async Task WriteAsync()
    {
        if (_writer == null) return;
        var selected = CheckedItems().ToList();
        if (selected.Count == 0) return;

        // Re-check right before asking; the polled state may be up to two seconds old.
        var writer = _writer;
        try { await Task.Run(writer.EnsureRunning); }
        catch (Exception ex)
        {
            ShowState();
            MessageBox.Show(this, ex.Message, "Write to PLC", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var answer = MessageBox.Show(this,
            $"Write {selected.Count} value(s) from\n{_file.Path}\ninto the PLC at {_target.Text}, port {Port()} ({_writer.PlcState})?\n\n" +
            "The current PLC values are overwritten.",
            "Write to PLC", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.OK) return;

        SetBusy(true, "Writing...");
        try
        {
            await Task.Run(() => writer.Write(selected));   // checks Run again and stops if the PLC leaves it
            Fill();
            _status.Text = "Write finished. " + Summary();
        }
        catch (Exception ex)
        {
            _status.Text = "Write failed: " + ex.Message;
        }
        finally { SetBusy(false); ShowState(); }
    }

    IEnumerable<WriteItem> CheckedItems() =>
        _tree.Nodes.Cast<TreeNode>().Where(n => n.Checked).Select(n => ((Row)n.Tag!).Item);

    void Fill()
    {
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        foreach (var item in _items)
        {
            var fileNode = _decoder.Decode(item.Symbol);
            var plcNode = item.PlcData != null ? DecodeWith(item.Symbol, item.PlcData) : null;
            var row = new Row
            {
                Item = item, File = fileNode, Plc = plcNode, IsTop = true,
                Differs = item.Status is WriteStatus.Differs,
            };
            row.DifferingChildren = CountDifferingChildren(row);
            var node = new TreeNode(item.Name) { Tag = row, Checked = item.Status == WriteStatus.Differs };
            if (fileNode.Children.Count > 0) node.Nodes.Add(new TreeNode());   // placeholder until expanded
            _tree.Nodes.Add(node);
        }
        _tree.EndUpdate();
        UpdateWriteButton();
    }

    ValueNode DecodeWith(SymbolEntry sym, byte[] data) => _decoder.Decode(new SymbolEntry
    {
        Name = sym.Name, FullName = sym.FullName, Size = sym.Size, Flags = sym.Flags, TypeGuid = sym.TypeGuid, Data = data,
    });

    // Children are created on first expand; large arrays of structs would otherwise make thousands of nodes up front.
    void AddChildren(TreeNode node)
    {
        if (node.Nodes.Count != 1 || node.Nodes[0].Tag != null || node.Tag is not Row row) return;
        _tree.BeginUpdate();
        node.Nodes.Clear();
        var data = row.Item.Symbol.Data;
        for (int i = 0; i < row.File.Children.Count; i++)
        {
            var f = row.File.Children[i];
            var p = row.Plc != null && i < row.Plc.Children.Count ? row.Plc.Children[i] : null;
            var childRow = new Row { Item = row.Item, File = f, Plc = p, Differs = Differs(f, p, data, row.Item.PlcData) };
            if (childRow.Differs) childRow.DifferingChildren = CountDifferingChildren(childRow);
            var child = new TreeNode(f.Name) { Tag = childRow };
            if (f.Children.Count > 0) child.Nodes.Add(new TreeNode());
            node.Nodes.Add(child);
            HideCheckBox(child);
        }
        _tree.EndUpdate();
    }

    static int CountDifferingChildren(Row row)
    {
        if (row.Plc == null || row.Item.PlcData == null) return 0;
        int count = 0;
        for (int i = 0; i < row.File.Children.Count && i < row.Plc.Children.Count; i++)
            if (Differs(row.File.Children[i], row.Plc.Children[i], row.Item.Symbol.Data, row.Item.PlcData)) count++;
        return count;
    }

    static bool Differs(ValueNode file, ValueNode? plc, byte[] fileData, byte[]? plcData)
    {
        if (plc == null || plcData == null) return false;
        if (file.BitOffset != null || file.Children.Count == 0 && file.Size == 0) return file.Value != plc.Value;
        int len = Math.Min(file.Size, Math.Min(fileData.Length, plcData.Length) - file.Offset);
        return len > 0 && !fileData.AsSpan(file.Offset, len).SequenceEqual(plcData.AsSpan(file.Offset, len));
    }

    void DrawRow(object? sender, DrawTreeNodeEventArgs e)
    {
        if (e.Node?.Tag is not Row row || e.Bounds.IsEmpty) return;
        var g = e.Graphics;
        bool selected = (e.State & TreeNodeStates.Selected) != 0;
        var rowRect = new Rectangle(e.Bounds.X, e.Bounds.Y, _tree.ClientSize.Width - e.Bounds.X, e.Bounds.Height);
        using (var back = new SolidBrush(selected ? SystemColors.Highlight : row.Differs ? DiffBack : SystemColors.Window))
            g.FillRectangle(back, rowRect);

        var item = row.Item;
        Color color = selected ? SystemColors.HighlightText
            : row.IsTop ? item.Status switch
            {
                WriteStatus.Differs => SystemColors.ControlText,
                WriteStatus.Written => Color.DarkGreen,
                WriteStatus.Equal => SystemColors.GrayText,
                _ => Color.DarkRed,
            }
            : SystemColors.ControlText;

        // Structs/arrays show how many of their direct members differ, e.g. "≠ 1/3".
        string count = row.Differs && row.File.Children.Count > 0 ? $"{row.DifferingChildren}/{row.File.Children.Count}" : "";
        string status = row.IsTop ? item.Status + (count.Length > 0 ? " " + count : "")
            : row.Differs ? (count.Length > 0 ? "≠ " + count : "≠ differs") : "";
        string type = row.IsTop && item.PlcType.Length > 0 ? item.PlcType : row.File.Type;
        string plcValue = row.Plc == null ? "" : Display(row.Plc);
        string fileValue = row.IsTop && item.Message.Length > 0 ? item.Message : Display(row.File);
        string[] cells = { row.IsTop ? item.Name : row.File.Name, status, type, plcValue, fileValue };

        // Column 0 starts at the node's indent; the others are fixed so they line up across levels.
        int x = 0;
        for (int i = 0; i < cells.Length; i++)
        {
            int left = i == 0 ? e.Bounds.X : x;
            int right = x + Columns[i].Width;
            if (right > left)
            {
                bool bold = row.Differs && (i == 0 || i >= 3);
                var font = bold ? new Font(_tree.Font, FontStyle.Bold) : _tree.Font;
                var cellColor = selected || !row.Differs ? color : i switch { 3 => PlcColor, 4 => FileColor, _ => color };
                TextRenderer.DrawText(g, cells[i], font, new Rectangle(left + 2, e.Bounds.Y, right - left - 6, e.Bounds.Height),
                                      cellColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                if (bold) font.Dispose();
            }
            x = right;
        }
    }

    static string Display(ValueNode n) => n.Children.Count == 0 ? n.Value : $"{{{n.Children.Count} elements}}";

    string Summary() =>
        string.Join(", ", _items.GroupBy(i => i.Status).Select(g => $"{g.Count()} {g.Key}"));

    void UpdateWriteButton() =>
        _write.Enabled = _writer is { IsRunning: true } && _stateKnown && !_busy && CheckedItems().Any();

    void SetBusy(bool busy, string? text = null)
    {
        _busy = busy;
        _compare.Enabled = !busy;
        UseWaitCursor = busy;
        if (text != null) _status.Text = text;
        UpdateWriteButton();
    }

    // Win32 TreeView has no per-node checkbox switch; clearing the state image removes it.
    const int TVIF_STATE = 0x8, TVIS_STATEIMAGEMASK = 0xF000, TVM_SETITEMW = 0x113F;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct TVITEM
    {
        public int mask;
        public IntPtr hItem;
        public int state, stateMask;
        public IntPtr pszText;
        public int cchTextMax, iImage, iSelectedImage, cChildren;
        public IntPtr lParam;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref TVITEM lParam);

    void HideCheckBox(TreeNode node)
    {
        var tvi = new TVITEM { mask = TVIF_STATE, hItem = node.Handle, stateMask = TVIS_STATEIMAGEMASK, state = 0 };
        SendMessage(_tree.Handle, TVM_SETITEMW, IntPtr.Zero, ref tvi);
    }
}
