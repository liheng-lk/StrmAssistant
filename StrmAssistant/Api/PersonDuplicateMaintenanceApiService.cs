using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StrmAssistant.Api
{
    public sealed class PersonDuplicateCandidate
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public Dictionary<string, string> ProviderIds { get; set; } = new Dictionary<string, string>();
        public int RelatedItemCount { get; set; }
        public bool Selected { get; set; }
        public bool PlannedForDeletion { get; set; }
    }

    public sealed class PersonDuplicatePlanResult
    {
        public bool Success { get; set; }
        public string SelectedId { get; set; }
        public string SelectedName { get; set; }
        public List<PersonDuplicateCandidate> Candidates { get; set; } = new List<PersonDuplicateCandidate>();
        public List<string> DeleteIds { get; set; } = new List<string>();
        public List<string> MatchedProviderIds { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public string Error { get; set; }
    }

    public sealed class PersonDuplicateClearResult
    {
        public bool Success { get; set; }
        public bool Executed { get; set; }
        public string KeptId { get; set; }
        public List<string> DeletedIds { get; set; } = new List<string>();
        public string Error { get; set; }
    }

    [Route("/StrmAssistant/People/{Id}/Duplicates/Plan", "GET",
        Summary = "Plan duplicate-person cleanup while preserving the selected person")]
    [Authenticated(Roles = "Admin")]
    public sealed class GetPersonDuplicatePlan : IReturn<PersonDuplicatePlanResult>
    {
        public string Id { get; set; }
    }

    [Route("/StrmAssistant/People/{Id}/Duplicates/Clear", "POST",
        Summary = "Delete duplicate persons after explicit confirmation while preserving the selected person")]
    [Authenticated(Roles = "Admin")]
    public sealed class ClearPersonDuplicates : IReturn<PersonDuplicateClearResult>
    {
        public string Id { get; set; }
        public bool Confirm { get; set; }
    }

    public sealed class PersonDuplicateMaintenanceApiService : BaseApiService
    {
        private static readonly HashSet<string> ProviderKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tmdb", "imdb", "tvdb" };

        private readonly ILibraryManager _libraryManager;

        public PersonDuplicateMaintenanceApiService(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public object Get(GetPersonDuplicatePlan request)
        {
            return BuildPlan(request?.Id);
        }

        public object Post(ClearPersonDuplicates request)
        {
            var result = new PersonDuplicateClearResult { KeptId = request?.Id };
            if (request?.Confirm != true)
            {
                result.Error = "Confirm=true is required before duplicate persons are deleted.";
                return result;
            }

            var plan = BuildPlan(request.Id);
            if (!plan.Success)
            {
                result.Error = plan.Error ?? "Unable to build duplicate-person plan.";
                return result;
            }
            if (plan.DeleteIds.Count == 0)
            {
                result.Success = true;
                result.Error = "No duplicate persons need deletion.";
                return result;
            }

            var ids = plan.DeleteIds
                .Select(value => long.TryParse(value, out var id) ? (long?)id : null)
                .Where(value => value.HasValue)
                .Select(value => value.Value)
                .ToArray();
            if (ids.Length == 0)
            {
                result.Error = "Duplicate person ids could not be resolved.";
                return result;
            }

            try
            {
                _libraryManager.DeleteItems(ids);
                result.DeletedIds = ids.Select(id => id.ToString()).ToList();
                result.Executed = true;
                result.Success = true;
                Plugin.Instance?.Logger?.Info("Person duplicate cleanup kept {0} and deleted {1}.",
                    request.Id, string.Join(",", result.DeletedIds));
            }
            catch (Exception ex)
            {
                result.Error = ex.GetType().Name + ": " + ex.Message;
            }
            return result;
        }

        private PersonDuplicatePlanResult BuildPlan(string id)
        {
            var result = new PersonDuplicatePlanResult { SelectedId = id };
            if (string.IsNullOrWhiteSpace(id) || !long.TryParse(id, out var internalId))
            {
                result.Error = "Invalid person id.";
                return result;
            }

            var selected = _libraryManager.GetItemById(internalId) as Person;
            if (selected == null)
            {
                result.Error = "Selected person was not found.";
                return result;
            }

            result.SelectedName = selected.Name;
            var selectedIds = selected.ProviderIds?
                .Where(pair => ProviderKeys.Contains(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (selectedIds.Count == 0)
            {
                result.Error = "Selected person has no TMDB/IMDb/TVDB id to use for duplicate matching.";
                return result;
            }

            var allPeople = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Person) }
            }).OfType<Person>();

            var candidates = allPeople
                .Where(person => person.InternalId == selected.InternalId || SharesProviderId(person, selectedIds))
                .OrderByDescending(person => person.InternalId == selected.InternalId)
                .ThenBy(person => person.InternalId)
                .ToList();

            foreach (var person in candidates)
            {
                var isSelected = person.InternalId == selected.InternalId;
                var relatedCount = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    PersonIds = new[] { person.InternalId },
                    Recursive = true,
                    IncludeItemTypes = new[]
                    {
                        nameof(Movie), nameof(Series), nameof(Season), nameof(Episode), nameof(Video), nameof(Trailer)
                    }
                }).Count;

                result.Candidates.Add(new PersonDuplicateCandidate
                {
                    Id = person.InternalId.ToString(),
                    Name = person.Name,
                    ProviderIds = person.ProviderIds == null
                        ? new Dictionary<string, string>()
                        : person.ProviderIds.Where(pair => ProviderKeys.Contains(pair.Key))
                            .ToDictionary(pair => pair.Key, pair => pair.Value),
                    RelatedItemCount = relatedCount,
                    Selected = isSelected,
                    PlannedForDeletion = !isSelected
                });
            }

            result.DeleteIds = result.Candidates.Where(candidate => candidate.PlannedForDeletion)
                .Select(candidate => candidate.Id).ToList();
            result.MatchedProviderIds = selectedIds.Select(pair => pair.Key + ":" + pair.Value).ToList();
            if (result.DeleteIds.Count == 0)
                result.Warnings.Add("No duplicate person with a matching TMDB/IMDb/TVDB id was found.");
            if (result.Candidates.Any(candidate => candidate.PlannedForDeletion && candidate.RelatedItemCount > 0))
                result.Warnings.Add("One or more duplicate persons are still referenced by media items. Emby will need to reconcile people references after deletion/metadata refresh.");
            result.Success = true;
            return result;
        }

        private static bool SharesProviderId(Person person, IReadOnlyDictionary<string, string> selectedIds)
        {
            if (person?.ProviderIds == null) return false;
            foreach (var pair in person.ProviderIds)
            {
                if (!ProviderKeys.Contains(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) continue;
                if (selectedIds.TryGetValue(pair.Key, out var selectedValue) &&
                    string.Equals(selectedValue, pair.Value, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
