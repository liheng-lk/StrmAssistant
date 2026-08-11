using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
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
    public sealed class SeriesCollectionMembershipItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool ContainsSeriesDirectly { get; set; }
        public List<string> SeasonIds { get; set; } = new List<string>();
        public List<string> SeasonNames { get; set; } = new List<string>();
    }

    public sealed class SeriesCollectionMembershipResult
    {
        public bool Success { get; set; }
        public string SeriesId { get; set; }
        public string SeriesName { get; set; }
        public string UserId { get; set; }
        public List<SeriesCollectionMembershipItem> Collections { get; set; } = new List<SeriesCollectionMembershipItem>();
        public string Error { get; set; }
    }

    [Route("/StrmAssistant/SeriesCollections/{Id}", "GET",
        Summary = "Get collections containing a series or one of its seasons")]
    [Authenticated]
    public sealed class GetSeriesCollectionMembership : IReturn<SeriesCollectionMembershipResult>
    {
        public string Id { get; set; }
        public string UserId { get; set; }
    }

    /// <summary>
    /// Read-only series collection aggregation for the web detail screen. It never adds/removes
    /// BoxSet members. A collection is returned when it contains the Series itself OR any direct
    /// Season below that Series. Internal BoxSet membership access is reflection-bridged because
    /// Emby changed linked-child identifiers from Guid to long between server generations.
    /// </summary>
    public sealed class SeriesCollectionMembershipApiService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;

        public SeriesCollectionMembershipApiService(ILibraryManager libraryManager, IUserManager userManager)
        {
            _libraryManager = libraryManager;
            _userManager = userManager;
        }

        public object Get(GetSeriesCollectionMembership request)
        {
            var result = new SeriesCollectionMembershipResult
            {
                SeriesId = request?.Id,
                UserId = request?.UserId
            };

            try
            {
                if (!long.TryParse(request?.Id, out var seriesId))
                {
                    result.Error = "A numeric Series Id is required.";
                    return result;
                }

                var series = _libraryManager.GetItemById(seriesId) as Series;
                if (series == null)
                {
                    result.Error = "Series was not found.";
                    return result;
                }

                User user = null;
                if (!string.IsNullOrWhiteSpace(request?.UserId) && long.TryParse(request.UserId, out var userId))
                    user = _userManager.GetUserById(userId);

                result.SeriesName = series.Name;
                var seasons = _libraryManager.GetItemList(new InternalItemsQuery(user)
                {
                    ParentIds = new[] { series.InternalId },
                    IncludeItemTypes = new[] { nameof(Season) },
                    Recursive = false
                }).OfType<Season>().ToList();

                var boxSets = _libraryManager.GetItemList(new InternalItemsQuery(user)
                {
                    IncludeItemTypes = new[] { nameof(BoxSet) },
                    Recursive = true
                }).OfType<BoxSet>();

                foreach (var boxSet in boxSets)
                {
                    var containsSeries = ContainsItem(boxSet, series);
                    var matchingSeasons = seasons.Where(season => ContainsItem(boxSet, season)).ToList();
                    if (!containsSeries && matchingSeasons.Count == 0) continue;

                    result.Collections.Add(new SeriesCollectionMembershipItem
                    {
                        Id = boxSet.InternalId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Name = boxSet.Name,
                        ContainsSeriesDirectly = containsSeries,
                        SeasonIds = matchingSeasons
                            .Select(v => v.InternalId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                            .ToList(),
                        SeasonNames = matchingSeasons.Select(v => v.Name).Where(v => !string.IsNullOrWhiteSpace(v)).ToList()
                    });
                }

                result.Collections = result.Collections
                    .GroupBy(v => v.Id, StringComparer.Ordinal)
                    .Select(v => v.First())
                    .OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Error = ex.GetBaseException().Message;
            }

            return result;
        }

        private static bool ContainsItem(BoxSet boxSet, BaseItem item)
        {
            if (boxSet == null || item == null) return false;
            var type = boxSet.GetType();

            // Prefer native contains helpers when present. Their ID type differs by Emby generation.
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                         .Where(v => string.Equals(v.Name, "ContainsLinkedChildByItemId", StringComparison.Ordinal) &&
                                     v.GetParameters().Length == 1))
            {
                try
                {
                    var parameterType = method.GetParameters()[0].ParameterType;
                    object argument = null;
                    if (parameterType == typeof(long)) argument = item.InternalId;
                    else if (parameterType == typeof(long?)) argument = (long?)item.InternalId;
                    else if (parameterType == typeof(Guid)) argument = ReadGuidId(item);
                    else if (parameterType == typeof(Guid?)) argument = (Guid?)ReadGuidId(item);
                    if (argument == null) continue;
                    if (method.Invoke(boxSet, new[] { argument }) is bool value && value) return true;
                }
                catch
                {
                    // Try linked-child enumeration below.
                }
            }

            try
            {
                var getLinkedChildren = type.GetMethod("GetLinkedChildren",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                var linked = getLinkedChildren?.Invoke(boxSet, null) as IEnumerable;
                if (linked == null) return false;
                foreach (var child in linked)
                {
                    if (child == null) continue;
                    var childType = child.GetType();
                    var internalId = ReadLong(childType, child, "InternalId") ?? ReadLong(childType, child, "Id");
                    if (internalId.HasValue && internalId.Value == item.InternalId) return true;

                    var guid = ReadGuid(childType, child, "Id") ?? ReadGuid(childType, child, "ItemId");
                    var itemGuid = ReadGuidId(item);
                    if (guid.HasValue && itemGuid != Guid.Empty && guid.Value == itemGuid) return true;
                }
            }
            catch
            {
                // No compatible linked-child accessor in this runtime.
            }

            return false;
        }

        private static long? ReadLong(Type type, object instance, string name)
        {
            try
            {
                var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var value = property?.GetValue(instance);
                if (value is long direct) return direct;
                if (value != null && long.TryParse(Convert.ToString(value), out var parsed)) return parsed;
            }
            catch { }
            return null;
        }

        private static Guid? ReadGuid(Type type, object instance, string name)
        {
            try
            {
                var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var value = property?.GetValue(instance);
                if (value is Guid direct) return direct;
                if (value != null && Guid.TryParse(Convert.ToString(value), out var parsed)) return parsed;
            }
            catch { }
            return null;
        }

        private static Guid ReadGuidId(BaseItem item)
        {
            try
            {
                var property = item?.GetType().GetProperty("Id", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var value = property?.GetValue(item);
                if (value is Guid direct) return direct;
                if (value != null && Guid.TryParse(Convert.ToString(value), out var parsed)) return parsed;
            }
            catch { }
            return Guid.Empty;
        }
    }
}
