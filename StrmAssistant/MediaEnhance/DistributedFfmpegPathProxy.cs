using MediaBrowser.Controller.MediaEncoding;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace StrmAssistant.MediaEnhance
{
    /// <summary>
    /// Creates interface proxies that override only the ffmpeg executable path while forwarding
    /// every other IFfmpegManager / IMediaEncoder / IFfmpegConfiguration member to Emby's original
    /// objects. The original global objects are never mutated.
    /// </summary>
    public static class DistributedFfmpegPathProxy
    {
        public static IFfmpegManager CreateManagerProxy(IFfmpegManager original, string encoderPath)
        {
            if (original == null) throw new ArgumentNullException(nameof(original));
            if (string.IsNullOrWhiteSpace(encoderPath)) return original;

            var originalConfiguration = original.FfmpegConfiguration;
            if (originalConfiguration == null) return original;

            var configurationProxy = CreateProxy(originalConfiguration,
                new Dictionary<string, Func<object[], object>>(StringComparer.Ordinal)
                {
                    ["get_EncoderPath"] = _ => encoderPath
                });

            return CreateProxy(original,
                new Dictionary<string, Func<object[], object>>(StringComparer.Ordinal)
                {
                    ["get_FfmpegConfiguration"] = _ => configurationProxy
                });
        }

        public static IMediaEncoder CreateMediaEncoderProxy(IMediaEncoder original, IFfmpegManager managerProxy,
            string encoderPath)
        {
            if (original == null) throw new ArgumentNullException(nameof(original));
            if (string.IsNullOrWhiteSpace(encoderPath)) return original;

            var configuration = managerProxy?.FfmpegConfiguration;
            return CreateProxy(original,
                new Dictionary<string, Func<object[], object>>(StringComparer.Ordinal)
                {
                    // Older Emby implementations may read the obsolete IMediaEncoder path directly.
                    ["get_EncoderPath"] = _ => encoderPath,
                    ["get_FfmpegConfig"] = _ => configuration
                });
        }

        private static T CreateProxy<T>(T original, IDictionary<string, Func<object[], object>> overrides)
            where T : class
        {
            var proxy = DispatchProxy.Create<T, ForwardingDispatchProxy>();
            var state = (ForwardingDispatchProxy)(object)proxy;
            state.Target = original;
            state.Overrides = overrides;
            return proxy;
        }
    }

    /// <summary>
    /// Generic forwarding DispatchProxy used only by DistributedFfmpegPathProxy.
    /// It deliberately performs no member-name rewriting beyond explicit getter overrides.
    /// </summary>
    public class ForwardingDispatchProxy : DispatchProxy
    {
        internal object Target { get; set; }
        internal IDictionary<string, Func<object[], object>> Overrides { get; set; }

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            if (targetMethod == null) throw new ArgumentNullException(nameof(targetMethod));

            if (Overrides != null && Overrides.TryGetValue(targetMethod.Name, out var handler))
                return handler(args ?? Array.Empty<object>());

            if (Target == null)
                throw new InvalidOperationException("Forwarding proxy target is unavailable.");

            try
            {
                return targetMethod.Invoke(Target, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }
}
