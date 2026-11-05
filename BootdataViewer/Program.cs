using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BootdataViewer;

static class Program
{
    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);

    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    // BootdataViewer.exe                     -> GUI
    // BootdataViewer.exe <file>              -> GUI with file opened
    // BootdataViewer.exe [<file>] --target <AmsNetId> [--port <n>] [--old] [--write-dialog]
    //                                        -> GUI; see GuiOptions
    // BootdataViewer.exe --dump <file>       -> text tree on stdout
    // BootdataViewer.exe --json <file>       -> JSON on stdout
    // BootdataViewer.exe --types <file>      -> embedded datatype table on stdout
    // BootdataViewer.exe --write <file> [--target <AmsNetId>|local] [--port <n>] [--filter <prefix>] [--yes]
    //                                        -> compare file values with the PLC; with --yes write the differing ones
    // BootdataViewer.exe --check <file>      -> integrity check; exit code 0 intact, 1 value warnings, 2 corrupt
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--check")
        {
            AttachConsole(-1);
            Console.OutputEncoding = Encoding.UTF8;
            try
            {
                var report = BootdataCheck.CheckFile(args[1]);
                Console.WriteLine(report);
                return report.IsCorrupt ? 2 : report.Warnings > 0 ? 1 : 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                return 2;
            }
        }
        if (args.Length >= 2 && args[0] == "--write")
        {
            AttachConsole(-1);
            Console.OutputEncoding = Encoding.UTF8;
            try { return WriteCommand(args); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                return 1;
            }
        }
        if (args.Length >= 2 && args[0] is "--dump" or "--json" or "--types")
        {
            AttachConsole(-1);
            Console.OutputEncoding = Encoding.UTF8;
            try
            {
                var file = BootdataFile.Load(args[1]);
                Console.WriteLine(args[0] switch
                {
                    "--json" => ToJson(file),
                    "--types" => TypesText(file),
                    _ => DumpText(file),
                });
                foreach (var w in file.Warnings) Console.Error.WriteLine("warning: " + w);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                return 1;
            }
        }

#if NET
        ApplicationConfiguration.Initialize();
