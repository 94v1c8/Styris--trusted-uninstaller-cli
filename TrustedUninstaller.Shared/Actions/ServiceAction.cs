﻿#nullable enable
using System;
using System.Collections.Specialized;
using System.Configuration.Install;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using TrustedUninstaller.Shared.Exceptions;
using TrustedUninstaller.Shared.Tasks;
using YamlDotNet.Serialization;

namespace TrustedUninstaller.Shared.Actions
{
    public enum ServiceOperation
    {
        Stop,
        Continue,
        Start,
        Pause,
        Delete,
        Change
    }
    public class ServiceAction : TaskAction, ITaskAction
    {
        public void RunTaskOnMainThread() { throw new NotImplementedException(); }
        [YamlMember(typeof(ServiceOperation), Alias = "operation")]
        public ServiceOperation Operation { get; set; } = ServiceOperation.Delete;
        
        [YamlMember(typeof(string), Alias = "name")]
        public string ServiceName { get; set; } = null!;
        
        [YamlMember(typeof(int), Alias = "startup")]
        public int? Startup { get; set; }
        
        [YamlMember(typeof(bool), Alias = "deleteStop")]
        public bool DeleteStop { get; set; } = true;
        
        [YamlMember(typeof(bool), Alias = "deleteUsingRegistry")]
        public bool RegistryDelete { get; set; } = false;
        
        [YamlMember(typeof(string), Alias = "device")]
        public bool Device { get; set; } = false;
        
        [YamlMember(typeof(string), Alias = "weight")]
        public int ProgressWeight { get; set; } = 4;
        public int GetProgressWeight() => ProgressWeight;
        
        private bool InProgress { get; set; }
        public void ResetProgress() => InProgress = false;

        public string ErrorString() => $"ServiceAction failed to {Operation.ToString().ToLower()} service {ServiceName}.";
        
        private ServiceController? GetService()
        {
            if (ServiceName.EndsWith("*") && ServiceName.StartsWith("*")) return ServiceController.GetServices()
                .FirstOrDefault(service => service.ServiceName.IndexOf(ServiceName.Trim('*'), StringComparison.CurrentCultureIgnoreCase) >= 0);
            if (ServiceName.EndsWith("*")) return ServiceController.GetServices()
                .FirstOrDefault(service => service.ServiceName.StartsWith(ServiceName.TrimEnd('*'), StringComparison.CurrentCultureIgnoreCase));
            if (ServiceName.StartsWith("*")) return ServiceController.GetServices()
                .FirstOrDefault(service => service.ServiceName.EndsWith(ServiceName.TrimStart('*'), StringComparison.CurrentCultureIgnoreCase));
            
            return ServiceController.GetServices()
                .FirstOrDefault(service => service.ServiceName.Equals(ServiceName, StringComparison.CurrentCultureIgnoreCase));
        }
        private ServiceController? GetDevice()
        {
            if (ServiceName.EndsWith("*") && ServiceName.StartsWith("*")) return ServiceController.GetDevices()
                .FirstOrDefault(service => service.ServiceName.IndexOf(ServiceName.Trim('*'), StringComparison.CurrentCultureIgnoreCase) >= 0);
            if (ServiceName.EndsWith("*")) return ServiceController.GetDevices()
                .FirstOrDefault(service => service.ServiceName.StartsWith(ServiceName.TrimEnd('*'), StringComparison.CurrentCultureIgnoreCase));
            if (ServiceName.StartsWith("*")) return ServiceController.GetDevices()
                .FirstOrDefault(service => service.ServiceName.EndsWith(ServiceName.TrimStart('*'), StringComparison.CurrentCultureIgnoreCase));
            
            return ServiceController.GetDevices()
                .FirstOrDefault(service => service.ServiceName.Equals(ServiceName, StringComparison.CurrentCultureIgnoreCase));
        }

