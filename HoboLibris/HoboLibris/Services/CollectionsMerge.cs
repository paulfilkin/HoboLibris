using HoboLibris.Resources;

namespace HoboLibris.Services
{
    public enum ConflictKind
    {
        /// <summary>Renamed here and renamed differently on the eReader.</summary>
        Renamed,
        /// <summary>Deleted here, changed on the eReader.</summary>
        DeletedHere,
        /// <summary>Changed here, deleted on the eReader.</summary>
        DeletedThere,
        /// <summary>A collection here and one on the eReader end up with the same name.</summary>
        SameName,
        /// <summary>Books added to a collection here are no longer on the eReader.</summary>
        BooksGone,
    }

    public record MergeOption(string Value, string Label);

    public record MergeConflict(string Key, ConflictKind Kind, string Text, IReadOnlyList<MergeOption> Options);

    public class MergeResult
    {
        /// <summary>The merged collections; complete only when there are no conflicts left.</summary>
        public SortedDictionary<string, SortedSet<string>> Collections { get; } = new(Services.Collections.NameComparer);
        /// <summary>Merged collections whose name differs from the eReader's: current name -> name on the eReader.</summary>
        public Dictionary<string, string> Renamed { get; } = new(Services.Collections.NameComparer);
        /// <summary>What the person has still to decide.</summary>
        public List<MergeConflict> Conflicts { get; } = new();
    }

    /// <summary>
    /// A three-way merge of the collections (#32).  The base is the eReader as it was read (here's
    /// Original), here is the person's changes since (here's Current), there is the eReader as it is
    /// now (there's Original).  A change made on one side only is kept, the same change made on both
    /// is kept once, and the books in a collection are merged as the base plus what each side added,
    /// minus what each side took out.  A collection is matched across the sides by the eReader's own
    /// id for it (Shelf.Id), then by name, so a rename on the eReader is seen as a rename when the
    /// firmware keeps the id, and as a delete and a new collection when it doesn't.
    ///
    /// What clashes is returned as conflicts, each with its options; the person's choices, by key,
    /// go into the next call.  A choice can lead to a new conflict (a rename that takes a name the
    /// eReader also uses), so the merge is run until none is left.  Books the Collections screen
    /// doesn't list (such as articles, #28) are kept as the eReader has them.
    /// </summary>
    public static class CollectionsMerge
    {
        public const string Delete = "delete";
        public const string Keep = "keep";
        public const string Here = "here";
        public const string There = "there";
        public const string Both = "both";
        public const string LeaveOut = "leave";

        private class Entry
        {
            public string Key = "";
            /// <summary>Null until a rename conflict is decided.</summary>
            public string? Name;
            /// <summary>The name came from here: a new collection, or one renamed here.</summary>
            public bool NamedHere;
            /// <summary>Its name on the eReader now; null when it isn't there.</summary>
            public string? ThereName;
            public SortedSet<string> Ids = new(StringComparer.Ordinal);
            /// <summary>Books put in it here that the eReader doesn't have in it.</summary>
            public HashSet<string> AddedHere = new(StringComparer.Ordinal);
        }

        private static List<MergeOption> Options(params (string Value, string Label)[] options) =>
            options.Select(o => new MergeOption(o.Value, o.Label)).ToList();

        private static string Actual(IEnumerable<string> names, string name) =>
            names.First(n => Collections.NameComparer.Equals(n, name));

