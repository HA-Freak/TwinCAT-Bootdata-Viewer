namespace BootdataViewer;

// Edits one scalar value. The input is validated by encoding it into a scratch copy of the
// variable's bytes; Result holds those bytes when the dialog closes with OK.
public sealed class EditValueDialog : Form
{
    readonly ValueNode _node;
    readonly byte[] _data;
    readonly int _offset;
    readonly Control _input;
    readonly Label _error = new() { AutoSize = true, ForeColor = Color.DarkRed, MaximumSize = new Size(460, 0) };

    public byte[]? Result { get; private set; }

    public EditValueDialog(ValueNode node, byte[] symbolData, int offset)
    {
        _node = node;
        _data = symbolData;
        _offset = offset;
        Text = "Edit value";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var kind = node.Kind!.Value;
        _input = kind switch
        {
            ScalarKind.Bool or ScalarKind.Bit => Choice(new[] { "TRUE", "FALSE" }, ComboBoxStyle.DropDownList),
            ScalarKind.Enum => Choice(node.EnumValues!.Select(e => $"{e.Name} ({e.Value})").ToArray(), ComboBoxStyle.DropDown),
            _ => new TextBox { Width = 460, Font = new Font("Consolas", 10f) },
        };
        _input.Text = node.Value;

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label { Text = "Variable:", AutoSize = true }, 0, 0);
        layout.Controls.Add(new Label { Text = node.Name, AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, 1, 0);
        layout.Controls.Add(new Label { Text = "Type:", AutoSize = true }, 0, 1);
        layout.Controls.Add(new Label { Text = $"{node.Type}  ({node.Size} byte{(node.Size == 1 ? "" : "s")})", AutoSize = true }, 1, 1);
        layout.Controls.Add(new Label { Text = "Value:", AutoSize = true, Padding = new Padding(0, 4, 0, 0) }, 0, 2);
        layout.Controls.Add(_input, 1, 2);
        layout.Controls.Add(new Label { Text = Hint(kind, node.Size), AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(460, 0) }, 1, 3);
        layout.Controls.Add(_error, 1, 4);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.None, Width = 90 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        layout.Controls.Add(buttons, 1, 5);
        Controls.Add(layout);

        AcceptButton = ok;
        CancelButton = cancel;
        ok.Click += (_, _) => Accept();
        Shown += (_, _) => { _input.Focus(); if (_input is TextBox tb) tb.SelectAll(); };
    }

    static ComboBox Choice(string[] items, ComboBoxStyle style)
    {
        var c = new ComboBox { Width = 460, DropDownStyle = style, Font = new Font("Consolas", 10f) };
        c.Items.AddRange(items);
        return c;
    }

    void Accept()
    {
        var scratch = (byte[])_data.Clone();
        try
        {
            ValueEncoder.Write(_node.Kind!.Value, _node.EnumValues, _input.Text, scratch, _offset, _node.Size, _node.BitOffset);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            _error.Text = ex.Message;
            return;
        }
        Result = scratch;
        DialogResult = DialogResult.OK;
    }

    static string Hint(ScalarKind kind, int size) => kind switch
    {
        ScalarKind.SInt or ScalarKind.USInt or ScalarKind.Int or ScalarKind.UInt or ScalarKind.DInt
            or ScalarKind.UDInt or ScalarKind.LInt or ScalarKind.ULInt => "Decimal, or 16#FF / 2#1010.",
        ScalarKind.Real or ScalarKind.LReal => "Number with '.' as decimal separator, e.g. 3.25 or 1.5E3.",
        ScalarKind.String => $"Text, at most {size - 1} characters. Quotes are optional.",
        ScalarKind.WString => $"Text, at most {size / 2 - 1} characters. Quotes are optional.",
        ScalarKind.Time or ScalarKind.LTime => "Duration, e.g. T#1h30m, T#250ms, or plain milliseconds.",
        ScalarKind.Tod or ScalarKind.LTod => "Time of day, e.g. TOD#13:45:00.",
        ScalarKind.Date or ScalarKind.LDate => "Date, e.g. D#2026-10-27.",
        ScalarKind.Dt or ScalarKind.Ldt => "Date and time, e.g. DT#2026-10-27-13:45:00.",
        ScalarKind.Enum => "Pick a member, or enter its name or number.",
        ScalarKind.Pointer => "Address as hex, e.g. 0x00000000.",
        ScalarKind.Raw => $"Exactly {size} byte(s) as hex.",
        _ => "",
    };
}