        public UninstallTaskStatus GetStatus()
        {
            if (InProgress) return UninstallTaskStatus.InProgress;

            if (Operation == ServiceOperation.Change && Startup.HasValue)
            {
                // TODO: Implement dev log. Example:
                // if (Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}") == null) WriteToDevLog($"Warning: Service name '{ServiceName}' not found in registry.");

                var root = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
                if (root == null) return UninstallTaskStatus.Completed;

                var value = root.GetValue("Start");

                return (int)value == Startup.Value ? UninstallTaskStatus.Completed : UninstallTaskStatus.ToDo;
            }
            
            ServiceController? serviceController;
            if (Device) serviceController = GetDevice();
            else serviceController = GetService();
            
            if (Operation == ServiceOperation.Delete && RegistryDelete)
            {
                // TODO: Implement dev log. Example:
                // if (Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}") == null) WriteToDevLog($"Warning: Service name '{ServiceName}' not found in registry.");

                var root = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
                return root == null ? UninstallTaskStatus.Completed : UninstallTaskStatus.ToDo;
            }

            return Operation switch
            {
                ServiceOperation.Stop =>
                    serviceController == null ||
                    serviceController?.Status == ServiceControllerStatus.Stopped
                    || serviceController?.Status == ServiceControllerStatus.StopPending ?
                        UninstallTaskStatus.Completed : UninstallTaskStatus.ToDo,
                ServiceOperation.Continue =>
                    serviceController == null ||
                    serviceController?.Status == ServiceControllerStatus.Running
                    || serviceController?.Status == ServiceControllerStatus.ContinuePending ?
                        UninstallTaskStatus.Completed : UninstallTaskStatus.ToDo,
                ServiceOperation.Start =>
                    serviceController?.Status == ServiceControllerStatus.StartPending
                    || serviceController?.Status == ServiceControllerStatus.Running ?
                        UninstallTaskStatus.Completed : UninstallTaskStatus.ToDo,
                ServiceOperation.Pause =>
                    serviceController == null ||
                    serviceController?.Status == ServiceControllerStatus.Paused
                    || serviceController?.Status == ServiceControllerStatus.PausePending ?
                        UninstallTaskStatus.Completed : UninstallTaskStatus.ToDo,
                ServiceOperation.Delete =>
                    serviceController == null || Win32.ServiceEx.IsPendingDeleteOrDeleted(serviceController.ServiceName) ?
                        UninstallTaskStatus.Completed : UninstallTaskStatus.ToDo,
                _ => throw new ArgumentOutOfRangeException("Argument out of Range", new ArgumentOutOfRangeException())
            };
        }

        private readonly string[] RegexNoKill = { "DcomLaunch" }; 
        
