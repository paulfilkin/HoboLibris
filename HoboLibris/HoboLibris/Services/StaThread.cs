namespace HoboLibris.Services
{
    /// <summary>Runs shell COM work on a single-threaded apartment thread and waits for it.</summary>
    public static class StaThread
    {
        public static T Run<T>(Func<T> work)
        {
            T result = default!;
            Exception? error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    result = work();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            })
            {
                IsBackground = true,
                Name = "HoboLibris shell",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (error != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            }
            return result;
        }

        public static void Run(Action work) => Run(() =>
        {
            work();
            return true;
        });
    }
}
