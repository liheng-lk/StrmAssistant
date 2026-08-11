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
        private EmbyRuntimeCompatibility(Version serverVersion, string detectionSource)
        {
            ServerVersion = serverVersion;
            DetectionSource = detectionSource ?? "unknown";
            Band = GetBand(serverVersion);
        }

        public Version ServerVersion { get; }

        public EmbyCompatibilityBand Band { get; }

        public string DetectionSource { get; }

        public bool IsKnown => ServerVersion != null;

        public bool IsAtLeast(int major, int minor)
        {
            return ServerVersion != null && ServerVersion >= new Version(major, minor);
        }

        public static EmbyRuntimeCompatibility Detect(object applicationHost)
        {
            if (applicationHost == null)
            {
                return new EmbyRuntimeCompatibility(null, "application-host-null");
            }

            var hostType = applicationHost.GetType();

            if (TryReadVersionProperty(hostType, applicationHost, "ApplicationVersion", out var version))
            {
                return new EmbyRuntimeCompatibility(version, hostType.FullName + ".ApplicationVersion");
            }

            if (TryReadVersionProperty(hostType, applicationHost, "Version", out version))
            {
                return new EmbyRuntimeCompatibility(version, hostType.FullName + ".Version");
            }

            if (TryReadAssemblyInformationalVersion(hostType.GetTypeInfo().Assembly, out version))
            {
                return new EmbyRuntimeCompatibility(version, hostType.GetTypeInfo().Assembly.GetName().Name + ".InformationalVersion");
            }

            var assemblyVersion = hostType.GetTypeInfo().Assembly.GetName().Version;
            if (assemblyVersion != null)
            {
                return new EmbyRuntimeCompatibility(assemblyVersion, hostType.GetTypeInfo().Assembly.GetName().Name + ".AssemblyVersion");
            }

            return new EmbyRuntimeCompatibility(null, "unresolved");
        }

        private static bool TryReadVersionProperty(Type hostType, object applicationHost, string propertyName,
            out Version version)
        {
            version = null;
            var property = hostType.GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (property == null || property.GetIndexParameters().Length != 0)
            {
                return false;
            }

            try
            {
                return TryParseVersionValue(property.GetValue(applicationHost), out version);
            }
            catch
            {
                // Runtime version detection must never prevent the plugin from loading.
                return false;
            }
        }

        private static bool TryReadAssemblyInformationalVersion(Assembly assembly, out Version version)
        {
            version = null;
            if (assembly == null) return false;

            try
            {
                var attribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                return TryParseVersionValue(attribute?.InformationalVersion, out version);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryParseVersionValue(object value, out Version version)
        {
            version = null;

            if (value is Version directVersion)
            {
                version = directVersion;
                return true;
            }

            var text = value?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(text)) return false;

            if (Version.TryParse(text, out version)) return true;

            // Accept informational versions such as "4.10.0.21-beta+sha" without
            // taking a dependency on a semantic-version library.
            var end = 0;
            while (end < text.Length)
            {
                var ch = text[end];
                if (!char.IsDigit(ch) && ch != '.') break;
                end++;
            }

            if (end <= 0) return false;

            var numericPrefix = text.Substring(0, end).TrimEnd('.');
            return Version.TryParse(numericPrefix, out version);
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
