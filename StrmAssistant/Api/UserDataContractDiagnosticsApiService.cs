using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace StrmAssistant.Api
{
    public sealed class UserDataContractItem
    {
        public string InternalId { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public string RuntimeType { get; set; }
        public string PresentationUniqueKey { get; set; }
        public List<string> UserDataKeys { get; set; } = new List<string>();
        public List<string> AlternateVersionIds { get; set; } = new List<string>();
        public Dictionary<string, string> ObservedProperties { get; set; } = new Dictionary<string, string>();
        public List<string> Notes { get; set; } = new List<string>();
    }

    public sealed class UserDataContractDiagnosticResult
    {
        public bool Success { get; set; }
        public string RequestedId { get; set; }
        public UserDataContractItem Primary { get; set; }
        public List<UserDataContractItem> AlternateVersions { get; set; } = new List<UserDataContractItem>();
        public List<string> DiscoveredKeyMethods { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public string Error { get; set; }
    }

    [Route("/StrmAssistant/Diagnostics/UserDataContract/{Id}", "GET",
        Summary = "Inspect BaseItem UserData/alternate-version key behavior without changing progress or favorites")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetUserDataContractDiagnostic : IReturn<UserDataContractDiagnosticResult>
    {
        public string Id { get; set; }
    }

    /// <summary>
    /// Read-only contract discovery used before implementing independent cross-library progress/favorites.
    /// It invokes only zero-argument BaseItem key/id accessors and never touches IUserDataManager or a database.
    /// </summary>
    public sealed class UserDataContractDiagnosticsApiService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;

        public UserDataContractDiagnosticsApiService(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public object Get(GetUserDataContractDiagnostic request)
        {
            var result = new UserDataContractDiagnosticResult { RequestedId = request?.Id };
            var item = ResolveItem(request?.Id);
            if (item == null)
            {
                result.Error = "Media item was not found.";
                return result;
            }

            try
            {
                result.Primary = Inspect(item, result.DiscoveredKeyMethods);
                foreach (var alternateId in result.Primary.AlternateVersionIds)
                {
                    if (!long.TryParse(alternateId, out var internalId)) continue;
                    var alternate = _libraryManager.GetItemById(internalId);
                    if (alternate == null || alternate.InternalId == item.InternalId) continue;
                    result.AlternateVersions.Add(Inspect(alternate, result.DiscoveredKeyMethods));
                }

                if (result.AlternateVersions.Count == 0)
                    result.Warnings.Add("No alternate-version items were resolved. Run this diagnostic on a currently merged multi-version item for the most useful contract output.");

                var all = new[] { result.Primary }.Concat(result.AlternateVersions).ToList();
                var distinctKeys = all.SelectMany(entry => entry.UserDataKeys)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count();
                if (all.Count > 1 && distinctKeys <= 1)
                    result.Warnings.Add("Merged versions appear to expose the same/limited UserData key set. Independent progress/favorites must not be enabled until the real IUserDataManager lookup path is verified.");

                result.DiscoveredKeyMethods = result.DiscoveredKeyMethods
                    .Distinct(StringComparer.Ordinal).OrderBy(value => value).ToList();
                result.Success = true;
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.GetType().Name + ": " + ex.Message;
                return result;
            }
        }

        private UserDataContractItem Inspect(BaseItem item, ICollection<string> discoveredMethods)
        {
            var entry = new UserDataContractItem
            {
                InternalId = item.InternalId.ToString(),
                Name = item.Name,
                Path = item.Path,
                RuntimeType = item.GetType().FullName,
                PresentationUniqueKey = ReadStringProperty(item, "PresentationUniqueKey")
            };

            ReadKnownProperty(entry, item, "PresentationUniqueKey");
            ReadKnownProperty(entry, item, "UserDataKey");
            ReadKnownProperty(entry, item, "SeriesPresentationUniqueKey");
            ReadKnownProperty(entry, item, "ProviderIds");

            foreach (var method in item.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                         .Where(method => method.GetParameters().Length == 0 &&
                             (method.Name.IndexOf("UserDataKey", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              method.Name.IndexOf("AlternateVersion", StringComparison.OrdinalIgnoreCase) >= 0)))
            {
                discoveredMethods.Add(item.GetType().FullName + "." + method);
                object value;
                try
                {
                    value = method.Invoke(item, Array.Empty<object>());
                }
                catch (Exception ex)
                {
                    entry.Notes.Add(method.Name + " invocation skipped: " + Unwrap(ex).Message);
                    continue;
                }

                if (method.Name.IndexOf("AlternateVersion", StringComparison.OrdinalIgnoreCase) >= 0)
                    AddValues(entry.AlternateVersionIds, value);
                else
                    AddValues(entry.UserDataKeys, value);
            }

            var alternateProperty = item.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(property => property.GetIndexParameters().Length == 0 &&
                    property.Name.IndexOf("AlternateVersion", StringComparison.OrdinalIgnoreCase) >= 0);
            if (alternateProperty?.CanRead == true)
            {
                try { AddValues(entry.AlternateVersionIds, alternateProperty.GetValue(item)); }
                catch { }
            }

            entry.UserDataKeys = entry.UserDataKeys.Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            entry.AlternateVersionIds = entry.AlternateVersionIds.Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return entry;
        }

        private static void AddValues(ICollection<string> target, object value)
        {
            if (value == null) return;
            if (value is string text)
            {
                target.Add(text);
                return;
            }
            if (value is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item != null) target.Add(Convert.ToString(item));
                }
                return;
            }
            target.Add(Convert.ToString(value));
        }

        private static void ReadKnownProperty(UserDataContractItem entry, object item, string propertyName)
        {
            try
            {
                var property = item.GetType().GetProperty(propertyName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property?.CanRead != true || property.GetIndexParameters().Length != 0) return;
                var value = property.GetValue(item);
                if (value != null) entry.ObservedProperties[propertyName] = Convert.ToString(value);
            }
            catch { }
        }

        private static string ReadStringProperty(object item, string propertyName)
        {
            try
            {
                return item?.GetType().GetProperty(propertyName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(item) as string;
            }
            catch
            {
                return null;
            }
        }

        private BaseItem ResolveItem(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || !long.TryParse(id, out var internalId)) return null;
            return _libraryManager.GetItemById(internalId);
        }

        private static Exception Unwrap(Exception ex)
        {
            return ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
        }
    }
}
