using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using StrmAssistant.Metadata;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace StrmAssistant.Metadata
{
    public sealed class LibraryProviderDefaultsPlan
    {
        public bool Success { get; set; }
        public bool Supported { get; set; }
        public bool Eligible { get; set; }
        public bool WouldChange { get; set; }
        public string ItemId { get; set; }
        public string Name { get; set; }
        public string CollectionType { get; set; }
        public string ProviderName { get; set; }
        public string MetadataProperty { get; set; }
        public string ImageProperty { get; set; }
        public List<string> CurrentMetadataFetchers { get; set; } = new List<string>();
        public List<string> ProposedMetadataFetchers { get; set; } = new List<string>();
        public List<string> CurrentImageFetchers { get; set; } = new List<string>();
        public List<string> ProposedImageFetchers { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();
    }

    public sealed class LibraryProviderDefaultsApplyResult
    {
        public bool Success { get; set; }
        public bool Executed { get; set; }
        public LibraryProviderDefaultsPlan Plan { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();
    }

    public sealed class LibraryProviderDefaultsService
    {
        private static readonly string[] MetadataCandidates =
            { "MetadataFetchers", "MetadataFetcherOrder" };
        private static readonly string[] ImageCandidates =
            { "ImageFetchers", "ImageFetcherOrder" };
        private static readonly string[] DisabledMetadataCandidates =
            { "DisabledMetadataFetchers" };
        private static readonly string[] DisabledImageCandidates =
            { "DisabledImageFetchers" };

        private readonly ILibraryManager _libraryManager;

        public LibraryProviderDefaultsService(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public LibraryProviderDefaultsPlan BuildPlan(string itemId, LibraryProviderDefaultsOptions settings = null)
        {
            settings ??= LibraryProviderDefaultsRuntimeSettings.GetSnapshot();
            var folder = FindVirtualFolder(itemId);
            if (folder == null)
                return ErrorPlan(itemId, "Virtual library was not found.");

            var options = GetProperty(folder, "LibraryOptions");
            var name = Convert.ToString(GetProperty(folder, "Name"));
            var collectionType = Convert.ToString(GetProperty(folder, "CollectionType"));
            var resolvedItemId = Convert.ToString(GetProperty(folder, "ItemId"));

            var plan = new LibraryProviderDefaultsPlan
            {
                ItemId = resolvedItemId ?? itemId,
                Name = name,
                CollectionType = collectionType,
                ProviderName = settings.ProviderName
            };

            if (options == null)
            {
                plan.Errors.Add("LibraryOptions is unavailable on this Emby build.");
                return plan;
            }

            var allowedTypes = LibraryProviderDefaultsRuntimeSettings.GetCollectionTypes(settings);
            plan.Eligible = allowedTypes.Count == 0 ||
                            (!string.IsNullOrWhiteSpace(collectionType) && allowedTypes.Contains(collectionType));
            if (!plan.Eligible)
            {
                plan.Success = true;
                plan.Supported = true;
                plan.Warnings.Add("The library collection type is outside the configured default-provider scope.");
                return plan;
            }

            var metadataProperty = FindStringCollectionProperty(options, MetadataCandidates);
            var imageProperty = FindStringCollectionProperty(options, ImageCandidates);
            plan.MetadataProperty = metadataProperty?.Name;
            plan.ImageProperty = imageProperty?.Name;
            plan.Supported = (!settings.ApplyMetadataFetcher || metadataProperty != null) &&
                             (!settings.ApplyImageFetcher || imageProperty != null);

            if (settings.ApplyMetadataFetcher && metadataProperty == null)
                plan.Errors.Add("No compatible metadata fetcher-order property was found on LibraryOptions.");
            if (settings.ApplyImageFetcher && imageProperty == null)
                plan.Errors.Add("No compatible image fetcher-order property was found on LibraryOptions.");
            if (!plan.Supported) return plan;

            plan.CurrentMetadataFetchers = ReadStrings(metadataProperty, options);
            plan.CurrentImageFetchers = ReadStrings(imageProperty, options);
            plan.ProposedMetadataFetchers = BuildProposed(plan.CurrentMetadataFetchers,
                settings.ProviderName, settings.ApplyMetadataFetcher, settings.OnlyWhenFetcherListEmpty);
            plan.ProposedImageFetchers = BuildProposed(plan.CurrentImageFetchers,
                settings.ProviderName, settings.ApplyImageFetcher, settings.OnlyWhenFetcherListEmpty);

            plan.WouldChange = !SequenceEqual(plan.CurrentMetadataFetchers, plan.ProposedMetadataFetchers) ||
                               !SequenceEqual(plan.CurrentImageFetchers, plan.ProposedImageFetchers);
            if (settings.OnlyWhenFetcherListEmpty)
            {
                if (settings.ApplyMetadataFetcher && plan.CurrentMetadataFetchers.Count > 0)
                    plan.Warnings.Add("Metadata fetchers are already configured; metadata defaults will not overwrite them.");
                if (settings.ApplyImageFetcher && plan.CurrentImageFetchers.Count > 0)
                    plan.Warnings.Add("Image fetchers are already configured; image defaults will not overwrite them.");
            }

            plan.Success = plan.Errors.Count == 0;
            return plan;
        }

        public LibraryProviderDefaultsApplyResult Apply(string itemId, bool confirm,
            LibraryProviderDefaultsOptions settings = null)
        {
            settings ??= LibraryProviderDefaultsRuntimeSettings.GetSnapshot();
            var plan = BuildPlan(itemId, settings);
            var result = new LibraryProviderDefaultsApplyResult { Plan = plan };
            if (!plan.Success || !plan.Supported)
            {
                result.Errors.AddRange(plan.Errors);
                return result;
            }
            if (!plan.Eligible)
            {
                result.Success = true;
                result.Warnings.AddRange(plan.Warnings);
                return result;
            }
            if (!confirm)
            {
                result.Warnings.Add("Apply was not confirmed. Review the plan and set Confirm=true.");
                return result;
            }
            if (!plan.WouldChange)
            {
                result.Success = true;
                result.Warnings.Add("No provider-order change is required.");
                return result;
            }

            var folder = FindVirtualFolder(plan.ItemId);
            var options = GetProperty(folder, "LibraryOptions");
            if (folder == null || options == null)
            {
                result.Errors.Add("The library disappeared before settings could be applied.");
                return result;
            }

            try
            {
                var metadataProperty = FindStringCollectionProperty(options, MetadataCandidates);
                var imageProperty = FindStringCollectionProperty(options, ImageCandidates);
                if (settings.ApplyMetadataFetcher)
                    WriteStrings(metadataProperty, options, plan.ProposedMetadataFetchers);
                if (settings.ApplyImageFetcher)
                    WriteStrings(imageProperty, options, plan.ProposedImageFetchers);

                RemoveFromDisabled(options, DisabledMetadataCandidates, settings.ProviderName);
                RemoveFromDisabled(options, DisabledImageCandidates, settings.ProviderName);

                if (!long.TryParse(plan.ItemId, out var internalId))
                    throw new InvalidOperationException("Virtual library ItemId is not a numeric internal id.");

                CollectionFolder.SaveLibraryOptions(internalId,
                    (MediaBrowser.Model.Configuration.LibraryOptions)options);
                result.Executed = true;
                result.Success = true;
                return result;
            }
            catch (Exception ex)
            {
                result.Errors.Add("Unable to save library provider defaults: " + ex.Message);
                return result;
            }
        }

        public IEnumerable<string> GetVirtualFolderIds()
        {
            return _libraryManager.GetVirtualFolders()
                .Select(folder => Convert.ToString(GetProperty(folder, "ItemId")))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private object FindVirtualFolder(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId)) return null;
            return _libraryManager.GetVirtualFolders().FirstOrDefault(folder =>
                string.Equals(Convert.ToString(GetProperty(folder, "ItemId")), itemId,
                    StringComparison.OrdinalIgnoreCase));
        }

        private static List<string> BuildProposed(List<string> current, string provider,
            bool apply, bool onlyWhenEmpty)
        {
            var result = current?.ToList() ?? new List<string>();
            if (!apply || string.IsNullOrWhiteSpace(provider)) return result;
            if (onlyWhenEmpty && result.Count > 0) return result;
            if (!result.Contains(provider, StringComparer.OrdinalIgnoreCase)) result.Insert(0, provider);
            return result;
        }

        private static void RemoveFromDisabled(object options, IEnumerable<string> propertyNames,
            string provider)
        {
            var property = FindStringCollectionProperty(options, propertyNames);
            if (property == null || string.IsNullOrWhiteSpace(provider)) return;
            var values = ReadStrings(property, options)
                .Where(value => !string.Equals(value, provider, StringComparison.OrdinalIgnoreCase)).ToList();
            WriteStrings(property, options, values);
        }

        private static PropertyInfo FindStringCollectionProperty(object target, IEnumerable<string> names)
        {
            if (target == null) return null;
            foreach (var name in names)
            {
                var property = target.GetType().GetProperty(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property?.CanRead != true || property.CanWrite != true) continue;
                if (property.PropertyType == typeof(string[]) ||
                    typeof(IEnumerable<string>).IsAssignableFrom(property.PropertyType))
                    return property;
            }
            return null;
        }

        private static List<string> ReadStrings(PropertyInfo property, object target)
        {
            if (property == null || target == null) return new List<string>();
            try
            {
                var value = property.GetValue(target);
                if (value is IEnumerable<string> strings)
                    return strings.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
                if (value is IEnumerable enumerable)
                    return enumerable.Cast<object>().Select(Convert.ToString)
                        .Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            }
            catch { }
            return new List<string>();
        }

        private static void WriteStrings(PropertyInfo property, object target, IList<string> values)
        {
            if (property == null || target == null) return;
            var clean = values?.Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();

            if (property.PropertyType == typeof(string[]))
            {
                property.SetValue(target, clean.ToArray());
                return;
            }
            if (property.PropertyType.IsAssignableFrom(typeof(List<string>)))
            {
                property.SetValue(target, clean);
                return;
            }

            var constructor = property.PropertyType.GetConstructor(new[] { typeof(IEnumerable<string>) });
            if (constructor != null)
            {
                property.SetValue(target, constructor.Invoke(new object[] { clean }));
                return;
            }

            throw new NotSupportedException("Unsupported provider-list property type: " + property.PropertyType.FullName);
        }

        private static bool SequenceEqual(IList<string> left, IList<string> right)
        {
            return (left ?? Array.Empty<string>()).SequenceEqual(right ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
        }

        private static object GetProperty(object target, string name)
        {
            try
            {
                return target?.GetType().GetProperty(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target);
            }
            catch
            {
                return null;
            }
        }

        private static LibraryProviderDefaultsPlan ErrorPlan(string itemId, string error)
        {
            return new LibraryProviderDefaultsPlan
            {
                ItemId = itemId,
                Errors = new List<string> { error }
            };
        }
    }
}
