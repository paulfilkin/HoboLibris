namespace HoboLibris.Tests
{
    /// <summary>A fresh, empty folder under the temp folder for one test.</summary>
    public static class TestFolder
    {
        public static string Fresh(string name)
        {
            var dir = Path.Combine(Path.GetTempPath(), "HoboLibrisTests", name);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
