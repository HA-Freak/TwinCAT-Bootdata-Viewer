using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace BootdataViewer.VsExtension
{
    // Asks which PLC project is meant when the TwinCAT project has several and none is selected.
    internal sealed class PlcChooser : Form
    {
        readonly ListBox _list = new ListBox { Width = 360, Height = 120, IntegralHeight = false };

        public PlcInfo? Selected => _list.SelectedItem as PlcInfo;

        public PlcChooser(string project, string netId, IEnumerable<PlcInfo> plcs)
        {
            Text = "Select PLC";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12);

            _list.Items.AddRange(plcs.Cast<object>().ToArray());
            _list.SelectedIndex = 0;
            _list.DoubleClick += (_, __) => { if (Selected != null) DialogResult = DialogResult.OK; };

            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 90 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);

            var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
            layout.Controls.Add(new Label { Text = $"{project} on target {netId}:", AutoSize = true, Margin = new Padding(0, 0, 0, 6) });
            layout.Controls.Add(_list);
            layout.Controls.Add(buttons);
            Controls.Add(layout);
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }
}
