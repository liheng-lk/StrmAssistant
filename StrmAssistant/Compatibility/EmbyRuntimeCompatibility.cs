using System;
using System.Reflection;

namespace StrmAssistant.Compatibility
{
    internal enum EmbyCompatibilityBand
    {
        Unknown,
        Emby48,
        Emby49,
        Emby410OrLater
    }

    /// <summary>
    /// Keeps runtime version detection independent from Emby APIs that may move or change
    /// between server releases. Version-sensitive feature code should depend on this layer
    /// instead of probing server internals directly.
    /// </summary>
    internal sealed class EmbyRuntimeCompatibility
    {
        private EmbyRuntimeCompatibility(Version serverVersion)
        {
            ServerVersion = serverVersion;
            Band = GetBand(serverVersion);
        }

        public Version ServerVersion { get; }

        public EmbyCompatibilityBand Band { get; }

        public bool IsKnown => ServerVersion != null;

        public bool IsAtLeast(int major, int minor)
        {
            return ServerVersion != null && ServerVersion >= new Version(major, minor);
        }

        public static EmbyRuntimeCompatibility Detect(object applicationHost)
        {
            if (applicationHost == null)
            {
                return new EmbyRuntimeCompatibility(null);
            }

            var hostType = applicationHost.GetType();
            var version = ReadVersionProperty(hostType, applicationHost, "ApplicationVersion")
                          ?? ReadVersionProperty(hostType, applicationHost, "Version");

            return new EmbyRuntimeCompatibility(version);
        }

        private static Version ReadVersionProperty(Type hostType, object applicationHost, string propertyName)
        {
            var property = hostType.GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (property == null || property.GetIndexParameters().Length != 0)
            {
                return null;
            }

            try
            {
                var value = property.GetValue(applicationHost);

                if (value is Version version)
                {
                    return version;
                }

                if (value != null && Version.TryParse(value.ToString(), out var parsed))
                {
                    return parsed;
                }
            }
            catch
            {
                // Runtime version detection must never prevent the plugin from loading.
            }

            return null;
        }

        private static EmbyCompatibilityBand GetBand(Version version)
        {
            if (version == null)
            {
                return EmbyCompatibilityBand.Unknown;
            }

            if (version.Major > 4 || version.Minor >= 10)
            {
                return EmbyCompatibilityBand.Emby410OrLater;
            }

            if (version.Minor >= 9)
            {
                return EmbyCompatibilityBand.Emby49;
            }

            if (version.Minor >= 8)
            {
                return EmbyCompatibilityBand.Emby48;
            }

            return EmbyCompatibilityBand.Unknown;
        }
    }
}
