using MediaBrowser.Controller.Plugins;
using StrmAssistant.Common;
using System;
using System.Reflection;

namespace StrmAssistant.Compatibility
{
    public sealed class VideoThumbnail410CapabilityStatus
    {
        public bool TargetFound { get; set; }
        public bool Patched { get; set; }
        public bool NativeRefreshMethodFound { get; set; }
        public int NativeParameterCount { get; set; }
        public string Error { get; set; }
    }

    public static class VideoThumbnail410CompatibilityState
    {
        public static VideoThumbnail410CapabilityStatus Status { get; internal set; } =
            new VideoThumbnail410CapabilityStatus();
    }

    /// <summary>
    /// Read-only capability probe. VideoThumbnailApi now selects Emby's 8/9/10-parameter
    /// RefreshThumbnailImages signature directly, including the Emby 4.10 extraction flag.
    /// </summary>
    public sealed class VideoThumbnail410RuntimeCompatibilityEntryPoint : IServerEntryPoint
    {
        public void Run()
        {
            var status = new VideoThumbnail410CapabilityStatus();
            VideoThumbnail410CompatibilityState.Status = status;

            try
            {
                var target = typeof(VideoThumbnailApi).GetMethod(
                    "RefreshThumbnailImages",
                    BindingFlags.Instance | BindingFlags.Public);
                status.TargetFound = target != null;

                var nativeMethod = typeof(VideoThumbnailApi).GetField(
                    "_refreshThumbnailImages",
                    BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(Plugin.VideoThumbnailApi) as MethodInfo;
                status.NativeRefreshMethodFound = nativeMethod != null;
                status.NativeParameterCount = nativeMethod?.GetParameters().Length ?? 0;

                status.Patched = target != null && nativeMethod != null &&
                                 (status.NativeParameterCount == 8 ||
                                  status.NativeParameterCount == 9 ||
                                  status.NativeParameterCount == 10);

                if (!status.Patched)
                {
                    status.Error = nativeMethod == null
                        ? "Emby ThumbnailGenerator.RefreshThumbnailImages was not found."
                        : "Unsupported RefreshThumbnailImages parameter count: " + status.NativeParameterCount;
                }
                else if (Plugin.Instance?.DebugMode == true)
                {
                    Plugin.Instance.Logger.Debug(
                        "Video thumbnail native compatibility active: RefreshThumbnailImages args={0}",
                        status.NativeParameterCount);
                }
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Warn("Video thumbnail 4.10 capability probe failed: " + status.Error);
            }
        }

        public void Dispose()
        {
        }
    }
}