#else
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        SetProcessDPIAware();   // like HighDpiMode.SystemAware on .NET 8; keeps text sharp
#endif
        Application.Run(new MainForm(GuiOptions(args)));
        return 0;
    }

    // GUI arguments, also used by the Visual Studio extension:
    //   [<file>] [--target <AmsNetId>|local] [--port <n>] [--old] [--write-dialog]
    // Without a file, --target downloads Plc/Port_<n>.bootdata (--old: the -old copy) from that target.
    // --write-dialog opens "Write to PLC" for that target right away.
    static StartOptions GuiOptions(string[] args)
    {
        string? path = null, target = null;
        int? port = null;
        bool old = false, write = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--target" when i + 1 < args.Length: target = args[++i]; break;
                case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out int p): port = p; i++; break;
                case "--old": old = true; break;
                case "--write-dialog": write = true; break;
                default: if (path == null && File.Exists(args[i])) path = args[i]; break;
            }
        }
        return new StartOptions(path, target, port, old, write);
    }

    static int WriteCommand(string[] args)
    {
        var file = BootdataFile.Load(args[1]);
        string target = "local";
        int port = DefaultPort(file.Path);
        string? filter = null;
        bool yes = false;
        for (int i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--target" when i + 1 < args.Length: target = args[++i]; break;
                case "--port" when i + 1 < args.Length: port = int.Parse(args[++i]); break;
                case "--filter" when i + 1 < args.Length: filter = args[++i]; break;
                case "--yes": yes = true; break;
                default: throw new ArgumentException($"Unknown option '{args[i]}'.");
            }
        }

        var integrity = BootdataCheck.CheckFile(file.Path);
        // Damaged entries get status Damaged in Compare and are never written; intact ones still are.
        if (integrity.IsCorrupt)
            Console.Error.WriteLine("warning: " + integrity.Summary + " " + integrity.Recovery + " Run --check for details.");

        using var writer = new AdsWriter(file, target, port);
        Console.WriteLine($"Connected to {target}:{port} (PLC state {writer.PlcState})");
        try
        {
            var check = writer.CheckPersistentSave();
            if (check.HasPersistentSave)
                Console.WriteLine("Persistent data saved by: " + string.Join(", ", check.Instances.Select(i => $"{i.Path} ({i.Type})")));
            else
                Console.Error.WriteLine("warning: " + check.Warning);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("warning: could not check for FB_S_UPS / FB_WritePersistentData: " + ex.Message);
        }
        if (yes && !writer.IsRunning)
            throw new InvalidOperationException($"The PLC is in {writer.PlcState}, not in Run. Start the PLC before writing.");
        var items = writer.Compare(AdsWriter.Leaves(file, filter));
        var toWrite = items.Where(i => i.Status == WriteStatus.Differs).ToList();
        if (yes) writer.Write(toWrite);

        var dec = new ValueDecoder(file);
        foreach (var item in items)
        {
            Console.Write($"{item.Status,-13} {item.Name}");
            if (item.Message.Length > 0) Console.Write($"  ({item.Message})");
            else if (item.Status is WriteStatus.Differs or WriteStatus.Written)
                Console.Write($"  PLC {(item.Status == WriteStatus.Written ? "now" : "has")} {ShortValue(dec, item.Symbol, item.PlcData)}, file {ShortValue(dec, item.Symbol, null)}");
            Console.WriteLine();
        }

        var counts = items.GroupBy(i => i.Status).Select(g => $"{g.Count()} {g.Key}");
        Console.WriteLine();
        Console.WriteLine(string.Join(", ", counts));
        if (!yes && toWrite.Count > 0)
            Console.WriteLine($"Dry run: nothing written. Add --yes to write the {toWrite.Count} differing value(s).");
        return items.Any(i => i.Status == WriteStatus.Failed) ? 2 : 0;
    }

    public static int DefaultPort(string path)
    {
        var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(path), @"Port_(\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : 851;
    }

    // Formats a value for one-line output; structured values are summarised.
    public static string ShortValue(ValueDecoder dec, SymbolEntry sym, byte[]? data)
    {
        var copy = new SymbolEntry
        {
            Name = sym.Name, FullName = sym.FullName, Size = sym.Size, Flags = sym.Flags,
            TypeGuid = sym.TypeGuid, Data = data ?? sym.Data,
        };
        var node = dec.Decode(copy);
        return node.Children.Count == 0 ? node.Value : $"{{{node.Children.Count} elements}}";
    }

    public static string DumpText(BootdataFile file)
    {
        var sb = new StringBuilder();
        var dec = new ValueDecoder(file);
        foreach (var sym in file.Symbols) Append(sb, dec.Decode(sym), 0);
        return sb.ToString().TrimEnd();

        static void Append(StringBuilder sb, ValueNode n, int indent)
        {
            sb.Append(' ', indent * 2).Append(n.Name).Append(" : ").Append(n.Type);
            if (n.Children.Count == 0) sb.Append(" := ").Append(n.Value);
            sb.AppendLine();
            foreach (var c in n.Children) Append(sb, c, indent + 1);
        }
    }

    public static string ToJson(BootdataFile file)
    {
        var dec = new ValueDecoder(file);
        var root = new JsonObject();
        foreach (var sym in file.Symbols) root[sym.Name] = dec.Decode(sym).ToJson();
        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    // Attributes the compiler adds on its own; they never appear in the source declaration.
    static readonly HashSet<string> GeneratedAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "textlistenumeration", "generate_implicit_init_function", "DisplayMinValue", "DisplayMaxValue",
    };

    // Built-in IEC types carry GUIDs of the form 18071995-0000-0000-0000-xxxxxxxxxxxx.
    static bool IsBuiltIn(DataTypeEntry t) =>
        t.TypeGuid is Guid g && g.ToString().StartsWith("18071995-0000-0000-0000-", StringComparison.Ordinal);

    // Anonymous types the compiler creates for declarations like ARRAY [..] OF X or POINTER TO X.
    static bool IsImplicit(DataTypeEntry t) =>
        t.Name.Contains(' ') || t.Name.Contains('(') || t.Name.Contains('[');

    // Renders the user-defined datatypes as IEC 61131-3 structured text declarations.
    public static string TypesText(BootdataFile file)
    {
        var sb = new StringBuilder();
        foreach (var t in file.DataTypes.Where(t => !IsBuiltIn(t) && !IsImplicit(t)))
        {
            AppendAttributes(sb, t, "");
            sb.Append("TYPE ").Append(t.Name).Append(" :");
            if (t.Comment.Length > 0) sb.Append(" // ").Append(t.Comment);
            sb.AppendLine();
            if (t.EnumValues.Count > 0)
            {
                sb.AppendLine("(");
                for (int i = 0; i < t.EnumValues.Count; i++)
                    sb.Append('\t').Append(t.EnumValues[i].Name).Append(" := ").Append(t.EnumValues[i].Value)
                      .AppendLine(i < t.EnumValues.Count - 1 ? "," : "");
                bool defaultBase = t.TypeName.Length == 0 || t.TypeName.Equals("INT", StringComparison.OrdinalIgnoreCase);
                sb.AppendLine(defaultBase ? ");" : $") {t.TypeName};");
            }
            else if (t.SubItems.Count > 0)
            {
                sb.AppendLine("STRUCT");
                foreach (var m in t.SubItems)
                {
                    AppendAttributes(sb, m, "\t");
                    sb.Append('\t').Append(m.Name).Append(" : ").Append(m.TypeName).Append(';');
                    if (m.Comment.Length > 0) sb.Append(" // ").Append(m.Comment);
                    sb.AppendLine();
                }
                sb.AppendLine("END_STRUCT");
            }
            else
            {
                sb.Append(t.TypeName).AppendLine(";");
            }
            sb.AppendLine("END_TYPE").AppendLine();
        }
        var builtIns = file.DataTypes.Where(IsBuiltIn).Select(t => t.Name).ToList();
        if (builtIns.Count > 0) sb.AppendLine("// Built-in types used: " + string.Join(", ", builtIns));
        return sb.ToString().TrimEnd();

        static void AppendAttributes(StringBuilder sb, DataTypeEntry t, string indent)
        {
            foreach (var a in t.Attributes.Where(a => !GeneratedAttributes.Contains(a.Key)))
                sb.Append(indent).AppendLine($"{{attribute '{a.Key}'{(a.Value.Length > 0 ? $" := '{a.Value}'" : "")}}}");
        }
    }
}