        public async Task<bool> RunTask()
        {
            if (InProgress) throw new TaskInProgressException("Another Service action was called while one was in progress.");
            if (Operation == ServiceOperation.Change && !Startup.HasValue) throw new ArgumentException("Startup property must be specified with the change operation.");
            if (Operation == ServiceOperation.Change && (Startup.Value > 4  || Startup.Value < 0)) throw new ArgumentException("Startup property must be between 1 and 4.");

            // This is a little cursed but it works and is concise lol
            Console.WriteLine($"{Operation.ToString().Replace("Stop", "Stopp").TrimEnd('e')}ing services matching '{ServiceName}'...");
            
            if (Operation == ServiceOperation.Change)
            {
                var action = new RegistryValueAction()
                {
                    KeyName = $@"HKLM\SYSTEM\CurrentControlSet\Services\{ServiceName}", 
                    Value = "Start", 
                    Data = Startup.Value, 
                    Type = RegistryValueType.REG_DWORD, 
                    Operation = RegistryValueOperation.Set
                };
                await action.RunTask();
                
                InProgress = false;
                return true;
            }
            
            ServiceController? service;

            if (Device) service = GetDevice();
            else service = GetService();

            if (service == null)
            {
                Console.WriteLine($"No services found matching '{ServiceName}'.");
                //ErrorLogger.WriteToErrorLog($"The service matching '{ServiceName}' does not exist.", Environment.StackTrace, "ServiceAction Error");
                if (Operation == ServiceOperation.Start)
                    throw new ArgumentException("Service " + ServiceName + " not found.");
                
                return false;
            }

            InProgress = true;

            var cmdAction = new CmdAction();

            if ((Operation == ServiceOperation.Delete && DeleteStop) || Operation == ServiceOperation.Stop)
            {
                if (RegexNoKill.Any(regex => Regex.Match(ServiceName, regex, RegexOptions.IgnoreCase).Success))
                {
                    Console.WriteLine($"Skipping {ServiceName}...");
                    return false;
                }

                try
                {
                    foreach (ServiceController dependentService in service.DependentServices.Where(x => x.Status != ServiceControllerStatus.Stopped))
                    {
                        Console.WriteLine($"Killing dependent service {dependentService.ServiceName}...");

                        if (dependentService.Status != ServiceControllerStatus.StopPending && dependentService.Status != ServiceControllerStatus.Stopped)
                        {
                            try
                            {
                                dependentService.Stop();
                            }
                            catch (Exception e)
                            {
                                dependentService.Refresh();
                                if (dependentService.Status != ServiceControllerStatus.Stopped && dependentService.Status != ServiceControllerStatus.StopPending)
                                    ErrorLogger.WriteToErrorLog("Dependent service stop failed: " + e.Message, e.StackTrace, "ServiceAction Warning", dependentService.ServiceName);
                            }

                            cmdAction.Command = Environment.Is64BitOperatingSystem ?
                                $"ProcessHacker\\x64\\ProcessHacker.exe -s -elevate -c -ctype service -cobject {dependentService.ServiceName} -caction stop" : 
                                $"ProcessHacker\\x86\\ProcessHacker.exe -s -elevate -c -ctype service -cobject {dependentService.ServiceName} -caction stop";
                            if (AmeliorationUtil.UseKernelDriver) cmdAction.RunTaskOnMainThread();
                        }

                        Console.WriteLine("Waiting for the dependent service to stop...");
                        try
                        {
                            dependentService.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromMilliseconds(5000));
                        }
                        catch (Exception e)
                        {
                            dependentService.Refresh();
                            if (service.Status != ServiceControllerStatus.Stopped)
                                ErrorLogger.WriteToErrorLog("Dependent service stop timeout exceeded.", e.StackTrace, "ServiceAction Warning", ServiceName);
                        }
                        
                        try
                        {
                            var killServ = new TaskKillAction()
                            {
                                ProcessID = Win32.ServiceEx.GetServiceProcessId(dependentService.ServiceName)
                            };
                            await killServ.RunTask();
                        }
                        catch (Exception e)
                        {
                            dependentService.Refresh();
                            if (dependentService.Status != ServiceControllerStatus.Stopped)
                                ErrorLogger.WriteToErrorLog($"Could not kill dependent service {dependentService.ServiceName}.",
                                    e.StackTrace, "ServiceAction Error", ServiceName);
                        }
                    }
                }
                catch (Exception e)
                {
                    ErrorLogger.WriteToErrorLog($"Error killing dependent services: " + e.Message,
                        e.StackTrace, "ServiceAction Error", ServiceName);
                }
            }

