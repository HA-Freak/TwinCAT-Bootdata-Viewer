using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using IOleServiceProvider = Microsoft.VisualStudio.OLE.Interop.IServiceProvider;

namespace BootdataViewer.VsExtension
{
    // Opening a .bootdata file in Visual Studio (double-click, File > Open, Open With) starts the
    // bootdata viewer with it. No document window is created inside Visual Studio; returning
    // OLE_E_PROMPTSAVECANCELLED makes the shell treat the open as cancelled, without an error.
    [Guid(GuidString)]
    internal sealed class BootdataEditorFactory : IVsEditorFactory
    {
        public const string GuidString = "3F6A0C9B-2D41-4B8E-A5C7-81E2D9F04B36";
        string? _lastFile;
        DateTime _lastStart;

        public int SetSite(IOleServiceProvider psp) => VSConstants.S_OK;

        public int Close() => VSConstants.S_OK;

        public int MapLogicalView(ref Guid rguidLogicalView, out string? pbstrPhysicalView)
        {
            pbstrPhysicalView = null;
            return rguidLogicalView == VSConstants.LOGVIEWID_Primary ? VSConstants.S_OK : VSConstants.E_NOTIMPL;
        }

        public int CreateEditorInstance(uint grfCreateDoc, string pszMkDocument, string pszPhysicalView, IVsHierarchy pvHier,
            uint itemid, IntPtr punkDocDataExisting, out IntPtr ppunkDocView, out IntPtr ppunkDocData,
            out string pbstrEditorCaption, out Guid pguidCmdUI, out int pgrfCDW)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ppunkDocView = IntPtr.Zero;
            ppunkDocData = IntPtr.Zero;
            pbstrEditorCaption = "";
            pguidCmdUI = Guid.Empty;
            pgrfCDW = 0;
            if ((grfCreateDoc & (VSConstants.CEF_OPENFILE | VSConstants.CEF_SILENT)) == 0) return VSConstants.E_INVALIDARG;
            // The shell retries a cancelled open once; start the viewer only for the first call.
            if (string.Equals(_lastFile, pszMkDocument, StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow - _lastStart < TimeSpan.FromSeconds(3))
                return VSConstants.OLE_E_PROMPTSAVECANCELLED;
            _lastFile = pszMkDocument;
            _lastStart = DateTime.UtcNow;
            try
            {
                ViewerLauncher.Start(pszMkDocument);
            }
            catch (Exception ex)
            {
                BootdataPackage.ShowError("Could not start the bootdata viewer: " + ex.Message);
            }
            return VSConstants.OLE_E_PROMPTSAVECANCELLED;
        }
    }
}