        public static MergeResult Merge(Collections here, Collections there, IReadOnlyDictionary<string, string>? choices = null)
        {
            choices ??= new Dictionary<string, string>();
            string? Chosen(string key) => choices.TryGetValue(key, out var v) ? v : null;
            var cmp = Collections.NameComparer;
            var result = new MergeResult();
            var entries = new List<Entry>();

            // Which collection on the eReader now each one read before is: by its id, then by name.
            var thereById = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, id) in there.ShelfIds)
            {
                if (there.Original.ContainsKey(name))
                {
                    thereById.TryAdd(id, Actual(there.Original.Keys, name));
                }
            }
            var thereOf = new Dictionary<string, string>(cmp);
            var claimed = new HashSet<string>(cmp);
            foreach (var b in here.Original.Keys)
            {
                if (here.ShelfIds.TryGetValue(b, out var id) && thereById.TryGetValue(id, out var name) && claimed.Add(name))
                {
                    thereOf[b] = name;
                }
            }
            foreach (var b in here.Original.Keys)
            {
                if (!thereOf.ContainsKey(b) && there.Original.ContainsKey(b) && !claimed.Contains(b))
                {
                    var name = Actual(there.Original.Keys, b);
                    thereOf[b] = name;
                    claimed.Add(name);
                }
            }

            // Which collection here each one read before is: the one renamed from it, or the one of the same name.
            string? HereOf(string b)
            {
                foreach (var (now, before) in here.Renamed)
                {
                    if (cmp.Equals(before, b) && here.Current.ContainsKey(now))
                    {
                        return Actual(here.Current.Keys, now);
                    }
                }
                return here.Current.ContainsKey(b) && !here.Renamed.ContainsKey(b) ? Actual(here.Current.Keys, b) : null;
            }

            var hereUsed = new HashSet<string>(cmp);
            foreach (var (b, bs) in here.Original)
            {
                var key = "base:" + b;
                var hn = HereOf(b);
                var tn = thereOf.GetValueOrDefault(b);
                var hs = hn != null ? here.Current[hn] : null;
                var ts = tn != null ? there.Original[tn] : null;
                if (hn != null)
                {
                    hereUsed.Add(hn);
                }
                var hereSame = hn == b && hs!.SetEquals(bs);
                var thereSame = tn == b && ts!.SetEquals(bs);

                if (hn == null && tn == null)
                {
                    continue;
                }
                if (hn == null)
                {
                    // Deleted here: gone, unless the eReader changed it since.
                    if (thereSame || Chosen(key) == Delete)
                    {
                        continue;
                    }
                    if (Chosen(key) == Keep)
                    {
                        entries.Add(new Entry { Key = key, Name = tn, ThereName = tn, Ids = new SortedSet<string>(ts!, StringComparer.Ordinal) });
                        continue;
                    }
                    result.Conflicts.Add(new MergeConflict(key, ConflictKind.DeletedHere, string.Format(Strings.Coll_ConflictDeletedHere, b),
                        Options((Delete, Strings.Coll_DeleteIt), (Keep, Strings.Coll_KeepEreaders))));
                    continue;
                }
                if (tn == null)
                {
                    // Deleted on the eReader: gone, unless it was changed here since.
                    if (hereSame || Chosen(key) == Delete)
                    {
                        continue;
                    }
                    if (Chosen(key) == Keep)
                    {
                        entries.Add(new Entry
                        {
                            Key = key, Name = hn, NamedHere = true,
                            Ids = new SortedSet<string>(hs!, StringComparer.Ordinal),
                            AddedHere = new HashSet<string>(hs!, StringComparer.Ordinal),
                        });
                        continue;
                    }
                    result.Conflicts.Add(new MergeConflict(key, ConflictKind.DeletedThere, string.Format(Strings.Coll_ConflictDeletedThere, b),
                        Options((Delete, Strings.Coll_DeleteIt), (Keep, Strings.Coll_KeepMine))));
                    continue;
                }

                // On both sides: the name changed on one side at most, or a choice.
                string? merged;
                var namedHere = false;
                if (hn == tn || hn == b)
                {
                    merged = tn;
                }
                else if (tn == b)
                {
                    merged = hn;
                    namedHere = true;
                }
                else if (Chosen(key) == Here)
                {
                    merged = hn;
                    namedHere = true;
                }
                else if (Chosen(key) == There)
                {
                    merged = tn;
                }
                else
                {
                    merged = null;
                    result.Conflicts.Add(new MergeConflict(key, ConflictKind.Renamed, string.Format(Strings.Coll_ConflictRenamed, b, hn, tn),
                        Options((Here, string.Format(Strings.Coll_Use, hn)), (There, string.Format(Strings.Coll_Use, tn)))));
                }
                var ids = new SortedSet<string>(hs!.Intersect(ts!), StringComparer.Ordinal);
                ids.UnionWith(hs!.Except(bs));
                ids.UnionWith(ts!.Except(bs));
                entries.Add(new Entry
                {
                    Key = key, Name = merged, NamedHere = namedHere, ThereName = tn, Ids = ids,
                    AddedHere = hs!.Except(bs).Except(ts!).ToHashSet(StringComparer.Ordinal),
                });
            }

