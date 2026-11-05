using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;

namespace BootdataViewer.VsExtension
{
    internal sealed class PlcInfo
    {
        public string Name = "";
        public int Port;
        public override string ToString() => $"{Name}  (port {Port})";
    }

    // The TwinCAT project the user works on: its active target and the PLC projects with their ADS ports.
    internal sealed class TwinCatContext
    {
        public Project Project = null!;
        public string NetId = "";
        public List<PlcInfo> Plcs = new List<PlcInfo>();
        public string? SelectedPlc;     // PLC project selected in Solution Explorer, if any

        // Finds the TwinCAT project of the current selection, or the only/first one in the solution.
        // Returns null with a reason when there is none.
        public static TwinCatContext? Resolve(DTE2 dte, out string? error)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            error = null;
            var projects = AllProjects(dte).ToList();
            var twinCat = projects.Where(IsTwinCatProject).ToList();
            if (twinCat.Count == 0)
            {
                error = "The solution contains no TwinCAT project (.tsproj).";
                return null;
            }

            // A selected PLC project (.plcproj) or TwinCAT project decides which one is meant.
            Project? selected = SelectedProject(dte);
            string? selectedPlc = null;
            Project? project = null;
            if (selected != null && IsTwinCatProject(selected)) project = selected;
            else if (selected != null && IsPlcProject(selected))
            {
                selectedPlc = selected.Name;
                string plcDir = Path.GetDirectoryName(selected.FullName) ?? "";
                foreach (var p in twinCat)
                    if (plcDir.StartsWith(Path.GetDirectoryName(p.FullName) ?? "\0", StringComparison.OrdinalIgnoreCase)) { project = p; break; }
            }
            project ??= twinCat[0];

            string? netId = TargetNetId(project);
            if (string.IsNullOrEmpty(netId))
            {
                error = $"Could not read the active target of '{project.Name}' from TwinCAT XAE.";
                return null;
            }
            var plcs = PlcPorts(project.FullName);
            if (plcs.Count == 0)
            {
                error = $"'{project.Name}' contains no PLC project.";
                return null;
            }
            return new TwinCatContext { Project = project, NetId = netId!, Plcs = plcs, SelectedPlc = selectedPlc };
        }

        public static bool IsTwinCatProject(Project p)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string file = SafeFullName(p);
            return file.EndsWith(".tsproj", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".tspproj", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsPlcProject(Project p)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return SafeFullName(p).EndsWith(".plcproj", StringComparison.OrdinalIgnoreCase);
        }

        // The TwinCAT project's automation object is ITcSysManager (TwinCAT Automation Interface);
        // it is called late bound, so no TwinCAT assembly has to be referenced.
        static string? TargetNetId(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                dynamic sysManager = project.Object;
                return (string)sysManager.GetTargetNetId();
            }
            catch (Exception) { return null; }
        }

        // PLC projects are <Project Name=".." AmsPort="851" ...> elements in the .tsproj, or in .xti
        // files when the PLC is saved as an independent project file.
        public static List<PlcInfo> PlcPorts(string tsprojPath)
        {
            var files = new List<string> { tsprojPath };
            string? dir = Path.GetDirectoryName(tsprojPath);
            if (dir != null) files.AddRange(Directory.EnumerateFiles(dir, "*.xti", SearchOption.AllDirectories));
            var result = new List<PlcInfo>();
            foreach (var file in files)
            {
                try
                {
                    foreach (var e in XDocument.Load(file).Descendants().Where(e => e.Name.LocalName == "Project"))
                        if (int.TryParse((string?)e.Attribute("AmsPort"), out int port) && result.All(p => p.Port != port))
                            result.Add(new PlcInfo { Name = (string?)e.Attribute("Name") ?? $"Port {port}", Port = port });
                }
                catch (Exception) { /* unreadable or not XML: skip */ }
            }
            return result.OrderBy(p => p.Port).ToList();
        }

        public static Project? SelectedProject(DTE2 dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (dte.SelectedItems.Count == 0) return null;
                var item = dte.SelectedItems.Item(1);
                return item.Project ?? item.ProjectItem?.ContainingProject;
            }
            catch (Exception) { return null; }
        }

        public static string? SelectedFile(DTE2 dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (dte.SelectedItems.Count != 1) return null;
                var item = dte.SelectedItems.Item(1).ProjectItem;
                return item != null && item.FileCount > 0 ? item.FileNames[1] : null;
            }
            catch (Exception) { return null; }
        }

        static IEnumerable<Project> AllProjects(DTE2 dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var stack = new Stack<Project>();
            foreach (Project p in dte.Solution.Projects) stack.Push(p);
            while (stack.Count > 0)
            {
                var p = stack.Pop();
                yield return p;
                ProjectItems? items = null;
                try { items = p.ProjectItems; } catch (Exception) { }
                if (items == null) continue;
                foreach (ProjectItem item in items)
                {
                    Project? sub = null;
                    try { sub = item.SubProject; } catch (Exception) { }
                    if (sub != null) stack.Push(sub);
                }
            }
        }

        static string SafeFullName(Project p)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { return p.FullName ?? ""; } catch (Exception) { return ""; }
        }
    }
}
