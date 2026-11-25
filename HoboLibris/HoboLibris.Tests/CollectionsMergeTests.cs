using HoboLibris.Models;
using HoboLibris.Services;

namespace HoboLibris.Tests
{
    /// <summary>The three-way merge of the collections (#32): base, the changes here, the eReader now.</summary>
    public class CollectionsMergeTests
    {
        private static readonly string[] AllBooks = { "a", "b", "c", "d", "e", "f" };

        private static List<CollectionBook> Books(params string[] extra) =>
            AllBooks.Concat(extra).Select(id => new CollectionBook { Id = id, Title = "Title " + id }).ToList();

        /// <summary>Collections as read from an eReader that has every book in AllBooks.</summary>
        private static Collections Read(params (string Name, string? Id, string[] Books)[] shelves) =>
            Collections.FromParts("", null, DateTime.UtcNow, Books("x"), shelves.Select(s => (s.Name, s.Id, (IEnumerable<string>)s.Books)), AllBooks);

        /// <summary>The eReader as it was read: Novels (a, b) and Poetry (c).</summary>
        private static Collections Base() => Read(("Novels", "1", new[] { "a", "b" }), ("Poetry", "2", new[] { "c" }));

        private static Dictionary<string, string[]> Flat(MergeResult r) =>
            r.Collections.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());

        private static MergeResult Merge(Collections here, Collections there, params (string Key, string Value)[] choices) =>
            CollectionsMerge.Merge(here, there, choices.ToDictionary(c => c.Key, c => c.Value));

        [Fact]
        public void ChangesMadeOnOneSideAreKept()
        {
            var here = Base();
            here.Add("Novels", new[] { "d" });
            here.Remove("Novels", new[] { "a" });
            here.Create("Plays");
            here.Add("Plays", new[] { "a" });
            var there = Read(("Novels", "1", new[] { "a", "b" }), ("Poetry", "2", new[] { "c", "e" }), ("History", "3", new[] { "f" }));

            var r = Merge(here, there);
            Assert.Empty(r.Conflicts);
            Assert.Equal(new[] { "b", "d" }, Flat(r)["Novels"]);
            Assert.Equal(new[] { "c", "e" }, Flat(r)["Poetry"]);
            Assert.Equal(new[] { "f" }, Flat(r)["History"]);
            Assert.Equal(new[] { "a" }, Flat(r)["Plays"]);
            Assert.Empty(r.Renamed);
        }

        [Fact]
        public void TheSameChangeOnBothSidesIsKeptOnce()
        {
            var here = Base();
            here.Add("Novels", new[] { "d" });
            here.Remove("Novels", new[] { "b" });
            here.Delete("Poetry");
            var there = Read(("Novels", "1", new[] { "a", "d" }));

            var r = Merge(here, there);
            Assert.Empty(r.Conflicts);
            Assert.Equal(new[] { "Novels" }, r.Collections.Keys);
            Assert.Equal(new[] { "a", "d" }, Flat(r)["Novels"]);
        }

        [Fact]
        public void ADeleteHereOfACollectionTheEreaderDidntChangeGoesThrough()
        {
            var here = Base();
            here.Delete("Novels");
            var r = Merge(here, Base());
            Assert.Empty(r.Conflicts);
            Assert.Equal(new[] { "Poetry" }, r.Collections.Keys);
        }

        [Fact]
        public void ARenameOnOneSideKeepsTheOtherSidesBooks()
        {
            var here = Base();
            here.Add("Novels", new[] { "d" });
            here.Rename("Poetry", "Verse");
            // On the eReader: Novels renamed Fiction (same id), and a book put in Poetry.
            var there = Read(("Fiction", "1", new[] { "a", "b" }), ("Poetry", "2", new[] { "c", "e" }));

            var r = Merge(here, there);
            Assert.Empty(r.Conflicts);
            Assert.Equal(new[] { "Fiction", "Verse" }, r.Collections.Keys);
            Assert.Equal(new[] { "a", "b", "d" }, Flat(r)["Fiction"]);
            Assert.Equal(new[] { "c", "e" }, Flat(r)["Verse"]);
            Assert.Equal("Poetry", r.Renamed["Verse"]);
        }