            // Created on one side only.
            foreach (var (name, hs) in here.Current)
            {
                if (!hereUsed.Contains(name))
                {
                    entries.Add(new Entry
                    {
                        Key = "here:" + name, Name = name, NamedHere = true,
                        Ids = new SortedSet<string>(hs, StringComparer.Ordinal),
                        AddedHere = new HashSet<string>(hs, StringComparer.Ordinal),
                    });
                }
            }
            foreach (var (name, ts) in there.Original)
            {
                if (!claimed.Contains(name))
                {
                    entries.Add(new Entry { Key = "there:" + name, Name = name, ThereName = name, Ids = new SortedSet<string>(ts, StringComparer.Ordinal) });
                }
            }

            // One name on two collections: one named here, one on the eReader.
            var clashes = entries.Where(e => e.Name != null).GroupBy(e => e.Name!, cmp).Where(g => g.Count() > 1).ToList();
            foreach (var clash in clashes)
            {
                var mine = clash.FirstOrDefault(e => e.NamedHere) ?? clash.First();
                var theirs = clash.First(e => e != mine);
                var key = "same:" + clash.Key.ToUpperInvariant();
                switch (Chosen(key))
                {
                    case Both:
                        var before = new HashSet<string>(theirs.Ids, StringComparer.Ordinal);
                        theirs.Ids.UnionWith(mine.Ids);
                        theirs.AddedHere.UnionWith(mine.Ids.Where(id => !before.Contains(id)));
                        entries.Remove(mine);
                        break;
                    case Here:
                        entries.Remove(theirs);
                        break;
                    case There:
                        entries.Remove(mine);
                        break;
                    default:
                        result.Conflicts.Add(new MergeConflict(key, ConflictKind.SameName, string.Format(Strings.Coll_ConflictSameName, clash.Key),
                            Options((Both, Strings.Coll_MergeBoth), (Here, Strings.Coll_KeepOnlyMine), (There, Strings.Coll_KeepOnlyEreaders))));
                        break;
                }
            }

            // Books put in a collection here that the eReader no longer has at all.
            var titles = here.Books.GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First().Title, StringComparer.Ordinal);
            foreach (var e in entries.Where(e => e.Name != null))
            {
                var gone = e.AddedHere.Where(id => e.Ids.Contains(id) && !there.KnownIds.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToList();
                if (gone.Count == 0)
                {
                    continue;
                }
                var key = "gone:" + e.Name!.ToUpperInvariant();
                switch (Chosen(key))
                {
                    case LeaveOut:
                        e.Ids.ExceptWith(gone);
                        break;
                    case Keep:
                        break;
                    default:
                        var list = string.Join(", ", gone.Select(id => $"\"{titles.GetValueOrDefault(id, id)}\""));
                        result.Conflicts.Add(new MergeConflict(key, ConflictKind.BooksGone, string.Format(Strings.Coll_ConflictBooksGone, e.Name, list),
                            Options((LeaveOut, Strings.Coll_LeaveOut), (Keep, Strings.Coll_KeepInCollection))));
                        break;
                }
            }

            if (result.Conflicts.Count > 0)
            {
                return result;
            }
            foreach (var e in entries)
            {
                result.Collections[e.Name!] = e.Ids;
                if (e.ThereName != null && e.ThereName != e.Name)
                {
                    result.Renamed[e.Name!] = e.ThereName;
                }
            }
            return result;
        }
    }
}
