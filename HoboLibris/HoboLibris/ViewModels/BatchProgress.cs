using CommunityToolkit.Mvvm.ComponentModel;
using HoboLibris.Resources;

namespace HoboLibris.ViewModels
{
    /// <summary>
    /// Overall progress of a batch of downloads or copies: finished books plus the share of the
    /// book in progress.
    /// </summary>
    public partial class BatchProgress : ObservableObject
    {
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Fraction), nameof(Text), nameof(IsActive))]
        private int _done;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Fraction), nameof(Text), nameof(IsActive))]
        private int _total;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Fraction), nameof(Text))]
        private double _partial;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsActive))]
        private bool _running;

        private readonly string _verb;

        public BatchProgress(string verb)
        {
            _verb = verb;
        }

        public bool IsActive => Running && Total > 0;

        public double Fraction => Total == 0 ? 0 : Math.Clamp((Done + Partial) / Total, 0, 1);

        public string Text => Done >= Total
            ? string.Format(Strings.Progress_AllFinished, _verb, Total)
            : string.Format(Strings.Progress_BookOf, _verb, Done + 1, Total, Math.Round(Fraction * 100));

        public void Start(int total)
        {
            Done = 0;
            Total = total;
            Partial = 0;
            Running = true;
        }

        public void Finish()
        {
            Running = false;
            Partial = 0;
        }
    }
}