        [Fact]
        public void RenamedDifferentlyOnEachSideIsAConflict()
        {
            var here = Base();
            here.Rename("Novels", "Fiction");
            var there = Read(("Stories", "1", new[] { "a", "b" }), ("Poetry", "2", new[] { "c" }));

            var r = Merge(here, there);
            var conflict = Assert.Single(r.Conflicts);
            Assert.Equal(ConflictKind.Renamed, conflict.Kind);
            Assert.Equal(new[] { CollectionsMerge.Here, CollectionsMerge.There }, conflict.Options.Select(o => o.Value));

            var there1 = Merge(here, there, (conflict.Key, CollectionsMerge.There));
            Assert.Empty(there1.Conflicts);
            Assert.Equal(new[] { "Poetry", "Stories" }, there1.Collections.Keys);

            var here1 = Merge(here, there, (conflict.Key, CollectionsMerge.Here));
            Assert.Equal(new[] { "Fiction", "Poetry" }, here1.Collections.Keys);
            Assert.Equal("Stories", here1.Renamed["Fiction"]);
        }

        [Fact]
        public void DeletedHereButAddedToOnTheEreaderIsAConflict()
        {
            var here = Base();
            here.Delete("Novels");
            var there = Read(("Novels", "1", new[] { "a", "b", "e" }), ("Poetry", "2", new[] { "c" }));

            var r = Merge(here, there);
            var conflict = Assert.Single(r.Conflicts);
            Assert.Equal(ConflictKind.DeletedHere, conflict.Kind);

            Assert.Equal(new[] { "Poetry" }, Merge(here, there, (conflict.Key, CollectionsMerge.Delete)).Collections.Keys);
            var kept = Merge(here, there, (conflict.Key, CollectionsMerge.Keep));
            Assert.Equal(new[] { "a", "b", "e" }, Flat(kept)["Novels"]);
        }

        [Fact]
        public void AddedToHereButDeletedOnTheEreaderIsAConflict()
        {
            var here = Base();
            here.Add("Poetry", new[] { "d" });
            var there = Read(("Novels", "1", new[] { "a", "b" }));

            var r = Merge(here, there);
            var conflict = Assert.Single(r.Conflicts);
            Assert.Equal(ConflictKind.DeletedThere, conflict.Kind);

            Assert.Equal(new[] { "Novels" }, Merge(here, there, (conflict.Key, CollectionsMerge.Delete)).Collections.Keys);
            var kept = Merge(here, there, (conflict.Key, CollectionsMerge.Keep));
            Assert.Equal(new[] { "c", "d" }, Flat(kept)["Poetry"]);
        }

        /// <summary>A firmware that renames by deleting and creating gives the collection a new id: seen as a delete and a new one.</summary>
        [Fact]
        public void ARenameThatChangesTheIdIsADeleteAndANewCollection()
        {
            var here = Base();
            here.Add("Novels", new[] { "d" });
            var there = Read(("Fiction", "9", new[] { "a", "b" }), ("Poetry", "2", new[] { "c" }));

            var r = Merge(here, there);
            var conflict = Assert.Single(r.Conflicts);
            Assert.Equal(ConflictKind.DeletedThere, conflict.Kind);
            var kept = Merge(here, there, (conflict.Key, CollectionsMerge.Keep));
            Assert.Equal(new[] { "Fiction", "Novels", "Poetry" }, kept.Collections.Keys);
            Assert.Equal(new[] { "a", "b", "d" }, Flat(kept)["Novels"]);
        }

