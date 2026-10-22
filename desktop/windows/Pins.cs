// `Verticals.exe --unpin`, run by the uninstaller while the exe is still there: takes Verticals off the taskbar and the
// Start menu the way Windows offers uninstallers (IStartMenuPinnedList), so no dead icon stays behind. Windows only matches
// a pin to a program that exists, which is why this runs before the files go.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Verticals
{
    static class Pins
    {
        public static void Unpin()
        {
            var exe = Application.ExecutablePath;
            var taskbar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                       @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
            var shortcuts = new List<string>();
            if (Directory.Exists(taskbar))
                foreach (var link in Directory.GetFiles(taskbar, "*.lnk"))
                    if (string.Equals(Target(link), exe, StringComparison.OrdinalIgnoreCase)) shortcuts.Add(link);
            // A pin is known by its shortcut, its program or the app's id: whichever Windows took when it was pinned.
            var items = new List<string>(shortcuts) { exe, @"shell:AppsFolder\Verticals.Desktop" };
            foreach (var item in items) Remove(item);
            // A shortcut Windows did not take back would stay as a blank icon.
            foreach (var link in shortcuts)
                try { if (File.Exists(link)) File.Delete(link); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        static string Target(string link)
        {
            try
            {
                var type = Type.GetTypeFromProgID("WScript.Shell");
                var shell = Activator.CreateInstance(type);
                var shortcut = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { link });
                return shortcut.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null) as string;
            }
            catch (Exception) { return null; }
        }

        static void Remove(string path)
        {
            var iid = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");  // IShellItem
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item) != 0) return;
            try
            {
                var list = (IStartMenuPinnedList)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("a2a9545d-a0c2-42b4-9708-a0b2badd77c8")));
                list.RemoveFromList(item);
            }
            catch (Exception) { }
            finally { Marshal.Release(item); }
        }

        [ComImport, Guid("4CD19ADA-25A5-4A32-B3B7-347BEE5BE36B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IStartMenuPinnedList { [PreserveSig] int RemoveFromList(IntPtr item); }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr bc, ref Guid riid, out IntPtr item);
    }
}
