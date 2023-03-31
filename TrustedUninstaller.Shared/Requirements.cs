﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Xml.Serialization;
using Microsoft.Win32;
using TrustedUninstaller.Shared;
using TrustedUninstaller.Shared.Actions;

namespace TrustedUninstaller.Shared
{
    public static class Requirements
    {
        [Serializable]
        public enum Requirement
        {
            [XmlEnum("Internet")]
            Internet = 0,
            [XmlEnum("NoInternet")]
            NoInternet = 1,
            [XmlEnum("DefenderDisabled")]
            DefenderDisabled = 2,
            [XmlEnum("DefenderToggled")]
            DefenderToggled = 3,
            [XmlEnum("NoPendingUpdates")]
            NoPendingUpdates = 4,
            [XmlEnum("Activation")]
            Activation = 5,
            [XmlEnum("NoAntivirus")]
            NoAntivirus = 6
        }

        public static async Task<Requirement[]> MetRequirements(this Requirement[] requirements)
        {
            var requirementEnum = (Requirement[])Enum.GetValues(typeof(Requirement));
            if (requirements == null)
            {
                return requirementEnum;
            }
            // Add all requirements that are not included
            var metRequirements = requirementEnum.Except(requirements).ToList();
            
            if (requirements.Contains (Requirement.Internet))
                if (await new Internet().IsMet()) metRequirements.Add(Requirement.Internet);
                else metRequirements.Add(Requirement.NoInternet);

            if (requirements.Contains (Requirement.NoAntivirus))
                if (await new NoAntivirus().IsMet()) metRequirements.Add(Requirement.NoAntivirus);
            
            if (requirements.Contains (Requirement.NoPendingUpdates))
                if (await new NoPendingUpdates().IsMet()) metRequirements.Add(Requirement.NoPendingUpdates);

            if (requirements.Contains (Requirement.Activation))
                if (await new Activation().IsMet()) metRequirements.Add(Requirement.Activation);
            
            if (requirements.Contains (Requirement.DefenderDisabled))
                if (await new DefenderDisabled().IsMet()) metRequirements.Add(Requirement.DefenderDisabled);
            
            if (requirements.Contains (Requirement.DefenderToggled))
                if (await new DefenderDisabled().IsMet()) metRequirements.Add(Requirement.DefenderToggled);

            return metRequirements.ToArray();
        }
        
        public interface IRequirements
        {
            Task<bool> IsMet();
            Task<bool> Meet();
        }
        public class RequirementBase
        {
            public class ProgressEventArgs : EventArgs
            {
                public int PercentAdded;
                public ProgressEventArgs(int percent)
                {
                    PercentAdded = percent;
                }
            }
            
            public event EventHandler<ProgressEventArgs> ProgressChanged;

            protected void OnProgressAdded(int percent)
            {
                ProgressChanged?.Invoke(this, new ProgressEventArgs(percent));
            }
        }

        public class Internet : RequirementBase, IRequirements
        {
            [DllImport("wininet.dll", SetLastError = true)]
            private static extern bool InternetCheckConnection(string lpszUrl, int dwFlags, int dwReserved);
            
            [DllImport("wininet.dll", SetLastError=true)]
            extern static bool InternetGetConnectedState(out int lpdwFlags, int dwReserved);
            
            public async Task<bool> IsMet()
            {
                try
                {
                    try
                    {
                        if (!InternetCheckConnection("http://archlinux.org", 1, 0))
                        {
                            if (!InternetCheckConnection("http://google.com", 1, 0))
                                return false;
                        }
                        return true;
                    }
                    catch
                    {
                        var request = (HttpWebRequest)WebRequest.Create("http://google.com");
                        request.KeepAlive = false;
                        request.Timeout = 5000;
                        using (var response = (HttpWebResponse)request.GetResponse())
                            return true;
                    }
                }
                catch
                {
                    return false;
                }
            }

            public Task<bool> Meet() => throw new NotImplementedException();
        }