            if (Operation == ServiceOperation.Delete)
            {

                if (DeleteStop && service.Status != ServiceControllerStatus.StopPending && service.Status != ServiceControllerStatus.Stopped)
                {
                    try
                    {
                        service.Stop();
                    }
                    catch (Exception e)
                    {
                        service.Refresh();
                        if (service.Status != ServiceControllerStatus.Stopped && service.Status != ServiceControllerStatus.StopPending)
                            ErrorLogger.WriteToErrorLog("Service stop failed: " + e.Message, e.StackTrace, "ServiceAction Warning", ServiceName);
                    }

                    cmdAction.Command = Environment.Is64BitOperatingSystem ?
                        $"ProcessHacker\\x64\\ProcessHacker.exe -s -elevate -c -ctype service -cobject {service.ServiceName} -caction stop" : 
                        $"ProcessHacker\\x86\\ProcessHacker.exe -s -elevate -c -ctype service -cobject {service.ServiceName} -caction stop";
                    if (AmeliorationUtil.UseKernelDriver) cmdAction.RunTaskOnMainThread();
                    
                    Console.WriteLine("Waiting for the service to stop...");
                    try
                    {
                        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromMilliseconds(5000));
                    }
                    catch (Exception e)
                    {
                        service.Refresh();
                        if (service.Status != ServiceControllerStatus.Stopped)
                            ErrorLogger.WriteToErrorLog("Service stop timeout exceeded.", e.StackTrace, "ServiceAction Warning", ServiceName);
                    }
                    try
                    {
                        var killServ = new TaskKillAction()
                        {
                            ProcessID = Win32.ServiceEx.GetServiceProcessId(service.ServiceName)
                        };
                        await killServ.RunTask();
                    }
                    catch (Exception e)
                    {
                        service.Refresh();
                        if (service.Status != ServiceControllerStatus.Stopped)
                            ErrorLogger.WriteToErrorLog($"Could not kill service {service.ServiceName}.", e.StackTrace, "ServiceAction Error");
                    }
                }
                
