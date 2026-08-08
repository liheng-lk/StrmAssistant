using StrmAssistant.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace StrmAssistant.Experience
{
    public enum DeepDeleteEntryKind
    {
        StrmTarget,
        AssociatedFile
    }

    public sealed class DeepDeletePlanEntry
    {
        public string Path { get; set; }
        public DeepDeleteEntryKind Kind { get; set; }
        public bool Allowed { get; set; }
        public string Reason { get; set; }
    }

    public sealed class DeepDeletePlan
    {
        public string SourcePath { get; set; }
        public List<DeepDeletePlanEntry> Entries { get; } = new List<DeepDeletePlanEntry>();
        public List<string> Warnings { get; } = new List<string>();

        public bool HasBlockedEntries => Entries.Any(entry => !entry.Allowed);
    }

    public sealed class DeepDeleteExecutionResult
    {
        public bool DryRun { get; set; }
        public List<string> DeletedPaths { get; } = new List<string>();
        public List<string> DeletedDirectories { get; } = new List<string>();
        public List<string> SkippedPaths { get; } = new List<string>();
        public List<string> Errors { get; } = new List<string>();
    }

    /// <summary>
    /// Safety-first deep-delete planner/executor.
    ///
    /// This service intentionally does NOT subscribe to ILibraryManager.ItemRemoved. A library
    /// scan can remove database items for many reasons that are not an explicit user delete.
    /// It is invoked only by the authenticated plugin-owned deep-delete API.
    /// </summary>
    public sealed class DeepDeleteService
    {
        private static readonly HashSet<string> AssociatedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".nfo", ".json", ".xml",
            ".jpg", ".jpeg", ".png", ".webp",
            ".srt", ".ass", ".ssa", ".sub", ".idx", ".vtt", ".sup"
        };

        public DeepDeletePlan BuildPlan(string sourcePath, ExperienceEnhanceOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            var plan = new DeepDeletePlan { SourcePath = sourcePath };

            if (!options.EnableDeepDelete)
            {
                plan.Warnings.Add("Deep delete is disabled in plugin options.");
                return plan;
            }

            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                plan.Warnings.Add("Source path is empty.");
                return plan;
            }

            var allowedRoots = ParseAllowedRoots(options.DeepDeleteAllowedRoots);
            if (allowedRoots.Count == 0)
            {
                plan.Warnings.Add("No allowed delete roots are configured. Target deletion is blocked.");
            }

            if (!string.Equals(Path.GetExtension(sourcePath), ".strm", StringComparison.OrdinalIgnoreCase))
            {
                plan.Warnings.Add("Deep delete currently resolves local targets from .strm files. Symlink target resolution will be added separately.");
                return plan;
            }

            var targetPath = ResolveStrmTarget(sourcePath, plan.Warnings);
            if (string.IsNullOrEmpty(targetPath)) return plan;

            if (options.DeepDeleteTargetFile)
            {
                AddEntry(plan, targetPath, DeepDeleteEntryKind.StrmTarget, allowedRoots,
                    "STRM local target file");
            }

            if (options.DeepDeleteAssociatedFiles)
            {
                AddAssociatedFiles(plan, targetPath, allowedRoots);
            }

            return plan;
        }

        public DeepDeleteExecutionResult Execute(DeepDeletePlan plan, ExperienceEnhanceOptions options)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (options == null) throw new ArgumentNullException(nameof(options));

            var result = new DeepDeleteExecutionResult { DryRun = options.DeepDeleteDryRun };

            if (!options.EnableDeepDelete)
            {
                result.Errors.Add("Deep delete is disabled.");
                return result;
            }

            foreach (var entry in plan.Entries)
            {
                if (!entry.Allowed)
                {
                    result.SkippedPaths.Add(entry.Path);
                    continue;
                }

                if (options.DeepDeleteDryRun)
                {
                    result.SkippedPaths.Add(entry.Path);
                    continue;
                }

                try
                {
                    if (!File.Exists(entry.Path))
                    {
                        result.SkippedPaths.Add(entry.Path);
                        continue;
                    }

                    File.Delete(entry.Path);
                    result.DeletedPaths.Add(entry.Path);
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"{entry.Path}: {ex.Message}");
                }
            }

            if (!options.DeepDeleteDryRun && options.DeepDeleteEmptyDirectories && result.Errors.Count == 0)
            {
                CleanupEmptyDirectories(result, options.DeepDeleteAllowedRoots);
            }

            return result;
        }

        private static void CleanupEmptyDirectories(DeepDeleteExecutionResult result, string allowedRootsRaw)
        {
            var allowedRoots = ParseAllowedRoots(allowedRootsRaw);
            if (allowedRoots.Count == 0 || result.DeletedPaths.Count == 0) return;

            var candidateDirectories = result.DeletedPaths
                .Select(path =>
                {
                    try { return Path.GetDirectoryName(Path.GetFullPath(path)); }
                    catch { return null; }
                })
                .Where(path => !string.IsNullOrEmpty(path))
                .Distinct(PathComparer)
                .OrderByDescending(path => path.Length)
                .ToList();

            foreach (var startDirectory in candidateDirectories)
            {
                var root = allowedRoots
                    .Where(candidateRoot => IsWithinRoot(startDirectory, candidateRoot))
                    .OrderByDescending(candidateRoot => candidateRoot.Length)
                    .FirstOrDefault();

                if (string.IsNullOrEmpty(root)) continue;

                var current = startDirectory;
                while (!string.IsNullOrEmpty(current) &&
                       !string.Equals(current, root, PathComparison) &&
                       IsWithinRoot(current, root))
                {
                    try
                    {
                        if (!Directory.Exists(current)) break;
                        if (Directory.EnumerateFileSystemEntries(current).Any()) break;

                        Directory.Delete(current, false);
                        result.DeletedDirectories.Add(current);
                        current = Path.GetDirectoryName(current);
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add($"Directory cleanup {current}: {ex.Message}");
                        break;
                    }
                }
            }
        }

        private static void AddAssociatedFiles(DeepDeletePlan plan, string targetPath, IReadOnlyCollection<string> allowedRoots)
        {
            string directory;
            string baseName;
            try
            {
                directory = Path.GetDirectoryName(targetPath);
                baseName = Path.GetFileNameWithoutExtension(targetPath);
            }
            catch (Exception ex)
            {
                plan.Warnings.Add($"Unable to inspect associated files: {ex.Message}");
                return;
            }

            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(baseName) || !Directory.Exists(directory)) return;

            IEnumerable<string> candidates;
            try
            {
                candidates = Directory.EnumerateFiles(directory, baseName + ".*");
            }
            catch (Exception ex)
            {
                plan.Warnings.Add($"Unable to enumerate associated files in {directory}: {ex.Message}");
                return;
            }

            foreach (var candidate in candidates)
            {
                if (string.Equals(candidate, targetPath, PathComparison)) continue;
                if (!AssociatedExtensions.Contains(Path.GetExtension(candidate))) continue;

                AddEntry(plan, candidate, DeepDeleteEntryKind.AssociatedFile, allowedRoots,
                    "Associated metadata/image/subtitle file");
            }
        }

        private static void AddEntry(DeepDeletePlan plan, string path, DeepDeleteEntryKind kind,
            IReadOnlyCollection<string> allowedRoots, string reason)
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch (Exception ex)
            {
                plan.Entries.Add(new DeepDeletePlanEntry
                {
                    Path = path,
                    Kind = kind,
                    Allowed = false,
                    Reason = $"Invalid path: {ex.Message}"
                });
                return;
            }

            var allowed = allowedRoots.Any(root => IsWithinRoot(fullPath, root));
            plan.Entries.Add(new DeepDeletePlanEntry
            {
                Path = fullPath,
                Kind = kind,
                Allowed = allowed,
                Reason = allowed ? reason : "Blocked: path is outside configured allowed roots"
            });
        }

        private static string ResolveStrmTarget(string sourcePath, ICollection<string> warnings)
        {
            string firstLine;
            try
            {
                firstLine = File.ReadLines(sourcePath)
                    .Select(line => line?.Trim())
                    .FirstOrDefault(line => !string.IsNullOrEmpty(line));
            }
            catch (Exception ex)
            {
                warnings.Add($"Unable to read STRM file: {ex.Message}");
                return null;
            }

            if (string.IsNullOrEmpty(firstLine))
            {
                warnings.Add("STRM file does not contain a target path.");
                return null;
            }

            if (Uri.TryCreate(firstLine, UriKind.Absolute, out var uri))
            {
                if (!uri.IsFile)
                {
                    warnings.Add($"Remote STRM target is not deletable: {uri.Scheme}://");
                    return null;
                }

                return uri.LocalPath;
            }

            try
            {
                if (Path.IsPathRooted(firstLine)) return Path.GetFullPath(firstLine);

                var sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
                if (string.IsNullOrEmpty(sourceDirectory)) return null;
                return Path.GetFullPath(Path.Combine(sourceDirectory, firstLine));
            }
            catch (Exception ex)
            {
                warnings.Add($"Unable to resolve STRM target: {ex.Message}");
                return null;
            }
        }

        private static List<string> ParseAllowedRoots(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new List<string>();

            return raw
                .Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim())
                .Where(value => !string.IsNullOrEmpty(value))
                .Select(TryNormalizeRoot)
                .Where(value => value != null)
                .Distinct(PathComparer)
                .ToList();
        }

        private static string TryNormalizeRoot(string value)
        {
            try
            {
                var full = Path.GetFullPath(value);
                return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return null;
            }
        }

        private static bool IsWithinRoot(string candidate, string root)
        {
            if (string.Equals(candidate, root, PathComparison)) return true;

            var prefix = root + Path.DirectorySeparatorChar;
            if (candidate.StartsWith(prefix, PathComparison)) return true;

            if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
            {
                prefix = root + Path.AltDirectorySeparatorChar;
                if (candidate.StartsWith(prefix, PathComparison)) return true;
            }

            return false;
        }

        private static StringComparison PathComparison =>
            Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        private static StringComparer PathComparer =>
            Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    }
}
