namespace HoboLibris.Models
{
    /// <summary>A book on the Kobo eReader, as the Collections screen sees it.</summary>
    public class CollectionBook
    {
        /// <summary>Kobo's ContentID: a store id, or "file:///mnt/onboard/..." for books added by the person.</summary>
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Authors { get; set; } = "";
        /// <summary>Bought in the Kobo store (not added by the person).</summary>
        public bool Store { get; set; }
    }

    /// <summary>What saving the collections would write.</summary>
    public class CollectionChanges
    {
        public List<string> Created { get; } = new();
        public List<string> Deleted { get; } = new();
        public List<(string Shelf, string Id)> Added { get; } = new();
        public List<(string Shelf, string Id)> Removed { get; } = new();

        public int Count => Created.Count + Deleted.Count + Added.Count + Removed.Count;
    }
}
