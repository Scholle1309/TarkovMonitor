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
                return local.ToString("ddd") + " " + local.ToString("t");
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
                // Texts follow the language; dates and times may use the formats
                // chosen in the settings instead of the language's own.
                var formatting = WithDateTimeFormats(culture);
                CultureInfo.CurrentCulture = formatting;
                CultureInfo.CurrentUICulture = culture;
                // Blazor renders and background callbacks do not all run on the
                // thread that set the language; without a process-wide default
                // they fall back to the Windows language.
                CultureInfo.DefaultThreadCurrentCulture = formatting;
                CultureInfo.DefaultThreadCurrentUICulture = culture;
                
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

        /// <summary>Re-applies the language with the current date and time format settings.</summary>
        public void ApplyDateTimeFormats() => SetCulture(Properties.Settings.Default.language);

        // dateFormat: "" (language), "dmy" 31.12.2026, "mdy" 12/31/2026, "ymd" 2026-12-31.
        // timeFormat: "" (language), "24" 13:45, "12" 1:45 PM.
        private static CultureInfo WithDateTimeFormats(CultureInfo culture)
        {
            var result = (CultureInfo)culture.Clone();
            var format = result.DateTimeFormat;
            switch (Properties.Settings.Default.dateFormat)
            {
                case "dmy":
                    format.DateSeparator = ".";
                    format.ShortDatePattern = "dd/MM/yyyy";
                    break;
                case "mdy":
                    format.DateSeparator = "/";
                    format.ShortDatePattern = "MM/dd/yyyy";
                    break;
                case "ymd":
                    format.DateSeparator = "-";
                    format.ShortDatePattern = "yyyy/MM/dd";
                    break;
            }
            switch (Properties.Settings.Default.timeFormat)
            {
                case "24":
                    format.ShortTimePattern = "HH:mm";
                    format.LongTimePattern = "HH:mm:ss";
                    break;
                case "12":
                    format.ShortTimePattern = "h:mm tt";
                    format.LongTimePattern = "h:mm:ss tt";
                    if (string.IsNullOrEmpty(format.AMDesignator))
                    {
                        format.AMDesignator = "AM";
                        format.PMDesignator = "PM";
                    }
                    break;
            }
            return result;
        }

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
