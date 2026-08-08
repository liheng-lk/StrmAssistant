using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Compatibility shims for Emby APIs whose public signatures changed after 4.8.
    ///
    /// These are extension methods on purpose: when an older Emby package still exposes
    /// the original instance method, the instance method wins. When a newer package removes
    /// that signature, C# falls back to the shim below. This keeps version-specific behavior
    /// isolated from the feature code.
    /// </summary>
    internal static class LegacyEmbyApiExtensions
    {
        public static List<ChapterInfo> GetChapters(this IItemRepository itemRepository, long itemId,
            MarkerType[] markerTypes)
        {
            var item = Plugin.LibraryApi?.GetItemsByIds(new[] { itemId }).FirstOrDefault();
            if (item == null)
            {
                return new List<ChapterInfo>();
            }

            var chapters = itemRepository.GetChapters(item) ?? new List<ChapterInfo>();
            if (markerTypes == null || markerTypes.Length == 0)
            {
                return chapters;
            }

            var markerSet = new HashSet<MarkerType>(markerTypes);
            return chapters.Where(chapter => markerSet.Contains(chapter.MarkerType)).ToList();
        }

        public static string[] GetExternalSubtitleFiles(this ILibraryManager libraryManager, long itemId)
        {
            // Some server implementations keep the old helper as an implementation detail even
            // after it disappears from ILibraryManager. Prefer it when present.
            try
            {
                var legacyMethod = libraryManager.GetType().GetMethod(
                    "GetExternalSubtitleFiles",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(long) },
                    null);

                if (legacyMethod?.Invoke(libraryManager, new object[] { itemId }) is IEnumerable<string> files)
                {
                    return files.Where(path => !string.IsNullOrEmpty(path)).ToArray();
                }
            }
            catch
            {
                // Fall through to the stored media stream representation.
            }

            var item = libraryManager.GetItemById(itemId);
            if (item == null)
            {
                return Array.Empty<string>();
            }

            return item.GetMediaStreams()
                .Where(stream => stream.IsExternal &&
                                 stream.Type == MediaStreamType.Subtitle &&
                                 stream.Protocol == MediaProtocol.File &&
                                 !string.IsNullOrEmpty(stream.Path))
                .Select(stream => stream.Path)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        public static List<MediaSourceInfo> GetStaticMediaSources(this IMediaSourceManager mediaSourceManager,
            BaseItem item, bool enableAlternateMediaSources, bool enablePathSubstitution,
            LibraryOptions libraryOptions, DeviceProfile deviceProfile, User user)
        {
            var method = mediaSourceManager.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(candidate => candidate.Name == "GetStaticMediaSources")
                .FirstOrDefault(candidate =>
                {
                    var parameters = candidate.GetParameters();
                    return parameters.Length == 7 && parameters[0].ParameterType == typeof(BaseItem);
                });

            if (method == null)
            {
                throw new MissingMethodException(mediaSourceManager.GetType().FullName,
                    "GetStaticMediaSources(BaseItem, bool, bool, bool, LibraryOptions, DeviceProfile, User)");
            }

            var result = method.Invoke(mediaSourceManager,
                new object[]
                {
                    item,
                    enableAlternateMediaSources,
                    enablePathSubstitution,
                    false,
                    libraryOptions,
                    deviceProfile,
                    user
                });

            if (result is List<MediaSourceInfo> list)
            {
                return list;
            }

            if (result is IEnumerable<MediaSourceInfo> enumerable)
            {
                return enumerable.ToList();
            }

            return new List<MediaSourceInfo>();
        }

        /// <summary>
        /// Emby 4.9 changed IMediaMountManager.Mount from a ReadOnlyMemory-based API and also
        /// changed the returned mount object's path property from MountedPath to
        /// MountedPathInfo.FullName. The adapter preserves the old call site used by this fork.
        /// </summary>
        public static async Task<LegacyMediaMount> Mount(this IMediaMountManager mediaMountManager,
            ReadOnlyMemory<char> mediaPath, string container, CancellationToken cancellationToken)
        {
            var method = mediaMountManager.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(candidate => candidate.Name == "Mount")
                .FirstOrDefault(candidate =>
                {
                    var parameters = candidate.GetParameters();
                    return parameters.Length == 3 && parameters[0].ParameterType == typeof(string);
                });

            if (method == null)
            {
                throw new MissingMethodException(mediaMountManager.GetType().FullName,
                    "Mount(string, ..., CancellationToken)");
            }

            var parameters = method.GetParameters();
            object containerArgument = container;

            if (parameters[1].ParameterType == typeof(ReadOnlyMemory<char>))
            {
                containerArgument = string.IsNullOrEmpty(container)
                    ? ReadOnlyMemory<char>.Empty
                    : container.AsMemory();
            }
            else if (parameters[1].ParameterType == typeof(ReadOnlyMemory<char>?))
            {
                containerArgument = string.IsNullOrEmpty(container)
                    ? (ReadOnlyMemory<char>?)null
                    : container.AsMemory();
            }

            var pending = method.Invoke(mediaMountManager,
                new[] { (object)mediaPath.ToString(), containerArgument, cancellationToken });

            if (!(pending is Task task))
            {
                throw new InvalidOperationException(
                    $"Unsupported Mount return type: {pending?.GetType().FullName ?? "null"}");
            }

            await task.ConfigureAwait(false);

            var resultProperty = task.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public);
            var mount = resultProperty?.GetValue(task);
            return new LegacyMediaMount(mount);
        }
    }

    internal sealed class LegacyMediaMount : IDisposable
    {
        private readonly object _innerMount;

        public LegacyMediaMount(object innerMount)
        {
            _innerMount = innerMount;
            MountedPath = ResolveMountedPath(innerMount);
        }

        public string MountedPath { get; }

        private static string ResolveMountedPath(object mount)
        {
            if (mount == null)
            {
                return null;
            }

            var mountType = mount.GetType();

            // Emby 4.8-style implementations.
            var legacyPath = mountType.GetProperty("MountedPath", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(mount) as string;
            if (!string.IsNullOrEmpty(legacyPath))
            {
                return legacyPath;
            }

            // Emby 4.9+ uses MountedPathInfo.FullName.
            var pathInfo = mountType.GetProperty("MountedPathInfo", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(mount);
            return pathInfo?.GetType().GetProperty("FullName", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(pathInfo) as string;
        }

        public void Dispose()
        {
            if (_innerMount is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
