using System;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Snapline
{
    interface IStartupEntry
    {
        string Read();
        void Write(string command);
        void Remove();
    }
    sealed class RegistryStartupEntry : IStartupEntry
    {
        internal const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        readonly string path;
        internal RegistryStartupEntry() : this(RunPath) { }
        internal RegistryStartupEntry(string path) { this.path = path; }
        public string Read()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(path))
                return key == null ? null : key.GetValue("Snapline", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        public void Write(string command)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(path))
                key.SetValue("Snapline", command, RegistryValueKind.String);
        }
        public void Remove()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(path, true))
                if (key != null) key.DeleteValue("Snapline", false);
        }
    }
    sealed class StartupRegistration
    {
        readonly Store store;
        readonly IStartupEntry entry;
        readonly string executable;
        internal StartupRegistration(Store store, IStartupEntry entry, string executable)
        { this.store = store; this.entry = entry; this.executable = executable; }
        internal static bool ManagesSystemStartup(string[] args)
        {
            return !args.Any(x => x == "--data-dir" || x == "--demo" || x == "--demo-notes" || x == "--demo-remarks" || x == "--no-capture" || x.EndsWith("-test", StringComparison.Ordinal));
        }
        internal static string Command(string executable)
        {
            string path = Path.GetFullPath(executable);
            if (path.IndexOf('"') >= 0 || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("启动程序路径无效。");
            string command = "\"" + path + "\" --startup";
            if (command.Length > 260) throw new InvalidOperationException("程序路径过长，请解压到较短路径后重新启用开机启动。");
            return command;
        }
        internal bool Initialize(out string error)
        {
            // A first normal launch, including an upgrade, enables startup once.
            return Apply(store.Settings.StartupRevision < 1 || store.Settings.StartupEnabled, out error);
        }
        internal bool Apply(bool enabled, out string error)
        {
            error = null;
            bool oldEnabled = store.Settings.StartupEnabled;
            int oldRevision = store.Settings.StartupRevision;
            string oldCommand = null;
            bool changed = false;
            try
            {
                string command = enabled ? Command(executable) : null;
                oldCommand = entry.Read();
                if (!String.Equals(oldCommand, command, StringComparison.Ordinal))
                {
                    if (enabled) entry.Write(command); else entry.Remove();
                    changed = true;
                }
                store.Settings.StartupEnabled = enabled;
                store.Settings.StartupRevision = 1;
                store.SaveSettings();
                return true;
            }
            catch (Exception ex)
            {
                store.Settings.StartupEnabled = oldEnabled;
                store.Settings.StartupRevision = oldRevision;
                if (changed)
                {
                    try { if (oldCommand == null) entry.Remove(); else entry.Write(oldCommand); }
                    catch (Exception rollback) { store.Log("Restore startup registration", rollback); }
                }
                store.Log("Set startup registration", ex);
                error = "开机启动设置未保存：" + ex.Message;
                return false;
            }
        }
    }
}
