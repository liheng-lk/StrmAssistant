using MediaBrowser.Controller.Plugins;
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace StrmAssistant.Compatibility
{
    public sealed class AssemblyResolutionCapabilityStatus
    {
        public bool Installed { get; set; }
        public bool MovieDbAlreadyLoaded { get; set; }
        public string MovieDbAssemblyVersion { get; set; }
        public string MovieDbLocation { get; set; }
        public int ResolveHits { get; set; }
        public string Error { get; set; }
    }

    public static class AssemblyResolutionCompatibilityState
    {
        public static AssemblyResolutionCapabilityStatus Status { get; internal set; } =
            new AssemblyResolutionCapabilityStatus();
    }

    /// <summary>
    /// Emby 4.10 can load bundled provider assemblies into a load context where a later
    /// Assembly.Load(simpleName) from a plugin no longer resolves them. Reuse an already
    /// loaded MovieDb assembly instead of loading another copy or depending on its path.
    /// </summary>
    public sealed class AssemblyResolutionCompatibilityEntryPoint : IServerEntryPoint
    {
        private bool _installed;

        public void Run()
        {
            var status = new AssemblyResolutionCapabilityStatus();
            AssemblyResolutionCompatibilityState.Status = status;

            try
            {
                var movieDb = FindLoaded("MovieDb");
                status.MovieDbAlreadyLoaded = movieDb != null;
                status.MovieDbAssemblyVersion = movieDb?.GetName().Version?.ToString();
                try { status.MovieDbLocation = movieDb?.Location; } catch { }

                AppDomain.CurrentDomain.AssemblyResolve += ResolveAppDomainAssembly;
                AssemblyLoadContext.Default.Resolving += ResolveDefaultLoadContext;
                _installed = true;
                status.Installed = true;
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Warn("Assembly resolution compatibility unavailable: " + status.Error);
            }
        }

        private static Assembly ResolveAppDomainAssembly(object sender, ResolveEventArgs args)
        {
            var requested = GetSimpleName(args?.Name);
            if (!string.Equals(requested, "MovieDb", StringComparison.OrdinalIgnoreCase)) return null;

            var assembly = FindLoaded(requested);
            if (assembly != null)
                AssemblyResolutionCompatibilityState.Status.ResolveHits++;
            return assembly;
        }

        private static Assembly ResolveDefaultLoadContext(AssemblyLoadContext context, AssemblyName name)
        {
            if (!string.Equals(name?.Name, "MovieDb", StringComparison.OrdinalIgnoreCase)) return null;

            var assembly = FindLoaded(name.Name);
            if (assembly != null)
                AssemblyResolutionCompatibilityState.Status.ResolveHits++;
            return assembly;
        }

        private static Assembly FindLoaded(string simpleName)
        {
            if (string.IsNullOrWhiteSpace(simpleName)) return null;
            return AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly =>
                    string.Equals(assembly.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase));
        }

        private static string GetSimpleName(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName)) return null;
            try { return new AssemblyName(displayName).Name; }
            catch
            {
                var comma = displayName.IndexOf(',');
                return comma > 0 ? displayName.Substring(0, comma).Trim() : displayName.Trim();
            }
        }

        public void Dispose()
        {
            if (!_installed) return;
            try { AppDomain.CurrentDomain.AssemblyResolve -= ResolveAppDomainAssembly; } catch { }
            try { AssemblyLoadContext.Default.Resolving -= ResolveDefaultLoadContext; } catch { }
            _installed = false;
        }
    }
}
