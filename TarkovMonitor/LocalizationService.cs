using System.Globalization;
using System.Resources;
using Microsoft.Extensions.Localization;

namespace TarkovMonitor
{
    public class LocalizationService
    {
        private readonly IStringLocalizerFactory _localizerFactory;
        private readonly IStringLocalizer _localizer;
        private ResourceManager _resourceManager;

        public event EventHandler? LanguageChanged;

        public LocalizationService(IStringLocalizerFactory localizerFactory)
        {
            _localizerFactory = localizerFactory;
            _localizer = _localizerFactory.Create("Strings", "TarkovMonitor");
            _resourceManager = Properties.Strings.ResourceManager;
            
            SetCulture(Properties.Settings.Default.language);
        }

        /// <summary>
        /// Coarse relative time for lists that refresh: no seconds, so the text
        /// changes at most once a minute and keeps a similar width.
        /// </summary>
        public string RelativeTime(DateTime time)
        {
            var local = time.Kind == DateTimeKind.Utc ? time.ToLocalTime() : time;
            var age = DateTime.Now - local;
            if (age < TimeSpan.FromMinutes(1))
            {
                return GetString("JustNow");
            }
            if (age < TimeSpan.FromHours(1))
            {
                return string.Format(GetString("MinutesAgo"), (int)age.TotalMinutes);
            }
            if (local.Date == DateTime.Now.Date)
            {
                return string.Format(GetString("HoursAgo"), (int)age.TotalHours);
            }
            if (local.Date > DateTime.Now.Date.AddDays(-6))
            {
                return local.ToString("ddd HH:mm");
            }
            return local.ToString("d");
        }

        public string GetString(string key)
        {
            try
            {
                var localizedString = _localizer[key];
                if (localizedString.ResourceNotFound)
                {
                    // Fallback to resource manager
                    return _resourceManager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
                }
                return localizedString.Value;
            }
            catch
            {
                return key;
            }
        }

        public void SetCulture(string cultureName)
        {
            try
            {
                var culture = new CultureInfo(cultureName);
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = culture;
                
                Properties.Settings.Default.language = cultureName;
                Properties.Settings.Default.Save();

                LanguageChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (CultureNotFoundException)
            {
                // Fallback to English if culture not found
                SetCulture("en");
            }
        }

        public string CurrentCulture => CultureInfo.CurrentUICulture.Name;

        public List<(string Code, string Name)> GetAvailableLanguages()
        {
            return new List<(string Code, string Name)>
            {
                ("de", GetString("German")),
                ("en", GetString("English")),
                ("es", GetString("Spanish")),
                ("fr", GetString("French")),
                ("pl", GetString("Polish")),
                ("pt", GetString("Portuguese")),
                ("ru", GetString("Russian")),
                ("zh", GetString("Chinese")),
            };
        }
    }
}