        public class DefenderDisabled : RequirementBase, IRequirements
        {
            public async Task<bool> IsMet()
            {
                if (Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Services\\WinDefend") != null)
                    return false;
                return Process.GetProcessesByName("MsMpEng").Length == 0;
            }

            public async Task<bool> Meet()
            {
                OnProgressAdded(30);
                try
                {
                    //Scheduled task to run the program on logon, and remove defender notifications
                    var runOnLogOn = new CmdAction()
                    {
                        Command = $"schtasks /create /tn \"AME Wizard\" /tr \"{Assembly.GetExecutingAssembly().Location}\" /sc onlogon /RL HIGHEST /f",
                        Wait = false
                    };
                    await runOnLogOn.RunTask();

                    OnProgressAdded(10);
                    var disableNotifs = new CmdAction()
                    {
                        Command = $"reg add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows Defender Security Center\\Notifications\" /v DisableNotifications /t REG_DWORD /d 1 /f"
                    };
                    await disableNotifs.RunTask();
                    OnProgressAdded(10);
                    var defenderService = new RunAction()
                    {
                        Exe = $"NSudoLC.exe",
                        Arguments = "-U:T -P:E -M:S -Priority:RealTime -UseCurrentConsole -Wait reg delete \"HKLM\\SYSTEM\\CurrentControlSet\\Services\\WinDefend\" /f",
                        BaseDir = true,
                        CreateWindow = false
                    };
                    await defenderService.RunTask();
                    OnProgressAdded(20);
                    // MpOAV.dll normally in use by a lot of processes. This prevents that.
                    var MpOAVCLSID = new RunAction()
                    {
                        Exe = $"NSudoLC.exe",
                        Arguments = @"-U:T -P:E -M:S -Priority:RealTime -Wait reg delete ""HKCR\CLSID\{2781761E-28E0-4109-99FE-B9D127C57AFE}\InprocServer32"" /f",
                        BaseDir = true,
                        CreateWindow = false
                    };
                    await MpOAVCLSID.RunTask();
                    OnProgressAdded(20);

                    if (Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Services\\WinDefend") != null)
                    {
                        throw new Exception("Could not remove WinDefend service.");
                    }
                    OnProgressAdded(10);

                    return true;
                }
                catch (Exception exception)
                {
                    ErrorLogger.WriteToErrorLog(exception.Message, exception.StackTrace,
                        $"Could not remove Windows Defender.");

                    return false;
                    // TODO: Move this to requirements page view if any Meet calls return false
                    try
                    {
                        var saveLogDir = System.Windows.Forms.Application.StartupPath + "\\AME Logs";
                        if (Directory.Exists(saveLogDir)) Directory.Delete(saveLogDir, true);
                        Directory.Move(Directory.GetCurrentDirectory() + "\\Logs", saveLogDir);
                    }
                    catch (Exception) { }

                    //MessageBox.Show("Could not remove Windows Defender. Check the error logs and contact the team " +
                    //    "for more information and assistance.", "Could not remove Windows Defender.", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        
        public class DefenderToggled : RequirementBase, IRequirements
        {
            public async Task<bool> IsMet()
            {
                var defenderKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Defender");

                RegistryKey realtimeKey = null;
                try
                {
                    realtimeKey = defenderKey.OpenSubKey("Real-Time Protection");
                }
                catch
                {
                    
                }
                if (realtimeKey != null)
                {
                    try
                    {
                        if (!((int)realtimeKey.GetValue("DisableRealtimeMonitoring") != 1))
                            return false;
                    }
                    catch (Exception exception)
                    {
                        return false;
                    }
                }

                try
                {
                    if (!((int)defenderKey.OpenSubKey("SpyNet").GetValue("SpyNetReporting") != 0))
                            return false;
                }
                catch
                {

                }
                try
                {
                    if (!((int)defenderKey.OpenSubKey("SpyNet").GetValue("SubmitSamplesConsent") != 0))
                            return false;
                }
                catch
                {

                }
                try
                {
                    if (!((int)defenderKey.OpenSubKey("Features").GetValue("TamperProtection") != 4))
                            return false;
                }
                catch
                {

                }
                return true;
            }

            public async Task<bool> Meet()
            {
                throw new NotImplementedException();
            }
        }
        
        public class NoPendingUpdates : RequirementBase, IRequirements
        {
            public async Task<bool> IsMet()
            {
                //TODO: This
                return true;
            }

            public Task<bool> Meet() => throw new NotImplementedException();
        }
        
        public class NoAntivirus : RequirementBase, IRequirements
        {
            public async Task<bool> IsMet()
            {
                return !WinUtil.GetEnabledAvList(false).Any();
            }

            public Task<bool> Meet() => throw new NotImplementedException();
        }

        public class Activation : RequirementBase, IRequirements
        {
            public async Task<bool> IsMet()
            {
                return WinUtil.IsGenuineWindows();
            }

            public Task<bool> Meet() => throw new NotImplementedException();
        }
        
        public class WindowsBuild
        {
            public bool IsMet(string[] builds)
            {
                return builds.Any(x => x.Equals(Globals.WinVer.ToString()));
            }

            public Task<bool> Meet() => throw new NotImplementedException();
        }
        
    }
}