namespace HoboLibris.Services
{
    /// <summary>
    /// Something the person needs to know about, in their language. The message is shown as it is.
    /// </summary>
    public class HoboLibrisException : Exception
    {
        public HoboLibrisException(string message) : base(message)
        {
        }

        public HoboLibrisException(string message, Exception inner) : base(message, inner)
        {
        }
    }
}
