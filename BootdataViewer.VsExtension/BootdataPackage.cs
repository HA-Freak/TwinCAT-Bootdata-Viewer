using System;
using System.ComponentModel.Design;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace BootdataViewer.VsExtension
{
    // TwinCAT > Bootdata (main menu of TwinCAT XAE), the TwinCAT/PLC project context menu, and .bootdata files.
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("TwinCAT XAE Bootdata Viewer", "Opens, checks and restores TwinCAT persistent data.", "1.4")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [Guid(PackageGuidString)]
    // Loaded in the background once a solution is open, so the context-menu entries can decide
    // whether they apply to the selected project.
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideEditorFactory(typeof(BootdataEditorFactory), 110, TrustLevel = __VSEDITORTRUSTLEVEL.ETL_AlwaysTrusted)]
    [ProvideEditorExtension(typeof(BootdataEditorFactory), ".bootdata", 50, NameResourceID = 110)]
    [ProvideEditorExtension(typeof(BootdataEditorFactory), ".bootdata-old", 50, NameResourceID = 110)]
    [ProvideEditorLogicalView(typeof(BootdataEditorFactory), "{7651A701-06E5-11D1-8EBD-00A0C90F26EA}")]   // LOGVIEWID_Primary
    public sealed class BootdataPackage : AsyncPackage
    {
        public const string PackageGuidString = "8E3B1C52-6A7D-4F21-9C0E-3D5B7A1F2E84";
        static readonly Guid CommandSet = new Guid("C4A9E2F1-5B3D-4E68-A7C1-9F0B2D4E6A13");
        const int CmdOpenActive = 0x0100, CmdOpenActiveOld = 0x0101, CmdWriteActive = 0x0102, CmdOpenViewer = 0x0103;
        const int CmdOpenActiveCtx = 0x0110, CmdWriteActiveCtx = 0x0111, CmdOpenFileCtx = 0x0120;

        static BootdataPackage? _instance;
        DTE2? _dte;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            _instance = this;
            if (await GetServiceAsync(typeof(DTE)) is not DTE2 dte) return;   // no automation model: the commands could not find the TwinCAT project
            _dte = dte;
            RegisterEditorFactory(new BootdataEditorFactory());

            if (await GetServiceAsync(typeof(IMenuCommandService)) is not OleMenuCommandService commands) return;
            Add(commands, CmdOpenActive, (_, __) => RunOnActivePlc(old: false, write: false), HasTwinCatProject);
            Add(commands, CmdOpenActiveOld, (_, __) => RunOnActivePlc(old: true, write: false), HasTwinCatProject);
            Add(commands, CmdWriteActive, (_, __) => RunOnActivePlc(old: false, write: true), HasTwinCatProject);
            Add(commands, CmdOpenViewer, (_, __) => Launch(), null);
            Add(commands, CmdOpenActiveCtx, (_, __) => RunOnActivePlc(old: false, write: false), SelectionIsTwinCat, hideWhenOff: true);
            Add(commands, CmdWriteActiveCtx, (_, __) => RunOnActivePlc(old: false, write: true), SelectionIsTwinCat, hideWhenOff: true);
            Add(commands, CmdOpenFileCtx, (_, __) => { if (SelectedBootdataFile() is string f) Launch(f); }, () => SelectedBootdataFile() != null, hideWhenOff: true);
        }

        void Add(OleMenuCommandService service, int id, EventHandler handler, Func<bool>? available, bool hideWhenOff = false)
        {
            var command = new OleMenuCommand(handler, new CommandID(CommandSet, id));
            if (available != null)
                command.BeforeQueryStatus += (_, __) =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    bool on = available();
                    command.Enabled = on;
                    if (hideWhenOff) command.Visible = on;
                };
            service.AddCommand(command);
        }

        bool HasTwinCatProject()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_dte?.Solution?.IsOpen != true) return false;
            foreach (Project p in _dte.Solution.Projects)
                if (TwinCatContext.IsTwinCatProject(p) || HasNested(p)) return true;
            return false;

            static bool HasNested(Project p)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                try
                {
                    foreach (ProjectItem item in p.ProjectItems)
                        if (item.SubProject is Project sub && (TwinCatContext.IsTwinCatProject(sub) || HasNested(sub))) return true;
                }
                catch (Exception) { }
                return false;
            }
        }

        bool SelectionIsTwinCat()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_dte == null) return false;
            var p = TwinCatContext.SelectedProject(_dte);
            return p != null && (TwinCatContext.IsTwinCatProject(p) || TwinCatContext.IsPlcProject(p));
        }

        string? SelectedBootdataFile()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_dte == null) return null;
            var file = TwinCatContext.SelectedFile(_dte);
            return file != null && (file.EndsWith(".bootdata", StringComparison.OrdinalIgnoreCase)
                                    || file.EndsWith(".bootdata-old", StringComparison.OrdinalIgnoreCase)) ? file : null;
        }

        // Opens (old) or compares/writes (write) the bootdata of the selected PLC on the active target.
        void RunOnActivePlc(bool old, bool write)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_dte == null) return;
            var context = TwinCatContext.Resolve(_dte, out string? error);
            if (context == null)
            {
                ShowError(error ?? "No TwinCAT project found.");
                return;
            }

            PlcInfo? plc = context.Plcs.Count == 1 ? context.Plcs[0]
                : context.Plcs.Find(p => string.Equals(p.Name, context.SelectedPlc, StringComparison.OrdinalIgnoreCase));
            if (plc == null)
            {
                using var chooser = new PlcChooser(context.Project.Name, context.NetId, context.Plcs);
                if (chooser.ShowDialog(MainWindow()) != DialogResult.OK || chooser.Selected == null) return;
                plc = chooser.Selected;
            }

            _dte.StatusBar.Text = $"Bootdata viewer: reading Port_{plc.Port}.bootdata{(old ? "-old" : "")} of {plc.Name} from {context.NetId}...";
            var args = new System.Collections.Generic.List<string> { "--target", context.NetId, "--port", plc.Port.ToString() };
            if (old) args.Add("--old");
            if (write) args.Add("--write-dialog");
            Launch(args.ToArray());
        }

        static void Launch(params string[] args)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { ViewerLauncher.Start(args); }
            catch (Exception ex) { ShowError("Could not start the bootdata viewer: " + ex.Message); }
        }

        IWin32Window? MainWindow()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { return _dte != null ? new Win32Window(_dte.MainWindow.HWnd) : null; }
            catch (Exception) { return null; }
        }

        internal static void ShowError(string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_instance == null) return;
            VsShellUtilities.ShowMessageBox(_instance, message, "TwinCAT XAE Bootdata Viewer",
                OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }

        sealed class Win32Window : IWin32Window
        {
            public Win32Window(IntPtr handle) => Handle = handle;
            public IntPtr Handle { get; }
        }
    }
}
