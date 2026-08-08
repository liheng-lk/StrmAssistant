using System;
using System.Collections.Generic;
using System.Linq;

namespace StrmAssistant.Experience
{
    public sealed class CollectionItemsChange
    {
        public IReadOnlyList<long> AddedIds { get; set; }
        public IReadOnlyList<long> RemovedIds { get; set; }
        public bool HasChanges => (AddedIds?.Count ?? 0) > 0 || (RemovedIds?.Count ?? 0) > 0;
    }

    public static class CollectionChangeTracker
    {
        public static CollectionItemsChange Compare(IEnumerable<long> beforeIds, IEnumerable<long> afterIds)
        {
            var before = new HashSet<long>(beforeIds ?? Array.Empty<long>());
            var after = new HashSet<long>(afterIds ?? Array.Empty<long>());

            return new CollectionItemsChange
            {
                AddedIds = after.Except(before).OrderBy(id => id).ToList(),
                RemovedIds = before.Except(after).OrderBy(id => id).ToList()
            };
        }

        public static string FormatDescription(IEnumerable<string> itemNames)
        {
            return string.Join(Environment.NewLine,
                (itemNames ?? Array.Empty<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim()));
        }
    }
}
