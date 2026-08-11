using HarmonyLib;
using MediaBrowser.Controller.Plugins;
using StrmAssistant.Experience;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Compatibility
{
    public sealed class RemoteDeepDeleteTransactionJournalStatus
    {
        public bool ExecuteAsyncPatched { get; set; }
        public long VerifiedDeletesRecorded { get; set; }
        public long MissingRetriesRecovered { get; set; }
        public long EntriesInvalidatedBecauseTargetExists { get; set; }
        public long JournalProbeFailures { get; set; }
        public long ExpiredEntriesPruned { get; set; }
        public int ActiveEntries { get; set; }
        public string JournalPath { get; set; }
        public string LastRemotePath { get; set; }
        public string LastError { get; set; }
        public string Error { get; set; }
    }

    public static class RemoteDeepDeleteTransactionJournalState
    {
        public static RemoteDeepDeleteTransactionJournalStatus Status { get; internal set; } =
            new RemoteDeepDeleteTransactionJournalStatus();
    }

    internal sealed class RemoteDeepDeleteJournalEntry
    {
        public string Provider { get; set; }
        public string RemotePath { get; set; }
        public string SourceTarget { get; set; }
        public DateTimeOffset VerifiedDeletedUtc { get; set; }
    }

    /// <summary>
    /// Persistent idempotence journal for the narrow remote-success/local-failure case. An entry is
    /// recorded only when ExecuteAsync first confirmed the object existed, issued a destructive request,
    /// and then verified that the object became missing. When TreatNotFoundAsSuccess is disabled, a later
    /// retry may accept Missing only if this exact provider/path/source identity has such a recent proof.
    /// No credentials, tokens, passwords or query strings are stored.
    /// </summary>
    public sealed class RemoteDeepDeleteTransactionJournalEntryPoint : IServerEntryPoint
    {
        private const string HarmonyId = "liheng-lk.strmassistantcustom.remote-delete-journal";
        private Harmony _harmony;

        public void Run()
        {
            var status = new RemoteDeepDeleteTransactionJournalStatus
            {
                JournalPath = RemoteDeepDeleteTransactionJournalStore.Path
            };
            RemoteDeepDeleteTransactionJournalState.Status = status;
            try
            {
                var execute = typeof(RemoteDeepDeleteService).GetMethod(nameof(RemoteDeepDeleteService.ExecuteAsync),
                    BindingFlags.Instance | BindingFlags.Public,
                    null, new[] { typeof(RemoteDeepDeletePlan), typeof(CancellationToken) }, null);
                if (execute == null)
                {
                    status.Error = "RemoteDeepDeleteService.ExecuteAsync was not found.";
                    return;
                }

                _harmony = new Harmony(HarmonyId);
                _harmony.Patch(execute,
                    prefix: new HarmonyMethod(typeof(RemoteDeepDeleteTransactionJournalPatches).GetMethod(
                        nameof(RemoteDeepDeleteTransactionJournalPatches.Prefix),
                        BindingFlags.Public | BindingFlags.Static)),
                    postfix: new HarmonyMethod(typeof(RemoteDeepDeleteTransactionJournalPatches).GetMethod(
                        nameof(RemoteDeepDeleteTransactionJournalPatches.Postfix),
                        BindingFlags.Public | BindingFlags.Static)));
                status.ExecuteAsyncPatched = true;
                RemoteDeepDeleteTransactionJournalStore.PruneExpired();
            }
            catch (Exception ex)
            {
                status.Error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Instance?.Logger?.Error("Remote deep-delete transaction journal initialization failed: " + status.Error);
            }
        }

        public void Dispose()
        {
            try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        }
    }

    public static class RemoteDeepDeleteTransactionJournalPatches
    {
        public static bool Prefix(RemoteDeepDeletePlan plan, CancellationToken cancellationToken,
            ref Task<RemoteDeepDeleteExecutionResult> __result)
        {
            if (plan == null || !plan.Applicable || !plan.Allowed || string.IsNullOrWhiteSpace(plan.RemotePath))
                return true;
            var options = RemoteDeepDeleteRuntimeSettings.GetSnapshot();
            if (options.TreatNotFoundAsSuccess) return true;
            if (!RemoteDeepDeleteTransactionJournalStore.Contains(plan)) return true;

            __result = ResumeVerifiedTransactionAsync(plan, cancellationToken);
            return false;
        }

        public static void Postfix(RemoteDeepDeletePlan plan, ref Task<RemoteDeepDeleteExecutionResult> __result)
        {
            if (plan == null || __result == null) return;
            __result = RecordVerifiedDeleteAsync(plan, __result);
        }

        private static async Task<RemoteDeepDeleteExecutionResult> ResumeVerifiedTransactionAsync(
            RemoteDeepDeletePlan plan, CancellationToken cancellationToken)
        {
            try
            {
                var probe = await new RemoteDeepDeleteService().ProbeAsync(plan, cancellationToken).ConfigureAwait(false);
                if (probe.Success && probe.Missing)
                {
                    Increment(status =>
                    {
                        status.MissingRetriesRecovered++;
                        status.LastRemotePath = plan.RemotePath;
                        status.LastError = null;
                    });
                    return new RemoteDeepDeleteExecutionResult
                    {
                        Success = true,
                        DeleteAccepted = false,
                        VerifiedDeleted = true,
                        AlreadyMissing = true,
                        PreProbeAlreadyMissing = true,
                        PreProbeStatusCode = probe.HttpStatusCode,
                        VerificationStatusCode = probe.HttpStatusCode,
                        Provider = plan.Provider,
                        RemotePath = plan.RemotePath
                    };
                }

                if (probe.Success && probe.Exists)
                {
                    RemoteDeepDeleteTransactionJournalStore.Remove(plan);
                    Increment(status =>
                    {
                        status.EntriesInvalidatedBecauseTargetExists++;
                        status.LastRemotePath = plan.RemotePath;
                    });
                    // The path has been recreated or the previous journal is stale. Fall back to the
                    // normal destructive pipeline, which will perform its own pre-probe and verification.
                    return await new RemoteDeepDeleteService().ExecuteAsync(plan, cancellationToken)
                        .ConfigureAwait(false);
                }

                Increment(status =>
                {
                    status.JournalProbeFailures++;
                    status.LastRemotePath = plan.RemotePath;
                    status.LastError = probe.Error ?? "Journal retry probe was ambiguous.";
                });
                return new RemoteDeepDeleteExecutionResult
                {
                    Success = false,
                    Provider = plan.Provider,
                    RemotePath = plan.RemotePath,
                    PreProbeStatusCode = probe.HttpStatusCode,
                    PreProbeError = probe.Error,
                    Error = "A prior verified-delete journal exists, but the current remote state could not be safely verified: " +
                            (probe.Error ?? "ambiguous probe result")
                };
            }
            catch (Exception ex)
            {
                Increment(status =>
                {
                    status.JournalProbeFailures++;
                    status.LastRemotePath = plan.RemotePath;
                    status.LastError = ex.GetBaseException().Message;
                });
                return new RemoteDeepDeleteExecutionResult
                {
                    Success = false,
                    Provider = plan.Provider,
                    RemotePath = plan.RemotePath,
                    Error = "Verified-delete journal retry failed: " + ex.GetBaseException().Message
                };
            }
        }

        private static async Task<RemoteDeepDeleteExecutionResult> RecordVerifiedDeleteAsync(RemoteDeepDeletePlan plan,
            Task<RemoteDeepDeleteExecutionResult> original)
        {
            var result = await original.ConfigureAwait(false);
            if (result?.Success == true && result.PreProbeVerifiedExists && result.DeleteAccepted &&
                result.VerifiedDeleted && !result.AlreadyMissing)
            {
                RemoteDeepDeleteTransactionJournalStore.Record(plan);
                Increment(status =>
                {
                    status.VerifiedDeletesRecorded++;
                    status.LastRemotePath = plan.RemotePath;
                    status.LastError = null;
                });
            }
            return result;
        }

        private static void Increment(Action<RemoteDeepDeleteTransactionJournalStatus> action)
        {
            var status = RemoteDeepDeleteTransactionJournalState.Status;
            if (status == null || action == null) return;
            lock (RemoteDeepDeleteTransactionJournalStore.SyncRoot)
            {
                action(status);
                status.ActiveEntries = RemoteDeepDeleteTransactionJournalStore.CountUnsafe;
            }
        }
    }

    public static class RemoteDeepDeleteTransactionJournalStore
    {
        internal static readonly object SyncRoot = new object();
        private static readonly TimeSpan MaxAge = TimeSpan.FromHours(48);
        private static readonly Dictionary<string, RemoteDeepDeleteJournalEntry> Entries =
            new Dictionary<string, RemoteDeepDeleteJournalEntry>(StringComparer.Ordinal);
        private static bool _loaded;
        private static string _path;

        internal static int CountUnsafe
        {
            get { EnsureLoadedUnsafe(); return Entries.Count; }
        }

        public static string Path
        {
            get
            {
                lock (SyncRoot)
                {
                    EnsureLoadedUnsafe();
                    return _path;
                }
            }
        }

        public static bool Contains(RemoteDeepDeletePlan plan)
        {
            if (plan == null) return false;
            lock (SyncRoot)
            {
                EnsureLoadedUnsafe();
                PruneExpiredUnsafe();
                return Entries.ContainsKey(Key(plan));
            }
        }

        public static void Record(RemoteDeepDeletePlan plan)
        {
            if (plan == null || string.IsNullOrWhiteSpace(plan.RemotePath)) return;
            lock (SyncRoot)
            {
                EnsureLoadedUnsafe();
                PruneExpiredUnsafe();
                Entries[Key(plan)] = new RemoteDeepDeleteJournalEntry
                {
                    Provider = plan.Provider ?? string.Empty,
                    RemotePath = NormalizePath(plan.RemotePath),
                    SourceTarget = NormalizeSource(plan.SourceTarget),
                    VerifiedDeletedUtc = DateTimeOffset.UtcNow
                };
                PersistUnsafe();
                UpdateStatusUnsafe();
            }
        }

        public static void Remove(RemoteDeepDeletePlan plan)
        {
            if (plan == null) return;
            lock (SyncRoot)
            {
                EnsureLoadedUnsafe();
                if (Entries.Remove(Key(plan))) PersistUnsafe();
                UpdateStatusUnsafe();
            }
        }

        public static void PruneExpired()
        {
            lock (SyncRoot)
            {
                EnsureLoadedUnsafe();
                if (PruneExpiredUnsafe() > 0) PersistUnsafe();
                UpdateStatusUnsafe();
            }
        }

        private static int PruneExpiredUnsafe()
        {
            var cutoff = DateTimeOffset.UtcNow - MaxAge;
            var expired = Entries.Where(pair => pair.Value == null || pair.Value.VerifiedDeletedUtc < cutoff)
                .Select(pair => pair.Key).ToArray();
            foreach (var key in expired) Entries.Remove(key);
            if (expired.Length > 0)
            {
                var status = RemoteDeepDeleteTransactionJournalState.Status;
                if (status != null) status.ExpiredEntriesPruned += expired.Length;
            }
            return expired.Length;
        }

        private static void EnsureLoadedUnsafe()
        {
            if (_loaded) return;
            _loaded = true;
            var root = Plugin.Instance?.DataFolderPath;
            if (string.IsNullOrWhiteSpace(root)) root = Plugin.Instance?.ApplicationPaths?.PluginConfigurationsPath;
            if (string.IsNullOrWhiteSpace(root)) root = System.IO.Path.GetTempPath();
            _path = System.IO.Path.Combine(root, "remote-deep-delete-verified-journal.tsv");
            if (!File.Exists(_path)) return;

            try
            {
                foreach (var line in File.ReadAllLines(_path))
                {
                    if (!TryParse(line, out var entry)) continue;
                    Entries[Key(entry.Provider, entry.RemotePath, entry.SourceTarget)] = entry;
                }
                PruneExpiredUnsafe();
            }
            catch (Exception ex)
            {
                var status = RemoteDeepDeleteTransactionJournalState.Status;
                if (status != null) status.LastError = "Journal load failed: " + ex.Message;
            }
            UpdateStatusUnsafe();
        }

        private static void PersistUnsafe()
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                var temp = _path + ".tmp";
                File.WriteAllLines(temp, Entries.Values
                    .OrderBy(entry => entry.VerifiedDeletedUtc)
                    .Select(Serialize));
                File.Copy(temp, _path, true);
                File.Delete(temp);
            }
            catch (Exception ex)
            {
                var status = RemoteDeepDeleteTransactionJournalState.Status;
                if (status != null) status.LastError = "Journal persist failed: " + ex.Message;
                Plugin.Instance?.Logger?.Warn("Remote deep-delete journal persist failed: " + ex.Message);
            }
        }

        private static string Serialize(RemoteDeepDeleteJournalEntry entry)
        {
            return entry.VerifiedDeletedUtc.UtcTicks + "\t" +
                   B64(entry.Provider) + "\t" + B64(entry.RemotePath) + "\t" + B64(entry.SourceTarget);
        }

        private static bool TryParse(string line, out RemoteDeepDeleteJournalEntry entry)
        {
            entry = null;
            try
            {
                var parts = (line ?? string.Empty).Split('\t');
                if (parts.Length != 4 || !long.TryParse(parts[0], out var ticks)) return false;
                var timestamp = new DateTimeOffset(ticks, TimeSpan.Zero);
                entry = new RemoteDeepDeleteJournalEntry
                {
                    VerifiedDeletedUtc = timestamp,
                    Provider = FromB64(parts[1]),
                    RemotePath = FromB64(parts[2]),
                    SourceTarget = FromB64(parts[3])
                };
                return !string.IsNullOrWhiteSpace(entry.RemotePath);
            }
            catch { return false; }
        }

        private static string B64(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
        }

        private static string FromB64(string value)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value ?? string.Empty));
        }

        private static string Key(RemoteDeepDeletePlan plan)
        {
            return Key(plan.Provider, NormalizePath(plan.RemotePath), NormalizeSource(plan.SourceTarget));
        }

        private static string Key(string provider, string remotePath, string sourceTarget)
        {
            return (provider ?? string.Empty).Trim().ToLowerInvariant() + "|" +
                   NormalizePath(remotePath) + "|" + NormalizeSource(sourceTarget);
        }

        private static string NormalizePath(string value)
        {
            return (value ?? string.Empty).Trim().Replace('\\', '/');
        }

        private static string NormalizeSource(string value)
        {
            // BuildPlan already strips query/fragment from SourceTarget; normalize separators only.
            return (value ?? string.Empty).Trim().Replace('\\', '/');
        }

        private static void UpdateStatusUnsafe()
        {
            var status = RemoteDeepDeleteTransactionJournalState.Status;
            if (status == null) return;
            status.ActiveEntries = Entries.Count;
            status.JournalPath = _path;
        }
    }
}
