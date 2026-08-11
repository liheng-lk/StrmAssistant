using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace StrmAssistant.Metadata
{
    /// <summary>
    /// Polls virtual-library identities. The first pass establishes a baseline and never mutates
    /// existing libraries. A newly observed ID must survive one additional poll before automatic
    /// defaults are applied, reducing races with library creation/configuration.
    /// </summary>
    public sealed class LibraryProviderDefaultsMonitor : IServerEntryPoint
    {
        private readonly LibraryProviderDefaultsService _service;
        private readonly object _sync = new object();
        private readonly HashSet<string> _known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private Timer _timer;
        private bool _baselineReady;
        private int _running;

        public LibraryProviderDefaultsMonitor(ILibraryManager libraryManager)
        {
            _service = new LibraryProviderDefaultsService(libraryManager);
        }

        public void Run()
        {
            _timer = new Timer(_ => Poll(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30));
        }

        public void Dispose()
        {
            try { _timer?.Dispose(); } catch { }
        }

        private void Poll()
        {
            if (Interlocked.Exchange(ref _running, 1) != 0) return;
            try
            {
                var current = new HashSet<string>(_service.GetVirtualFolderIds(), StringComparer.OrdinalIgnoreCase);
                lock (_sync)
                {
                    if (!_baselineReady)
                    {
                        foreach (var id in current) _known.Add(id);
                        _baselineReady = true;
                        return;
                    }

                    var newIds = current.Where(id => !_known.Contains(id)).ToArray();
                    foreach (var id in newIds)
                    {
                        if (_pending.Contains(id))
                        {
                            ApplyToNewLibrary(id);
                            _known.Add(id);
                            _pending.Remove(id);
                        }
                        else
                        {
                            _pending.Add(id);
                        }
                    }

                    foreach (var id in current.Where(id => _known.Contains(id)).ToArray())
                        _pending.Remove(id);

                    var removed = _known.Where(id => !current.Contains(id)).ToArray();
                    foreach (var id in removed) _known.Remove(id);
                    var vanishedPending = _pending.Where(id => !current.Contains(id)).ToArray();
                    foreach (var id in vanishedPending) _pending.Remove(id);
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Warn("LibraryProviderDefaults monitor failed: " + ex.Message);
            }
            finally
            {
                Volatile.Write(ref _running, 0);
            }
        }

        private void ApplyToNewLibrary(string itemId)
        {
            var settings = LibraryProviderDefaultsRuntimeSettings.GetSnapshot();
            if (!settings.Enabled) return;

            var result = _service.Apply(itemId, true, settings);
            if (result.Executed)
            {
                Plugin.Instance?.Logger?.Info("LibraryProviderDefaults - applied {0} to new library {1}",
                    settings.ProviderName, itemId);
            }
            else if (result.Errors.Count > 0)
            {
                Plugin.Instance?.Logger?.Warn("LibraryProviderDefaults - new library {0} was not changed: {1}",
                    itemId, string.Join("; ", result.Errors));
            }
            else if (Plugin.Instance?.DebugMode == true)
            {
                Plugin.Instance.Logger.Debug("LibraryProviderDefaults - new library {0} required no change: {1}",
                    itemId, string.Join("; ", result.Warnings));
            }
        }
    }
}