                if (RegistryDelete)
                {
                    var action = new RegistryKeyAction()
                    {
                        KeyName = $@"HKLM\SYSTEM\CurrentControlSet\Services\{ServiceName}",
                        Operation = RegistryKeyOperation.Delete
                    };
                    await action.RunTask();
                }
                else
                {
                    try
                    {
                        ServiceInstaller ServiceInstallerObj = new ServiceInstaller();
                        ServiceInstallerObj.Context = new InstallContext();
                        ServiceInstallerObj.ServiceName = service.ServiceName; 
                        ServiceInstallerObj.Uninstall(null);
                    }
                    catch (Exception e)
                    {
                        ErrorLogger.WriteToErrorLog("Service uninstall failed: " + e.Message, e.StackTrace, "ServiceAction Warning", ServiceName);
                    }
                    cmdAction.Command = Environment.Is64BitOperatingSystem ?
                        $"ProcessHacker\\x64\\ProcessHacker.exe -s -elevate -c -ctype service -cobject {service.ServiceName} -caction delete" :
                        $"ProcessHacker\\x86\\ProcessHacker.exe -s -elevate -c -ctype service -cobject {service.ServiceName} -caction delete";
                    if (AmeliorationUtil.UseKernelDriver) cmdAction.RunTaskOnMainThread();
                }

            } else if (Operation == ServiceOperation.Start)
            {
                try
                {
                    service.Start();
                }
                catch (Exception e)
                {
                    service.Refresh();
                    if (service.Status != ServiceControllerStatus.Running)
                        ErrorLogger.WriteToErrorLog("Service start failed: " + e.Message, e.StackTrace, "ServiceAction Warning", ServiceName);
                }

                cmdAction.Command = Environment.Is64BitOperatingSystem ?
                    $"ProcessHacker\\x64\\ProcessHacker.exe -s -elevate -c -ctype service -cobject {service.ServiceName} -caction start" : 
                    $"ProcessHacker\\x86\\ProcessHacker.exe -s -elevate -c -ctype service -cobject {service.ServiceName} -caction start";
                if (AmeliorationUtil.UseKernelDriver) cmdAction.RunTaskOnMainThread();
                
                try
                {
                    service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromMilliseconds(5000));
                }
                catch (Exception e)
                {
                    service.Refresh();
                    if (service.Status != ServiceControllerStatus.Running)
                        ErrorLogger.WriteToErrorLog("Service start timeout exceeded.", e.StackTrace, "ServiceAction Warning", ServiceName);
                }
            } else if (Operation == ServiceOperation.Stop)
            {
                try
                {
                    service.Stop();
                }
                catch (Exception e)
                {
                    service.Refresh();
                    if (service.Status != ServiceControllerStatus.Stopped && service.Status != ServiceControllerStatus.StopPending)
                        ErrorLogger.WriteToErrorLog("Service stop failed: " + e.Message, e.StackTrace, "ServiceAction Warning", ServiceName);
                }

                cmdAction.Command = Environment.Is64BitOperatingSystem ?
                    $"ProcessHacker\\x64\\ProcessHacker.exe -s -elevate -c -ctype service -cobject {service.ServiceName} -caction stop" :
                    $"ProcessHacker\\x86\\ProcessHacker.exe -s -elevate -c -ctype service -cobject {service.ServiceName} -caction stop";
                if (AmeliorationUtil.UseKernelDriver) cmdAction.RunTaskOnMainThread();

                Console.WriteLine("Waiting for the service to stop...");
                try
                {
                    service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromMilliseconds(5000));
                }
                catch (Exception e)
                {
                    service.Refresh();
                    if (service.Status != ServiceControllerStatus.Stopped)
                        ErrorLogger.WriteToErrorLog("Service stop timeout exceeded.", e.StackTrace, "ServiceAction Warning", ServiceName);
                }
                try
                {
                    var killServ = new TaskKillAction()
                    {
                        ProcessID = Win32.ServiceEx.GetServiceProcessId(service.ServiceName)
                    };
                    await killServ.RunTask();
                }
                catch (Exception e)
                {
                    service.Refresh();
                    if (service.Status != ServiceControllerStatus.Stopped)
                        ErrorLogger.WriteToErrorLog($"Could not kill dependent service {service.ServiceName}.",
                            e.StackTrace, "ServiceAction Error");
                }
            } else if (Operation == ServiceOperation.Pause)
            {
                try
                {
                    service.Pause();
                }
                catch (Exception e)
                {
                    service.Refresh();
                    if (service.Status != ServiceControllerStatus.Paused)
                        ErrorLogger.WriteToErrorLog("Service pause failed: " + e.Message, e.StackTrace, "ServiceAction Warning", ServiceName);
                }

                cmdAction.Command = Environment.Is64BitOperatingSystem ?
                    $"ProcessHacker\\x64\\ProcessHacker.exe -s -elevate -c -ctype service -cobject {service.ServiceName} -caction pause" : 
                    $"ProcessHacker\\x86\\ProcessHacker.exe -s -elevate -c -ctype service -cobject {service.ServiceName} -caction pause";
                if (AmeliorationUtil.UseKernelDriver) cmdAction.RunTaskOnMainThread();
                
                try
                {
                    service.WaitForStatus(ServiceControllerStatus.Paused, TimeSpan.FromMilliseconds(5000));
                }
                catch (Exception e)
                {
                    service.Refresh();
                    if (service.Status != ServiceControllerStatus.Paused)
                        ErrorLogger.WriteToErrorLog("Service pause timeout exceeded.", e.StackTrace, "ServiceAction Warning", ServiceName);
                }
            }
            else if (Operation == ServiceOperation.Continue)
            {
                try
                {
                    service.Pause();
                }
                catch (Exception e)
                {
                    service.Refresh();
                    if (service.Status != ServiceControllerStatus.Running)
                        ErrorLogger.WriteToErrorLog("Service continue failed: " + e.Message, e.StackTrace, "ServiceAction Warning", ServiceName);
                }

                cmdAction.Command = Environment.Is64BitOperatingSystem ?
                    $"ProcessHacker\\x64\\ProcessHacker.exe -s -elevate -c -ctype service -cobject {service.ServiceName} -caction continue" : 
                    $"ProcessHacker\\x86\\ProcessHacker.exe -s -elevate -c -ctype service -cobject {service.ServiceName} -caction continue";
                if (AmeliorationUtil.UseKernelDriver) cmdAction.RunTaskOnMainThread();
                
                try
                {
                    service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromMilliseconds(5000));
                }
                catch (Exception e)
                {
                    service.Refresh();
                    if (service.Status != ServiceControllerStatus.Running)
                        ErrorLogger.WriteToErrorLog("Service continue timeout exceeded.", e.StackTrace, "ServiceAction Warning", ServiceName);
                }
            }

            service?.Dispose();
            await Task.Delay(100);

            InProgress = false;
            return true;
        }
    }
}
