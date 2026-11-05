using System.Text;

namespace BootdataViewer;

public sealed class MainForm : Form
{
    static readonly string[] BootFolders =
    {
        @"C:\ProgramData\Beckhoff\TwinCAT\3.1\Runtimes\UmRT_Default\3.1\Boot\Plc",
        @"C:\ProgramData\Beckhoff\TwinCAT\3.1\Boot\Plc",
        @"C:\TwinCAT\3.1\Boot\Plc",
    };

    readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false, Font = new Font("Consolas", 10f) };
    readonly TextBox _details = MonoBox();
    readonly TextBox _types = MonoBox();
    readonly TextBox _hex = MonoBox();
    readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    BootdataFile? _file;
    IntegrityReport? _report;   // integrity check of the file as loaded
    // The PLC the file belongs to (downloaded from it, or passed by the Visual Studio extension);
    // preselected in the write dialog.
    string? _plcTarget;
    int? _plcPort;

    public MainForm(string? path) : this(new StartOptions(path, null, null, false, false)) { }

    public MainForm(StartOptions start)
    {
        string? path = start.Path;
        _plcTarget = start.Target;
        _plcPort = start.Port;
        Text = "TwinCAT XAE Bootdata Viewer";
        Width = 1200;
        Height = 800;
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;

        var menu = new MenuStrip();
        var fileMenu = new ToolStripMenuItem("&File");
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("&Open...", null, (_, _) => OpenDialog(), Keys.Control | Keys.O));
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("Open from &target...", null, (_, _) => OpenFromTargetDialog(), Keys.Control | Keys.T));
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("&Reload", null, (_, _) => Reload(), Keys.F5));
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("Check &integrity...", null, (_, _) => ShowIntegrity(), Keys.Control | Keys.I));
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("&Save", null, (_, _) => Save(_file?.Path), Keys.Control | Keys.S));
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("Save &as...", null, (_, _) => SaveAs(), Keys.Control | Keys.Shift | Keys.S));
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("Export &JSON...", null, (_, _) => Export("json")));
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("Export &text...", null, (_, _) => Export("txt")));
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("E&xit", null, (_, _) => Close()));
        var editMenu = new ToolStripMenuItem("&Edit");
        editMenu.DropDownItems.Add(new ToolStripMenuItem("&Edit value...", null, (_, _) => EditSelected(), Keys.F2));
        var viewMenu = new ToolStripMenuItem("&View");
        viewMenu.DropDownItems.Add(new ToolStripMenuItem("&Expand all", null, (_, _) => _tree.ExpandAll()));
        viewMenu.DropDownItems.Add(new ToolStripMenuItem("&Collapse all", null, (_, _) => _tree.CollapseAll()));
        var plcMenu = new ToolStripMenuItem("&PLC");
        plcMenu.DropDownItems.Add(new ToolStripMenuItem("&Write to PLC...", null, (_, _) => OpenWriteDialog(), Keys.Control | Keys.W));
        menu.Items.Add(fileMenu);
        menu.Items.Add(editMenu);
        menu.Items.Add(viewMenu);
        menu.Items.Add(plcMenu);

        var split = new SplitContainer { Dock = DockStyle.Fill };
        // Only meaningful once the form has its real size; earlier values get clamped.
        Load += (_, _) => split.SplitterDistance = split.Width * 3 / 5;
        split.Panel1.Controls.Add(_tree);
        split.Panel2.Controls.Add(_details);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(Page("Variables", split));
        tabs.TabPages.Add(Page("Datatypes", _types));
        tabs.TabPages.Add(Page("Hex", _hex));

        var status = new StatusStrip();
        status.Items.Add(_status);

        Controls.Add(tabs);
        Controls.Add(menu);
        Controls.Add(status);
        MainMenuStrip = menu;

        _tree.AfterSelect += (_, e) => ShowDetails(e.Node?.Tag as ValueNode);
        _tree.NodeMouseDoubleClick += (_, e) => { if (e.Node.Nodes.Count == 0) EditSelected(); };
        _tree.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && _tree.SelectedNode?.Nodes.Count == 0) { EditSelected(); e.Handled = true; }
        };
        FormClosing += (_, e) => { if (!ConfirmDiscard()) e.Cancel = true; };
        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) Open(files[0]);
        };

        _status.Text = "Open a Port_xxx.bootdata file (Ctrl+O), open one from a target (Ctrl+T), or drop it here.";
        Shown += (_, _) =>
        {
            if (path != null) Open(path);
            else if (start.Target != null) OpenFromTarget(start.Target, start.Port ?? 851, start.Old);
            if (start.WriteDialog && _file != null) OpenWriteDialog();
        };
    }

    // Downloads the bootdata from the target's boot folder (read only) and opens the local copy.
    void OpenFromTarget(string netId, int port, bool old)
    {
        _status.Text = $"Reading Port_{port}.bootdata{(old ? "-old" : "")} from {netId}...";
        Cursor = Cursors.WaitCursor;
        try
        {
            string local = AdsBootFiles.Download(netId, port, old);
            _plcTarget = netId;
            _plcPort = port;
            Open(local);
            if (_file != null)
                _status.Text = $"Downloaded from {netId} (read only on the target; Save keeps a local copy). " + _status.Text;
        }
        catch (Exception ex)
        {
            _status.Text = "";
            MessageBox.Show(this, ex.Message, "Open from target", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { Cursor = Cursors.Default; }
    }

    void OpenFromTargetDialog()
    {
        using var dlg = new OpenFromTargetDialog(_plcTarget, _plcPort);
        if (dlg.ShowDialog(this) == DialogResult.OK) OpenFromTarget(dlg.NetId, dlg.Port, dlg.Old);
    }

    static TextBox MonoBox() => new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
        WordWrap = false, Font = new Font("Consolas", 10f), BackColor = SystemColors.Window,
    };

    static TabPage Page(string title, Control c)
    {
        var p = new TabPage(title);
        p.Controls.Add(c);
        return p;
    }

    void OpenDialog()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "TwinCAT bootdata (*.bootdata;*.bootdata-old)|*.bootdata;*.bootdata-old|All files (*.*)|*.*",
            InitialDirectory = _file != null ? Path.GetDirectoryName(_file.Path) : BootFolders.FirstOrDefault(Directory.Exists),
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) Open(dlg.FileName);
    }

    void Reload()
    {
        if (_file != null) Open(_file.Path);
    }

    // Asks before throwing away unsaved edits; true means go ahead.
    bool ConfirmDiscard()
    {
        if (_file == null || !_file.IsModified) return true;
        var answer = MessageBox.Show(this, $"Save the changes to\n{_file.Path}?", "Unsaved changes",
                                     MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        return answer switch
        {
            DialogResult.Yes => Save(_file.Path),
            DialogResult.No => true,
            _ => false,
        };
    }

    void EditSelected()
    {
        if (_file == null || _tree.SelectedNode is not { Tag: ValueNode { Kind: not null } v } treeNode) return;
        var sym = _file.AllSymbols.FirstOrDefault(s => !s.IsContainer && v.FileOffset >= s.FileOffset && v.FileOffset < s.FileOffset + s.Data.Length);
        if (sym == null) return;
        int offset = (int)(v.FileOffset - sym.FileOffset);

        using var dlg = new EditValueDialog(v, sym.Data, offset);
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result == null) return;
        dlg.Result.CopyTo(sym.Data, 0);

        v.Value = ValueDecoder.Format(v.Kind.Value, v.EnumValues, sym.Data, offset, v.Size, v.BitOffset);
        treeNode.Text = $"{v.Name} : {v.Type} = {v.Value}" + (v.Damage != null ? $"   ⚠ damaged: {v.Damage}" : "");
        // Highlight the edited value and the path to it until the file is saved.
        for (var n = treeNode; n != null; n = n.Parent) n.ForeColor = Color.DarkOrange;
        treeNode.NodeFont = new Font(_tree.Font, FontStyle.Bold);
        treeNode.Text = treeNode.Text;   // re-measure for the bold font
        ShowDetails(v);
        _hex.Text = HexDump(_file.ToBytes());
        UpdateTitle();
    }

    void UpdateTitle()
    {
        if (_file == null) return;
        Text = $"TwinCAT XAE Bootdata Viewer - {_file.Path}{(_file.IsModified ? " *" : "")}";
    }

    void SaveAs()
    {
        if (_file == null) return;
        using var dlg = new SaveFileDialog
        {
            Filter = "TwinCAT bootdata (*.bootdata)|*.bootdata|All files (*.*)|*.*",
            InitialDirectory = Path.GetDirectoryName(_file.Path),
            FileName = Path.GetFileName(_file.Path),
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) Save(dlg.FileName);
    }

    // Overwriting an existing file keeps the previous version as <file>.bak.
    bool Save(string? path)
    {
        if (_file == null || path == null) return false;
        if (!ConfirmCorrupt("Save it")) return false;
        bool bootFolder = path.Contains(@"\Boot\Plc\", StringComparison.OrdinalIgnoreCase);
        if (bootFolder && MessageBox.Show(this,
                "This file is in a TwinCAT boot folder.\n\n" +
                "TwinCAT overwrites it with the current values whenever the PLC stops, and reads it when the PLC starts. " +
                "Save it only while that runtime is stopped (Config mode), otherwise your changes are lost or not used.\n\n" +
                "Save anyway?", "Save bootdata", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.OK)
            return false;
        try
        {
            if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
            _file.Save(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        ClearEditMarks(_tree.Nodes);
        UpdateTitle();
        _status.Text = $"Saved {path}" + (File.Exists(path + ".bak") ? $" (previous version in {Path.GetFileName(path)}.bak)" : "");
        return true;
    }

    static void ClearEditMarks(TreeNodeCollection nodes)
    {
        foreach (TreeNode n in nodes)
        {
            n.ForeColor = (n.Tag as ValueNode)?.Damage != null ? Color.Red : Color.Empty;
            n.NodeFont = null;
            ClearEditMarks(n.Nodes);
        }
    }

    void Open(string path)
    {
        if (!ConfirmDiscard()) return;
        BootdataFile file;
        byte[] raw;
        IntegrityReport report;
        try
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                raw = new byte[fs.Length];
                fs.ReadExactly(raw);
            }
            report = BootdataCheck.Check(raw);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Cannot open file", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        try
        {
            file = BootdataFile.Parse(raw);
            file.Path = path;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"{path}\n\n{ex.Message}\n\n{Shorten(report)}", "Cannot open file",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        BootdataCheck.CheckValues(file, report);
        if (report.IsCorrupt) report.Recovery = BootdataCheck.Recovery(file);

        _file = file;
        _report = report;
        Text = $"TwinCAT XAE Bootdata Viewer - {path}";

        var dec = new ValueDecoder(file);
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        foreach (var sym in file.Symbols)
        {
            ValueNode node;
            try { node = dec.Decode(sym); }
            catch (Exception ex)
            {
                sym.Damage ??= "cannot be decoded: " + ex.Message;
                node = new ValueNode { Name = sym.Name, Type = "<damaged>", SymbolPath = sym.FullName, Damage = sym.Damage, FileOffset = sym.FileOffset };
            }
            _tree.Nodes.Add(ToTreeNode(node));
        }
        if (file.Symbols.Count == 0)
            _tree.Nodes.Add(new TreeNode("(no variables could be loaded - see File → Check integrity)") { ForeColor = Color.Red });
        foreach (TreeNode n in _tree.Nodes) n.Expand();
        _tree.EndUpdate();

        _types.Text = Program.TypesText(file);
        _hex.Text = HexDump(raw);
        _details.Text = report.Issues.Count > 0 ? report.ToString() : "";
        _status.Text = $"{file.Symbols.Count} persistent symbol(s), {file.DataTypes.Count} datatype(s), {raw.Length} bytes"
                       + $" - modified {File.GetLastWriteTime(path):yyyy-MM-dd HH:mm:ss} - " + report.Summary;
        _status.ForeColor = report.IsCorrupt ? Color.DarkRed : report.Warnings > 0 ? Color.DarkOrange : SystemColors.ControlText;
        if (report.IsCorrupt)
            MessageBox.Show(this,
                $"{path}\n\n{Shorten(report)}\n\nThe file was loaded as far as possible. Damaged variables are marked red; " +
                "PLC → Write to PLC only writes intact ones. TwinCAT may not load this file at PLC start. " +
                "Use File → Check integrity for all details.",
                "Corrupt bootdata file", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    static string Shorten(IntegrityReport r, int lines = 8) =>
        r.Summary + (r.Recovery != null ? "\n" + r.Recovery : "") + "\n\n" + string.Join("\n", r.Issues.Take(lines)) + (r.Issues.Count > lines ? $"\n... and {r.Issues.Count - lines} more" : "");

    // Re-checks the current state (including unsaved edits) and lists every issue.
    void ShowIntegrity()
    {
        if (_file == null) return;
        var report = BootdataCheck.Check(_file.ToBytes());
        BootdataCheck.CheckValues(_file, report);
        if (report.IsCorrupt) report.Recovery = BootdataCheck.Recovery(_file);
        _report = report;
        using var dlg = new Form
        {
            Text = "Integrity check - " + Path.GetFileName(_file.Path), Width = 900, Height = 480,
            StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, ShowIcon = false,
        };
        var box = MonoBox();
        box.Text = _file.Path + Environment.NewLine + Environment.NewLine + report;
        box.ForeColor = report.IsCorrupt ? Color.DarkRed : SystemColors.WindowText;
        dlg.Controls.Add(box);
        dlg.ShowDialog(this);
    }

    // A corrupt file is still shown, but saving or writing it to a PLC needs an explicit yes.
    bool ConfirmCorrupt(string action)
    {
        if (_report is not { IsCorrupt: true }) return true;
        return MessageBox.Show(this, $"This file is corrupt ({_report.Errors} error(s)).\n\n{action} anyway?",
                               "Corrupt bootdata file", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                               MessageBoxDefaultButton.Button2) == DialogResult.OK;
    }

    static TreeNode ToTreeNode(ValueNode v)
    {
        var text = v.Children.Count == 0 ? $"{v.Name} : {v.Type} = {v.Value}" : $"{v.Name} : {v.Type}";
        if (v.Damage != null) text += $"   ⚠ damaged: {v.Damage}";
        var node = new TreeNode(text) { Tag = v };
        if (v.Damage != null) node.ForeColor = Color.Red;
        foreach (var c in v.Children) node.Nodes.Add(ToTreeNode(c));
        return node;
    }

    void ShowDetails(ValueNode? v)
    {
        if (v == null || _file == null) return;
        var sym = _file.AllSymbols.FirstOrDefault(s => !s.IsContainer && v.FileOffset >= s.FileOffset && v.FileOffset < s.FileOffset + Math.Max(s.Data.Length, 1));
        var sb = new StringBuilder();
        sb.AppendLine($"Name:        {v.Name}");
        sb.AppendLine($"Type:        {v.Type}");
        sb.AppendLine($"Size:        {v.Size} byte(s)");
        sb.AppendLine($"Offset:      {v.Offset}{(v.BitOffset is int bit ? $", bit {bit}" : "")} (file 0x{v.FileOffset:X})");
        if (v.Children.Count == 0) sb.AppendLine($"Value:       {v.Value}");
        if (sym != null)
        {
            int start = (int)(v.FileOffset - sym.FileOffset);
            int len = Math.Min(v.Size, sym.Data.Length - start);
            sb.AppendLine();
            sb.AppendLine("Raw bytes:");
            sb.Append(HexDump(sym.Data.AsSpan(start, Math.Max(len, 0)).ToArray(), v.FileOffset));
        }
        _details.Text = sb.ToString();
    }

    // Pre-fills the name filter with the bootdata entry the selected tree node belongs to.
    void OpenWriteDialog()
    {
        if (_file == null) return;   // damaged variables are excluded inside the dialog
        string? filter = null;
        for (var n = _tree.SelectedNode; n != null && filter == null; n = n.Parent)
            filter = (n.Tag as ValueNode)?.SymbolPath;
        using var dlg = new WriteDialog(_file, filter, _plcTarget ?? AdsBootFiles.SourceTarget(_file.Path), _plcPort);
        dlg.ShowDialog(this);
    }

    void Export(string kind)
    {
        if (_file == null) return;
        using var dlg = new SaveFileDialog
        {
            Filter = kind == "json" ? "JSON (*.json)|*.json" : "Text (*.txt)|*.txt",
            FileName = Path.GetFileName(_file.Path) + "." + kind,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        File.WriteAllText(dlg.FileName, kind == "json" ? Program.ToJson(_file) : Program.DumpText(_file), Encoding.UTF8);
        _status.Text = $"Exported to {dlg.FileName}";
    }

    static string HexDump(byte[] data, long baseOffset = 0)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < data.Length; i += 16)
        {
            sb.Append((baseOffset + i).ToString("X8")).Append("  ");
            for (int j = 0; j < 16; j++)
            {
                sb.Append(i + j < data.Length ? data[i + j].ToString("x2") + " " : "   ");
                if (j == 7) sb.Append(' ');
            }
            sb.Append(' ');
            for (int j = 0; j < 16 && i + j < data.Length; j++)
            {
                byte c = data[i + j];
                sb.Append(c is >= 0x20 and < 0x7f ? (char)c : '.');
            }
            sb.Append("\r\n");
        }
        return sb.ToString();
    }
}
