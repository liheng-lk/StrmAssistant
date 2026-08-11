using MediaBrowser.Controller.Plugins;
using StrmAssistant.Api;
using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace StrmAssistant.Compatibility
{
    public sealed class RemoteDeepDeleteCapabilityStatus
    {
        public bool PlanTargetFound { get; set; }
        public bool ExecuteTargetFound { get; set; }
        public bool DirectApiIntegration { get; set; }
        public bool Patched { get; set; }
        public long RemotePlansHandled { get; set; }
        public long RemoteDeletesSucceeded { get; set; }
        public long RemoteDeletesFailed { get; set; }
        public string LastProvider { get; set; }
        public string LastRemotePath { get; set; }
        public string LastError { get; set; }
        public string Error { get; set; }
    }

    public static class RemoteDeepDeleteModState
    {
        public static RemoteDeepDeleteCapabilityStatus Status { get; internal set; } =
            new RemoteDeepDeleteCapabilityStatus();
    }

    /// <summary>
    /// Compatibility/status entry point only. Remote deep delete used to depend on a Harmony prefix
    /// around DeepDeleteApiService. That made mapping failures silently fall through to local-only
    /// deletion and was fragile when the API return type changed. Version 2.0.4 integrates remote
    /// planning/execution directly into DeepDeleteApiService, so no runtime interception is needed.
    /// </summary>
    public sealed class RemoteDeepDeleteRuntimeModEntryPoint : IServerEntryPoint
    {
        public void Run()
        {
            var status = new RemoteDeepDeleteCapabilityStatus();
            RemoteDeepDeleteModState.Status = status;
            try
            {
                var planTarget = typeof(DeepDeleteApiService).GetMethod("Get",
                    BindingFlags.Instance | BindingFlags.Public,
                    null, new[] { typeof(GetDeepDeletePlan) }, null);
                var executeTarget = typeof(DeepDeleteApiService).GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(method => method.Name == "Delete" &&
                                              method.GetParameters().Length == 1 &&
                                              method.GetParameters()[0].ParameterType == typeof(ExecuteDeepDelete));
                status.PlanTargetFound = planTarget != null;
                status.ExecuteTargetFound = executeTarget != null;
                status.DirectApiIntegration = planTarget != null && executeTarget != null &&
                                              typeof(Task).IsAssignableFrom(executeTarget.ReturnType);
                status.Patched = false;
                if (!status.DirectApiIntegration)
                    status.Error = "Direct remote deep-delete API integration was not detected.";
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Warn("Remote Deep Delete capability probe failed: " + status.Error);
            }
        }

        public void Dispose()
        {
        }
    }
}