        [Fact]
        public void ACollectionCreatedOnBothSidesWithTheSameNameIsAConflict()
        {
            var here = Base();
            here.Create("Plays");
            here.Add("Plays", new[] { "a" });
            var there = Read(("Novels", "1", new[] { "a", "b" }), ("Poetry", "2", new[] { "c" }), ("plays", "7", new[] { "b" }));

            var r = Merge(here, there);
            var conflict = Assert.Single(r.Conflicts);
            Assert.Equal(ConflictKind.SameName, conflict.Kind);

            var both = Merge(here, there, (conflict.Key, CollectionsMerge.Both));
            Assert.Equal(new[] { "a", "b" }, Flat(both)["plays"]);
            Assert.Equal(3, both.Collections.Count);
            Assert.Equal(new[] { "a" }, Flat(Merge(here, there, (conflict.Key, CollectionsMerge.Here)))["Plays"]);
            Assert.Equal(new[] { "b" }, Flat(Merge(here, there, (conflict.Key, CollectionsMerge.There)))["plays"]);
        }

        [Fact]
        public void ARenameHereToANameCreatedOnTheEreaderIsAConflict()
        {
            var here = Base();
            here.Rename("Poetry", "History");
            var there = Read(("Novels", "1", new[] { "a", "b" }), ("Poetry", "2", new[] { "c" }), ("History", "3", new[] { "f" }));

            var conflict = Assert.Single(Merge(here, there).Conflicts);
            Assert.Equal(ConflictKind.SameName, conflict.Kind);
            var both = Merge(here, there, (conflict.Key, CollectionsMerge.Both));
            Assert.Equal(new[] { "History", "Novels" }, both.Collections.Keys);
            Assert.Equal(new[] { "c", "f" }, Flat(both)["History"]);
        }

        /// <summary>A choice can lead to a new conflict: the merge is run again until none is left.</summary>
        [Fact]
        public void AChoiceCanLeadToAnotherConflict()
        {
            var here = Base();
            here.Rename("Novels", "Fiction");
            var there = Read(("Stories", "1", new[] { "a", "b" }), ("Poetry", "2", new[] { "c" }), ("Fiction", "3", new[] { "f" }));

            var first = Assert.Single(Merge(here, there).Conflicts);
            Assert.Equal(ConflictKind.Renamed, first.Kind);
            var second = Assert.Single(Merge(here, there, (first.Key, CollectionsMerge.Here)).Conflicts);
            Assert.Equal(ConflictKind.SameName, second.Kind);
            var done = Merge(here, there, (first.Key, CollectionsMerge.Here), (second.Key, CollectionsMerge.There));
            Assert.Empty(done.Conflicts);
            Assert.Equal(new[] { "Fiction", "Poetry" }, done.Collections.Keys);
            Assert.Equal(new[] { "f" }, Flat(done)["Fiction"]);
        }

        [Fact]
        public void ABookAddedHereThatIsNoLongerOnTheEreaderIsAConflict()
        {
            var here = Base();
            here.Add("Novels", new[] { "x", "d" });
            var r = Merge(here, Base());
            var conflict = Assert.Single(r.Conflicts);
            Assert.Equal(ConflictKind.BooksGone, conflict.Kind);
            Assert.Contains("Title x", conflict.Text);
            Assert.DoesNotContain("Title d", conflict.Text);

            Assert.Equal(new[] { "a", "b", "d" }, Flat(Merge(here, Base(), (conflict.Key, CollectionsMerge.LeaveOut)))["Novels"]);
            Assert.Equal(new[] { "a", "b", "d", "x" }, Flat(Merge(here, Base(), (conflict.Key, CollectionsMerge.Keep)))["Novels"]);
        }

        /// <summary>Things the Collections screen doesn't list, such as articles (#28), stay as the eReader has them.</summary>
        [Fact]
        public void EntriesTheScreenDoesntListAreKept()
        {
            var here = Base();
            here.Add("Novels", new[] { "d" });
            var there = Read(("Novels", "1", new[] { "a", "b", "article-1" }), ("Poetry", "2", new[] { "c" }));
            var r = Merge(here, there);
            Assert.Empty(r.Conflicts);
            Assert.Equal(new[] { "a", "article-1", "b", "d" }, Flat(r)["Novels"]);
        }
    }
}
