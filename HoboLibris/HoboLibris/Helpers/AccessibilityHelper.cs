using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;

namespace HoboLibris.Helpers
{
    /// <summary>Announcements for screen readers.</summary>
    public static class AccessibilityHelper
    {
        /// <summary>Announce a message to screen readers through the element's live region.</summary>
        public static void Announce(string message, FrameworkElement? element)
        {
            if (element == null || string.IsNullOrEmpty(message))
            {
                return;
            }
            try
            {
                AutomationProperties.SetLiveSetting(element, AutomationLiveSetting.Polite);
                element.SetValue(AutomationProperties.NameProperty, message);
                if (AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
                {
                    var peer = UIElementAutomationPeer.FromElement(element);
                    peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
                }
            }
            catch (Exception ex)
            {
                Services.LogManager.Debug($"Accessibility announcement failed: {ex.Message}");
            }
        }

        public static void SetupLiveRegion(FrameworkElement element, AutomationLiveSetting setting = AutomationLiveSetting.Polite)
        {
            try
            {
                AutomationProperties.SetLiveSetting(element, setting);
            }
            catch (Exception ex)
            {
                Services.LogManager.Debug($"Failed to set up live region: {ex.Message}");
            }
        }
    }
}
