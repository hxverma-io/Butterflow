using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

namespace ButterFlow.Core
{
    public static class ShellHelper
    {
        /// <summary>
        /// Retrieves the currently selected file path(s) in active Windows Explorer folder windows
        /// OR on the Windows Desktop. Uses native Shell.Application COM via dynamic (0 external dependencies).
        /// </summary>
        public static List<string> GetSelectedExplorerFiles()
        {
            List<string> files = new List<string>();

            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return files;

                dynamic shell = Activator.CreateInstance(shellType);
                if (shell == null) return files;

                dynamic windows = shell.Windows();
                if (windows == null) return files;

                // 1. Check all open Windows Explorer folder windows
                int count = 0;
                try { count = (int)windows.Count; } catch { }

                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        dynamic win = windows.Item(i);
                        if (win == null) continue;

                        dynamic doc = win.Document;
                        if (doc == null) continue;

                        dynamic sel = doc.SelectedItems();
                        if (sel == null) continue;

                        int selCount = 0;
                        try { selCount = (int)sel.Count; } catch { }

                        for (int j = 0; j < selCount; j++)
                        {
                            try
                            {
                                dynamic item = sel.Item(j);
                                if (item != null)
                                {
                                    string path = (string)item.Path;
                                    if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)))
                                    {
                                        files.Add(path);
                                    }
                                }
                            }
                            catch { }
                        }

                        if (files.Count > 0) return files;
                    }
                    catch { }
                }

                // 2. Check the Windows Desktop (via IShellWindows.FindWindowSW)
                try
                {
                    object empty = null;
                    int deskHwnd = 0;
                    dynamic desk = windows.FindWindowSW(ref empty, ref empty, 8, out deskHwnd, 1);
                    if (desk != null && desk.Document != null)
                    {
                        dynamic sel = desk.Document.SelectedItems();
                        if (sel != null)
                        {
                            int selCount = 0;
                            try { selCount = (int)sel.Count; } catch { }

                            for (int j = 0; j < selCount; j++)
                            {
                                try
                                {
                                    dynamic item = sel.Item(j);
                                    if (item != null)
                                    {
                                        string path = (string)item.Path;
                                        if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)))
                                        {
                                            files.Add(path);
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
                catch { }
            }
            catch { }

            return files;
        }

        /// <summary>
        /// Extracts the real high-res Windows Shell icon bitmap (32x32) for any file name, extension, or full path.
        /// Converts to a managed Bitmap before destroying the unmanaged HICON handle, ensuring zero handle leaks or rendering errors.
        /// </summary>
        public static Bitmap GetFileIconBitmap(string fileName)
        {
            try
            {
                NativeMethods.SHFILEINFO shinfo = new NativeMethods.SHFILEINFO();

                if (!string.IsNullOrEmpty(fileName) && File.Exists(fileName))
                {
                    // Existing local file: extract exact icon/thumbnail
                    NativeMethods.SHGetFileInfo(
                        fileName,
                        0,
                        ref shinfo,
                        (uint)Marshal.SizeOf(shinfo),
                        NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON
                    );
                }
                else if (!string.IsNullOrEmpty(fileName) && Directory.Exists(fileName))
                {
                    // Folder icon
                    NativeMethods.SHGetFileInfo(
                        fileName,
                        NativeMethods.FILE_ATTRIBUTE_DIRECTORY,
                        ref shinfo,
                        (uint)Marshal.SizeOf(shinfo),
                        NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON | NativeMethods.SHGFI_USEFILEATTRIBUTES
                    );
                }
                else
                {
                    // Network or remote file name: query registered Shell file type icon
                    NativeMethods.SHGetFileInfo(
                        fileName,
                        NativeMethods.FILE_ATTRIBUTE_NORMAL,
                        ref shinfo,
                        (uint)Marshal.SizeOf(shinfo),
                        NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON | NativeMethods.SHGFI_USEFILEATTRIBUTES
                    );
                }

                if (shinfo.hIcon != IntPtr.Zero)
                {
                    using (Icon icon = Icon.FromHandle(shinfo.hIcon))
                    {
                        Bitmap bmp = icon.ToBitmap();
                        NativeMethods.DestroyIcon(shinfo.hIcon);
                        return bmp;
                    }
                }
            }
            catch { }

            try
            {
                return SystemIcons.Application.ToBitmap();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Inspects the window under cursor (x, y) to find the exact target folder.
        /// If over an open Explorer window, returns that window's current directory path.
        /// If over the Desktop or anything else, returns the Desktop path.
        /// </summary>
        public static string GetDropTargetFolder(int x, int y)
        {
            try
            {
                NativeMethods.POINT pt = new NativeMethods.POINT(x, y);
                IntPtr hwnd = NativeMethods.WindowFromPoint(pt);
                if (hwnd != IntPtr.Zero)
                {
                    IntPtr rootHwnd = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
                    if (rootHwnd == IntPtr.Zero) rootHwnd = hwnd;

                    System.Text.StringBuilder sb = new System.Text.StringBuilder(256);
                    NativeMethods.GetClassName(rootHwnd, sb, 256);
                    string rootClass = sb.ToString();

                    System.Text.StringBuilder sbChild = new System.Text.StringBuilder(256);
                    NativeMethods.GetClassName(hwnd, sbChild, 256);
                    string childClass = sbChild.ToString();

                    // If it is an Explorer folder window, find its path
                    if (rootClass == "CabinetWClass" || rootClass == "ExploreWClass" ||
                        childClass == "CabinetWClass" || childClass == "ExploreWClass")
                    {
                        string explorerPath = GetExplorerPathByHwndOrPoint(rootHwnd, hwnd, x, y);
                        if (!string.IsNullOrEmpty(explorerPath) && Directory.Exists(explorerPath))
                        {
                            return explorerPath;
                        }
                    }

                    // Desktop detection
                    if (rootClass == "Progman" || rootClass == "WorkerW" || childClass == "SHELLDLL_DefView" || childClass == "SysListView32")
                    {
                        return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                    }
                }

                // Check if (x, y) falls within any open Explorer window rect
                string fallbackExplorer = FindExplorerWindowAtPoint(x, y);
                if (!string.IsNullOrEmpty(fallbackExplorer) && Directory.Exists(fallbackExplorer))
                {
                    return fallbackExplorer;
                }
            }
            catch { }

            // Spatial fallback: User's Desktop
            return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }

        private static string GetExplorerPathByHwndOrPoint(IntPtr rootHwnd, IntPtr childHwnd, int x, int y)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return null;
                dynamic shell = Activator.CreateInstance(shellType);
                if (shell == null) return null;
                dynamic windows = shell.Windows();
                if (windows == null) return null;

                long rootTarget = rootHwnd.ToInt64();
                long childTarget = childHwnd.ToInt64();

                // 1. First pass: exact HWND match
                foreach (dynamic w in windows)
                {
                    try
                    {
                        long winHwnd = Convert.ToInt64(w.HWND);
                        if (winHwnd == rootTarget || winHwnd == childTarget)
                        {
                            dynamic doc = w.Document;
                            if (doc != null && doc.Folder != null && doc.Folder.Self != null)
                            {
                                string path = (string)doc.Folder.Self.Path;
                                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                                {
                                    return path;
                                }
                            }
                        }
                    }
                    catch { }
                }

                // 2. Second pass: bounds containment check
                foreach (dynamic w in windows)
                {
                    try
                    {
                        long winHwnd = Convert.ToInt64(w.HWND);
                        NativeMethods.RECT r;
                        if (NativeMethods.GetWindowRect((IntPtr)winHwnd, out r))
                        {
                            if (x >= r.Left && x <= r.Right && y >= r.Top && y <= r.Bottom)
                            {
                                dynamic doc = w.Document;
                                if (doc != null && doc.Folder != null && doc.Folder.Self != null)
                                {
                                    string path = (string)doc.Folder.Self.Path;
                                    if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                                    {
                                        return path;
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        private static string FindExplorerWindowAtPoint(int x, int y)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return null;
                dynamic shell = Activator.CreateInstance(shellType);
                if (shell == null) return null;
                dynamic windows = shell.Windows();
                if (windows == null) return null;

                foreach (dynamic w in windows)
                {
                    try
                    {
                        long winHwnd = Convert.ToInt64(w.HWND);
                        NativeMethods.RECT r;
                        if (NativeMethods.GetWindowRect((IntPtr)winHwnd, out r))
                        {
                            if (x >= r.Left && x <= r.Right && y >= r.Top && y <= r.Bottom)
                            {
                                dynamic doc = w.Document;
                                if (doc != null && doc.Folder != null && doc.Folder.Self != null)
                                {
                                    string path = (string)doc.Folder.Self.Path;
                                    if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                                    {
                                        return path;
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Refreshes the Windows Shell folder / Desktop so the dropped file appears immediately.
        /// </summary>
        public static void NotifyShellFolderUpdated(string folderPath)
        {
            try
            {
                NativeMethods.SHChangeNotify(NativeMethods.SHCNE_UPDATEDIR, NativeMethods.SHCNF_PATHW | NativeMethods.SHCNF_FLUSH, folderPath, null);
            }
            catch { }
        }
    }
}
