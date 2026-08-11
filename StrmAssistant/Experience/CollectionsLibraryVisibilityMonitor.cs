using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace StrmAssistant.Experience
{
    /// <summary>
    /// Keeps the BoxSets/Collections top-level library hidden through each user's
    /// MyMediaExcludes setting. A small state file records only exclusions added by this
    /// plugin, allowing the option to be reversed without touching user-managed exclusions.
    /// </summary>
    public sealed class CollectionsLibraryVisibilityMonitor : IServerEntryPoint
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly string _statePath;
        private readonly object _stateLock = new object();
        private readonly HashSet<string> _managedEntries;
        private Timer _timer;
        private int _reconciling;

        public CollectionsLibraryVisibilityMonitor(
            ILibraryManager libraryManager,
            IUserManager userManager,
            IApplicationPaths applicationPaths)
        {
            _libraryManager = libraryManager;
            _userManager = userManager;
            _statePath = Path.Combine(applicationPaths.PluginConfigurationsPath,
                "StrmAssistantCustom.HideCollections.state");
            _managedEntries = LoadState(_statePath);
        }

        public void Run()
        {
            ReconcileSafe();
            _timer = new Timer(_ => ReconcileSafe(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        }

        public void Dispose()
        {
            _timer?.Dispose();
            _timer = null;
        }

        private void ReconcileSafe()
        {
            if (Interlocked.Exchange(ref _reconciling, 1) != 0) return;

            try
            {
                Reconcile();
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Warn($"CollectionsLibraryVisibilityMonitor: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _reconciling, 0);
            }
        }

        private void Reconcile()
        {
            var options = Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions;
            if (options == null) return;

            var collectionIds = GetCollectionsLibraryIds();
            var users = _userManager.Users?.Where(user => user != null).ToList() ?? new List<User>();
            var stateChanged = false;

            foreach (var user in users)
            {
                var configuration = _userManager.GetUserConfiguration(user);
                if (configuration == null) continue;

                var excludes = new HashSet<string>(
                    configuration.MyMediaExcludes ?? Array.Empty<string>(),
                    StringComparer.OrdinalIgnoreCase);
                var configChanged = false;
                var userKeyPrefix = UserKey(user) + "|";

                if (options.HideCollectionsLibrary)
                {
                    foreach (var collectionId in collectionIds)
                    {
                        var stateKey = userKeyPrefix + collectionId;
                        if (excludes.Contains(collectionId)) continue;

                        excludes.Add(collectionId);
                        configChanged = true;

                        lock (_stateLock)
                        {
                            if (_managedEntries.Add(stateKey)) stateChanged = true;
                        }
                    }

                    // Remove stale exclusions that were previously added by us for a BoxSets
                    // library that no longer exists. This keeps user configuration clean while
                    // preserving any exclusion we did not create.
                    var staleKeys = GetManagedKeysForUser(userKeyPrefix)
                        .Where(key => !collectionIds.Contains(ExtractCollectionId(key), StringComparer.OrdinalIgnoreCase))
                        .ToList();

                    foreach (var staleKey in staleKeys)
                    {
                        var staleId = ExtractCollectionId(staleKey);
                        if (excludes.Remove(staleId)) configChanged = true;
                        lock (_stateLock)
                        {
                            if (_managedEntries.Remove(staleKey)) stateChanged = true;
                        }
                    }
                }
                else
                {
                    var managedKeys = GetManagedKeysForUser(userKeyPrefix).ToList();
                    foreach (var managedKey in managedKeys)
                    {
                        var collectionId = ExtractCollectionId(managedKey);
                        if (excludes.Remove(collectionId)) configChanged = true;

                        lock (_stateLock)
                        {
                            if (_managedEntries.Remove(managedKey)) stateChanged = true;
                        }
                    }
                }

                if (configChanged)
                {
                    configuration.MyMediaExcludes = excludes.ToArray();
                    _userManager.UpdateConfiguration(user, configuration);
                }
            }

            // Drop state for users that no longer exist.
            var activeUserKeys = new HashSet<string>(users.Select(UserKey), StringComparer.OrdinalIgnoreCase);
            lock (_stateLock)
            {
                var orphaned = _managedEntries
                    .Where(entry => !activeUserKeys.Contains(ExtractUserId(entry)))
                    .ToList();
                foreach (var entry in orphaned)
                {
                    if (_managedEntries.Remove(entry)) stateChanged = true;
                }
            }

            if (stateChanged) SaveState();
        }

        private HashSet<string> GetCollectionsLibraryIds()
        {
            var folders = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(CollectionFolder) }
            }).OfType<CollectionFolder>();

            return new HashSet<string>(
                folders
                    .Where(folder => string.Equals(folder.CollectionType, CollectionType.BoxSets.ToString(),
                        StringComparison.OrdinalIgnoreCase))
                    .Select(folder => folder.Id.ToString("N")),
                StringComparer.OrdinalIgnoreCase);
        }

        private List<string> GetManagedKeysForUser(string prefix)
        {
            lock (_stateLock)
            {
                return _managedEntries
                    .Where(entry => entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
        }

        private static string UserKey(User user)
        {
            return user.Id.ToString("N");
        }

        private static string ExtractUserId(string stateKey)
        {
            var separator = stateKey.IndexOf('|');
            return separator < 0 ? stateKey : stateKey.Substring(0, separator);
        }

        private static string ExtractCollectionId(string stateKey)
        {
            var separator = stateKey.IndexOf('|');
            return separator < 0 || separator + 1 >= stateKey.Length
                ? string.Empty
                : stateKey.Substring(separator + 1);
        }

        private static HashSet<string> LoadState(string path)
        {
            try
            {
                if (!File.Exists(path)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                return new HashSet<string>(
                    File.ReadAllLines(path).Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => line.Trim()),
                    StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private void SaveState()
        {
            try
            {
                var directory = Path.GetDirectoryName(_statePath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                string[] lines;
                lock (_stateLock)
                {
                    lines = _managedEntries.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
                }

                File.WriteAllLines(_statePath, lines);
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Warn($"Unable to persist hide-collections state: {ex.Message}");
            }
        }
    }
}
