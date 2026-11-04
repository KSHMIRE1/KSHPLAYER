using System;
using System.Runtime.InteropServices;
using static KSHPLAYER.Apercu.NativeMethods;

namespace KSHPLAYER.Apercu
{
    internal enum ShellKind { None, Explorer, Desktop }

    /// <summary>
    /// Lit (et règle) la sélection de l'Explorateur au premier plan ou du Bureau, par les interfaces COM du Shell
    /// (même principe que QuickLook / Orbit Look). À appeler depuis un seul fil STA (celui de l'aperçu).
    /// </summary>
    internal sealed class ShellSelection : IDisposable
    {
        private IShellWindows _windows;

        /// <summary>Fenêtre de l'Explorateur ou Bureau ? (vérification rapide, sans COM)</summary>
        public static ShellKind KindOf(IntPtr hwnd)
        {
            switch (ClassName(hwnd))
            {
                case "CabinetWClass":
                case "ExploreWClass":
                    return ShellKind.Explorer;
                case "Progman":
                case "WorkerW":
                    return ShellKind.Desktop;
                default:
                    return ShellKind.None;
            }
        }

        /// <summary>
        /// Le focus clavier est-il dans la liste des fichiers ? (pas dans un renommage, la barre d'adresse,
        /// la recherche, le volet de navigation ou un menu). Rapide : utilisable dans le crochet clavier.
        /// </summary>
        public static bool FocusInFileList(IntPtr fg, ShellKind kind)
        {
            uint tid = GetWindowThreadProcessId(fg, out _);
            if (tid == 0) return false;
            var gti = new GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(GUITHREADINFO)) };
            if (!GetGUIThreadInfo(tid, ref gti) || gti.hwndFocus == IntPtr.Zero) return false;
            if ((gti.flags & (GUI_INMENUMODE | GUI_POPUPMENUMODE | GUI_INMOVESIZE)) != 0) return false;
            // renommage en cours = le focus est dans un champ « Edit » : refusé par le test ci-dessous
            string focus = ClassName(gti.hwndFocus);
            string parent = ClassName(GetParent(gti.hwndFocus));
            if (kind == ShellKind.Explorer) return focus == "DirectUIHWND" && parent == "SHELLDLL_DefView";
            if (kind == ShellKind.Desktop) return focus == "SysListView32" && parent == "SHELLDLL_DefView";
            return false;
        }

        /// <summary>Chemin du fichier sélectionné dans la fenêtre donnée (null si rien ou pas un fichier).</summary>
        public string SelectedPath(IntPtr fg, ShellKind kind)
        {
            IShellView view = null;
            try
            {
                view = ActiveView(fg, kind);
                return view == null ? null : FirstSelected(view);
            }
            catch (Exception ex)
            {
                Forget(ex);
                return null;
            }
            finally { Release(view); }
        }

        /// <summary>Sélectionne ce fichier dans la fenêtre (l'Explorateur suit l'aperçu, comme Quick Look).</summary>
        public void Select(IntPtr fg, ShellKind kind, string path)
        {
            IShellView view = null;
            IntPtr pidl = IntPtr.Zero;
            try
            {
                if (!IsWindow(fg)) return;
                view = ActiveView(fg, kind);
                if (view == null) return;
                pidl = ILCreateFromPathW(path);
                if (pidl == IntPtr.Zero) return;
                view.SelectItem(ILFindLastID(pidl), SVSI_SELECT | SVSI_DESELECTOTHERS | SVSI_ENSUREVISIBLE | SVSI_FOCUSED);
            }
            catch (Exception ex) { Forget(ex); }
            finally
            {
                if (pidl != IntPtr.Zero) ILFree(pidl);
                Release(view);
            }
        }

        // ---------------------------------------------------------------------------------------------

        private IShellWindows Windows
        {
            get
            {
                if (_windows == null)
                    _windows = (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_ShellWindows, true));
                return _windows;
            }
        }

        /// <summary>Explorateur redémarré, etc. : on repartira d'une connexion neuve.</summary>
        private void Forget(Exception ex)
        {
            Log.Write("Sélection de l'Explorateur illisible : " + ex.Message);
            Release(_windows);
            _windows = null;
        }

        private IShellView ActiveView(IntPtr fg, ShellKind kind)
        {
            if (kind == ShellKind.Desktop)
            {
                object loc = CSIDL_DESKTOP, root = null;
                object disp = Windows.FindWindowSW(ref loc, ref root, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH);
                try { return ViewOf(disp, out _); }
                finally { Release(disp); }
            }

            // Windows 11 : plusieurs onglets dans la même fenêtre ; l'onglet actif est le premier « ShellTabWindowClass »
            IntPtr activeTab = FindWindowEx(fg, IntPtr.Zero, "ShellTabWindowClass", null);
            int count = Windows.Count;
            for (int i = 0; i < count; i++)
            {
                object disp = null;
                IShellView view = null;
                try
                {
                    disp = Windows.Item(i);
                    if (disp == null) continue;
                    view = ViewOf(disp, out IntPtr browserHwnd);
                    if (view == null) continue;
                    bool match = activeTab != IntPtr.Zero
                        ? browserHwnd == activeTab
                        : browserHwnd == fg || GetAncestor(browserHwnd, GA_ROOT) == fg;
                    if (match)
                    {
                        var found = view;
                        view = null;   // gardée : rendue à l'appelant
                        return found;
                    }
                }
                catch (COMException) { /* fenêtre en cours de fermeture : on passe */ }
                finally
                {
                    Release(view);
                    Release(disp);
                }
            }
            return null;
        }

        /// <summary>Fenêtre du Shell (IDispatch) → vue active (IShellView) et fenêtre de son navigateur.</summary>
        private static IShellView ViewOf(object disp, out IntPtr browserHwnd)
        {
            browserHwnd = IntPtr.Zero;
            if (!(disp is IServiceProvider sp)) return null;
            Guid sid = SID_STopLevelBrowser, iid = IID_IShellBrowser;
            if (sp.QueryService(ref sid, ref iid, out IntPtr pBrowser) != 0 || pBrowser == IntPtr.Zero) return null;
            IShellBrowser browser = null;
            try
            {
                browser = (IShellBrowser)Marshal.GetObjectForIUnknown(pBrowser);
                browser.GetWindow(out browserHwnd);
                return browser.QueryActiveShellView(out IShellView view) == 0 ? view : null;
            }
            finally
            {
                Marshal.Release(pBrowser);
                Release(browser);
            }
        }

        private static string FirstSelected(IShellView view)
        {
            if (!(view is IFolderView fv)) return null;
            Guid iid = IID_IShellItemArray;
            if (fv.Items(SVGIO_SELECTION, ref iid, out IntPtr pArray) != 0 || pArray == IntPtr.Zero) return null;
            IShellItemArray array = null;
            IShellItem item = null;
            try
            {
                array = (IShellItemArray)Marshal.GetObjectForIUnknown(pArray);
                if (array.GetCount(out uint n) != 0 || n == 0) return null;
                if (array.GetItemAt(0, out item) != 0 || item == null) return null;
                if (item.GetDisplayName(SIGDN_FILESYSPATH, out IntPtr psz) != 0 || psz == IntPtr.Zero) return null;
                try { return Marshal.PtrToStringUni(psz); }
                finally { Marshal.FreeCoTaskMem(psz); }
            }
            finally
            {
                Marshal.Release(pArray);
                Release(item);
                Release(array);
            }
        }

        private static void Release(object o)
        {
            try { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); } catch { }
        }

        public void Dispose()
        {
            Release(_windows);
            _windows = null;
        }

        // ----- Interfaces COM du Shell (seulement ce qui sert ; l'ordre des méthodes doit suivre Windows) -----

        private static readonly Guid CLSID_ShellWindows = new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
        private static readonly Guid SID_STopLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
        private static readonly Guid IID_IShellBrowser = new Guid("000214E2-0000-0000-C000-000000000046");
        private static readonly Guid IID_IShellItemArray = new Guid("B63EA76D-1F85-456F-A19C-48159EFA858B");
        private const int CSIDL_DESKTOP = 0, SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1;
        private const uint SVGIO_SELECTION = 1;
        private const uint SIGDN_FILESYSPATH = 0x80058000;
        private const uint SVSI_SELECT = 0x1, SVSI_DESELECTOTHERS = 0x4, SVSI_ENSUREVISIBLE = 0x8, SVSI_FOCUSED = 0x10;

        [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface IShellWindows
        {
            int Count { get; }
            [return: MarshalAs(UnmanagedType.IDispatch)] object Item([In, MarshalAs(UnmanagedType.Struct)] object index);
            [return: MarshalAs(UnmanagedType.IUnknown)] object _NewEnum();
            void Register([MarshalAs(UnmanagedType.IDispatch)] object pid, int hwnd, int swClass, out int plCookie);
            void RegisterPending(int lThreadId, [In, MarshalAs(UnmanagedType.Struct)] ref object pvarloc,
                                 [In, MarshalAs(UnmanagedType.Struct)] ref object pvarlocRoot, int swClass, out int plCookie);
            void Revoke(int lCookie);
            void OnNavigate(int lCookie, [In, MarshalAs(UnmanagedType.Struct)] ref object pvarLoc);
            void OnActivated(int lCookie, [MarshalAs(UnmanagedType.VariantBool)] bool fActive);
            [return: MarshalAs(UnmanagedType.IDispatch)]
            object FindWindowSW([In, MarshalAs(UnmanagedType.Struct)] ref object pvarLoc,
                                [In, MarshalAs(UnmanagedType.Struct)] ref object pvarLocRoot,
                                int swClass, out int phwnd, int swfwOptions);
            void OnCreated(int lCookie, [MarshalAs(UnmanagedType.IUnknown)] object punk);
            [return: MarshalAs(UnmanagedType.VariantBool)] bool ProcessAttachDetach([MarshalAs(UnmanagedType.VariantBool)] bool fAttach);
        }

        [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IServiceProvider
        {
            [PreserveSig] int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
        }

        [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellBrowser
        {
            [PreserveSig] int GetWindow(out IntPtr phwnd);
            [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool fEnterMode);
            [PreserveSig] int InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths);
            [PreserveSig] int SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject);
            [PreserveSig] int RemoveMenusSB(IntPtr hmenuShared);
            [PreserveSig] int SetStatusTextSB(IntPtr pszStatusText);
            [PreserveSig] int EnableModelessSB([MarshalAs(UnmanagedType.Bool)] bool fEnable);
            [PreserveSig] int TranslateAcceleratorSB(IntPtr pmsg, ushort wID);
            [PreserveSig] int BrowseObject(IntPtr pidl, uint wFlags);
            [PreserveSig] int GetViewStateStream(uint grfMode, out IntPtr ppStrm);
            [PreserveSig] int GetControlWindow(uint id, out IntPtr phwnd);
            [PreserveSig] int SendControlMsg(uint id, uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr pret);
            [PreserveSig] int QueryActiveShellView(out IShellView ppshv);
            [PreserveSig] int OnViewWindowActive(IShellView pshv);
            [PreserveSig] int SetToolbarItems(IntPtr lpButtons, uint nButtons, uint uFlags);
        }

        [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellView
        {
            [PreserveSig] int GetWindow(out IntPtr phwnd);
            [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool fEnterMode);
            [PreserveSig] int TranslateAccelerator(IntPtr pmsg);
            [PreserveSig] int EnableModeless([MarshalAs(UnmanagedType.Bool)] bool fEnable);
            [PreserveSig] int UIActivate(uint uState);
            [PreserveSig] int Refresh();
            [PreserveSig] int CreateViewWindow(IntPtr psvPrevious, IntPtr pfs, IntPtr psb, IntPtr prcView, out IntPtr phWnd);
            [PreserveSig] int DestroyViewWindow();
            [PreserveSig] int GetCurrentInfo(IntPtr pfs);
            [PreserveSig] int AddPropertySheetPages(uint dwReserved, IntPtr pfn, IntPtr lparam);
            [PreserveSig] int SaveViewState();
            [PreserveSig] int SelectItem(IntPtr pidlItem, uint uFlags);
            [PreserveSig] int GetItemObject(uint uItem, ref Guid riid, out IntPtr ppv);
        }

        [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFolderView
        {
            [PreserveSig] int GetCurrentViewMode(out uint pViewMode);
            [PreserveSig] int SetCurrentViewMode(uint viewMode);
            [PreserveSig] int GetFolder(ref Guid riid, out IntPtr ppv);
            [PreserveSig] int Item(int iItemIndex, out IntPtr ppidl);
            [PreserveSig] int ItemCount(uint uFlags, out int pcItems);
            [PreserveSig] int Items(uint uFlags, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetSelectionMarkedItem(out int piItem);
            [PreserveSig] int GetFocusedItem(out int piItem);
            [PreserveSig] int GetItemPosition(IntPtr pidl, out POINT ppt);
            [PreserveSig] int GetSpacing(ref POINT ppt);
            [PreserveSig] int GetDefaultSpacing(out POINT ppt);
            [PreserveSig] int GetAutoArrange();
            [PreserveSig] int SelectItem(int iItem, uint dwFlags);
            [PreserveSig] int SelectAndPositionItems(uint cidl, IntPtr apidl, IntPtr apt, uint dwFlags);
        }

        [ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemArray
        {
            [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppvOut);
            [PreserveSig] int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetAttributes(int attribFlags, uint sfgaoMask, out uint psfgaoAttribs);
            [PreserveSig] int GetCount(out uint pdwNumItems);
            [PreserveSig] int GetItemAt(uint dwIndex, out IShellItem ppsi);
            [PreserveSig] int EnumItems(out IntPtr ppenumShellItems);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetParent(out IShellItem ppsi);
            [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
            [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
        }
    }
}
