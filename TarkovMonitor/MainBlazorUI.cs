using MudBlazor.Services;
using Microsoft.AspNetCore.Components.WebView.WindowsForms;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using TarkovMonitor.GroupLoadout;
using System.Globalization;
using System.ComponentModel;
using MudBlazor;
using Microsoft.Extensions.Localization;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using System.Net;
using System.Text;

namespace TarkovMonitor
{
    public partial class MainBlazorUI : Form
    {
        private const int WmNcHitTest = 0x0084;
        private const int WmNcCalcSize = 0x0083;
        private const int WmNcPaint = 0x0085;
        private const int WmNcActivate = 0x0086;
        private const int WmNcLButtonDown = 0x00A1;
        private const int HtCaption = 0x0002;
        private const int HtClient = 0x0001;
        private const int ResizeBorderWidth = 4;
        private const int MinimumWindowWidth = 450;
        private const int MinimumWindowHeight = 250;
        private const int WsThickFrame = 0x00040000;
        private const int WsMinimizeBox = 0x00020000;
        private const int WsMaximizeBox = 0x00010000;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpFrameChanged = 0x0020;
        private const int DwmWindowCornerPreference = 33;
        private const int DwmBorderColor = 34;
        private const int DwmCaptionColor = 35;
        private const int DwmRound = 2;
        private const int DwmColorNone = unchecked((int)0xFFFFFFFE);
        private const int TarkovBorderColor = 0x003B555F;
        private const int TarkovHeaderColor = 0x002D2F2F;

        public event EventHandler? WindowStateChanged;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr windowHandle, int attribute, ref int value, int valueSize);

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.Style |= WsThickFrame | WsMinimizeBox | WsMaximizeBox;
                return parameters;
            }
        }

        private readonly GameWatcher eft;
        private readonly DiagnosticsService diagnostics;
        private readonly MessageLog messageLog;
        private readonly LogRepository logRepository;
        private readonly GroupManager groupManager;
        private readonly TimersManager timersManager;
        private readonly MapsService mapsService;
        private readonly QuestLogStore questLogStore;
        private const string TrackerApiHostPrefix = "api.tarkovtracker.";
        private const string MapFrameHost = "tarkov.dev";
        // Injected into the embedded Tarkov.dev document.
        // Style: the site header and the cookie banner only take space inside the
        // Maps tab. The map page sizes itself from the header height, so hiding
        // it lets the map fill the frame.
        // Script: Tarkov.dev clamps the view to 1.5x the map size. When the whole
        // map fits the window that locks the map in place and every drag snaps
        // back to the centre. Leaflet publishes itself as window.L, so its
        // setMaxBounds is widened before the map is created.
        private const string MapFrameInjection =
            "<style id=\"tarkov-monitor-frame\">"
            + "nav.navigation, .CookieConsent { display: none !important; }"
            // Quest markers on another floor or underground are faded to 20% by the site,
            // which makes them nearly invisible. Keep them visible and tint them light blue.
            // Everything on another level is faded to 20% by the site; 70% keeps the
            // other floors readable.
            + "html body .leaflet-layer.off-level > .leaflet-tile-container,"
            + " html body div.leaflet-pane.leaflet-overlay-pane > img.off-level,"
            + " html body div.leaflet-pane.leaflet-overlay-pane > svg.off-level g.base-layer,"
            + " html body div.leaflet-pane.leaflet-marker-pane > .off-level { opacity: 0.7; }"
            + "html body div.leaflet-overlay-pane > svg.leaflet-zoom-animated > g > path.off-level { stroke-opacity: 0.7; fill-opacity: 0.07; }"
            + "html body div.leaflet-pane.leaflet-marker-pane > .off-level.active-quest-marker {"
            + " opacity: 0.95 !important; z-index: 600 !important;"
            + " filter: sepia(1) saturate(5) hue-rotate(165deg) brightness(1.15) drop-shadow(0 0 2px #4fc3f7); }"
            // Markers found by the site's search carry the class "pulse"; make them blink
            // without touching transform (Leaflet positions markers with it).
            + "@keyframes tarkov-monitor-pulse { 0%, 100% { box-shadow: 0 0 0 2px rgba(255, 235, 59, 0.95), 0 0 6px 4px rgba(255, 235, 59, 0.5); }"
            + " 50% { box-shadow: 0 0 0 8px rgba(255, 235, 59, 0.15), 0 0 18px 10px rgba(255, 235, 59, 0.6); } }"
            + "@keyframes tarkov-monitor-position-pulse { 0%, 100% { box-shadow: 0 0 0 2px rgba(76, 175, 80, 0.95), 0 0 6px 4px rgba(76, 175, 80, 0.5); }"
            + " 50% { box-shadow: 0 0 0 10px rgba(76, 175, 80, 0.15), 0 0 22px 12px rgba(76, 175, 80, 0.65); } }"
            + "html body div.leaflet-pane.leaflet-marker-pane > .tarkov-monitor-position-pulse { animation: tarkov-monitor-position-pulse 0.8s ease-in-out infinite;"
            + " border-radius: 50%; z-index: 1000 !important; }"
            + "html body .leaflet-left .leaflet-control-raid-info { text-align: left; }"
            + "html body .leaflet-control-coordinates { display: none !important; }"
            + "html body .leaflet-control-fullscreen { display: none !important; }"
            // One visual grid for the map controls: 44px squares, 8px gaps, same glass
            // background and corner radius; the raid info box matches the id box width.
            + "html body .leaflet-control-container .leaflet-control, html body .id-wrapper { background: rgba(30, 30, 30, 0.95) !important; border: 1px solid rgba(255, 255, 255, 0.18) !important; border-radius: 8px !important; box-shadow: 0 2px 8px rgba(0, 0, 0, 0.5) !important; }"
            // Top-left: zoom and layers side by side; the layers panel drops down below its toggle.
            + "html body .leaflet-top.leaflet-left { display: flex !important; align-items: flex-start; }"
            + "html body .leaflet-top.leaflet-left .leaflet-control { float: none !important; clear: none !important; margin: 8px 0 0 8px !important; }"
            + "html body .leaflet-top.leaflet-left { z-index: 1010; }"
            + "html body .leaflet-bottom.leaflet-left { margin-bottom: 79px !important; }"
            + "html body .leaflet-bottom.leaflet-left .leaflet-control { margin: 0 0 8px 8px !important; }"
            + "html body .leaflet-touch .leaflet-bar a, html body .leaflet-bar a { width: 42px !important; height: 42px !important; line-height: 42px !important; background: transparent !important; color: rgba(255, 255, 255, 0.85) !important; border-bottom-color: rgba(255, 255, 255, 0.12) !important; }"
            + "html body .leaflet-control-zoom a:first-child { border-top-left-radius: 7px; border-top-right-radius: 7px; }"
            + "html body .leaflet-control-zoom a:last-child { border-bottom-left-radius: 7px; border-bottom-right-radius: 7px; }"
            // Layers, settings and search: 44px buttons in the bottom-right dock (same look as the
            // Maps tab buttons next to them); their panels open upwards, right-aligned.
            + "html body .leaflet-control-layers, html body .leaflet-control-map-settings, html body .maps-search-wrapper { width: 44px !important; height: 44px !important; padding: 0 !important; margin: 0 0 0 8px !important; box-sizing: border-box !important; overflow: visible !important; float: none !important; clear: none !important; background: rgba(30, 30, 30, 0.95) !important; border: 1px solid rgba(255, 255, 255, 0.18) !important; border-radius: 8px !important; box-shadow: 0 2px 8px rgba(0, 0, 0, 0.5) !important; }"
            + "html body .leaflet-control-layers:hover, html body .leaflet-control-map-settings:hover, html body .maps-search-wrapper:hover { background: rgba(60, 60, 60, 0.95) !important; }"
            + "html body .leaflet-control-layers-expanded, html body .leaflet-control-map-settings-expanded, html body .leaflet-control-icon-search-expanded { border-color: rgba(33, 150, 243, 0.8) !important; }"
            + "html body .leaflet-control-layers-toggle, html body .leaflet-control-map-settings-toggle, html body .leaflet-control-icon-search-toggle { display: block !important; width: 100% !important; height: 100% !important; box-sizing: border-box; border-radius: 7px; }"
            + "html body .leaflet-control-layers-toggle, html body .leaflet-control-map-settings-toggle, html body .leaflet-control-icon-search-toggle { background-color: transparent !important; border: 0 !important; box-shadow: none !important; opacity: 0.85; }"
            + "html body .leaflet-control-layers-expanded .leaflet-control-layers-toggle, html body .leaflet-control-map-settings-expanded .leaflet-control-map-settings-toggle, html body .leaflet-control-icon-search-expanded .leaflet-control-icon-search-toggle { opacity: 1; }"
            + "html body .leaflet-control-layers-expanded .leaflet-control-layers-list, html body .leaflet-control-map-settings-expanded .leaflet-control-map-settings-list, html body .leaflet-control-icon-search-expanded .leaflet-control-icon-search-list { display: block; position: absolute !important; right: -1px; left: auto !important; bottom: 51px; top: auto !important; box-sizing: border-box; width: max-content; min-width: 280px; max-width: min(420px, calc(100vw - 32px)); height: auto !important; max-height: calc(100vh - 140px); overflow-y: auto; margin: 0 !important; padding: 10px 12px !important; background: rgba(30, 30, 30, 0.95); border: 1px solid rgba(255, 255, 255, 0.12); border-radius: 6px; box-shadow: 0 4px 16px rgba(0, 0, 0, 0.5); }"
            // Panel content in the look of the Maps tab task panel: same font, colours, row blocks and controls.
            + "html body .leaflet-control-layers-list, html body .leaflet-control-map-settings-list, html body .leaflet-control-icon-search-list { width: 340px !important; min-width: 0 !important; padding: 6px 10px !important; font-family: \"Segoe UI\", Roboto, sans-serif !important; font-size: 0.8rem !important; line-height: 1.35 !important; color: rgba(255, 255, 255, 0.85) !important; }"
            + "html body .leaflet-control-layers-list *, html body .leaflet-control-map-settings-list *, html body .leaflet-control-icon-search-list * { font-family: inherit !important; }"
            + "html body .leaflet-control-layers-list label, html body .leaflet-control-map-settings-list label, html body .leaflet-control-icon-search-list label { display: flex !important; align-items: center !important; gap: 8px !important; margin: 0 0 3px !important; padding: 5px 8px !important; border-radius: 4px !important; background: rgba(255, 255, 255, 0.05) !important; border: 1px solid transparent !important; font-size: 0.8rem !important; font-weight: 400 !important; color: rgba(255, 255, 255, 0.85) !important; text-transform: none !important; cursor: pointer; }"
            + "html body .leaflet-control-layers-list label:hover, html body .leaflet-control-map-settings-list label:hover, html body .leaflet-control-icon-search-list label:hover { background: rgba(255, 255, 255, 0.1) !important; }"
            + "html body .leaflet-control-layers-list label span, html body .leaflet-control-map-settings-list label span, html body .leaflet-control-icon-search-list label span { color: inherit !important; font-size: inherit !important; }"
            // Check boxes and radios drawn like the app's: rounded, faint border, blue with a white tick when checked.
            + "html body .leaflet-control-layers-list input[type=checkbox], html body .leaflet-control-map-settings-list input[type=checkbox], html body .leaflet-control-icon-search-list input[type=checkbox], html body .leaflet-control-layers-list input[type=radio], html body .leaflet-control-map-settings-list input[type=radio], html body .leaflet-control-icon-search-list input[type=radio] { appearance: none !important; -webkit-appearance: none !important; width: 16px !important; height: 16px !important; margin: 0 !important; flex: none; box-sizing: border-box; border: 1.5px solid rgba(255, 255, 255, 0.45) !important; border-radius: 4px !important; background: rgba(255, 255, 255, 0.04) !important; box-shadow: none !important; cursor: pointer; transition: background 0.1s, border-color 0.1s; }"
            + "html body .leaflet-control-layers-list input[type=radio], html body .leaflet-control-map-settings-list input[type=radio], html body .leaflet-control-icon-search-list input[type=radio] { border-radius: 50% !important; }"
            + "html body .leaflet-control-layers-list input[type=checkbox]:hover, html body .leaflet-control-map-settings-list input[type=checkbox]:hover, html body .leaflet-control-icon-search-list input[type=checkbox]:hover, html body .leaflet-control-layers-list input[type=radio]:hover, html body .leaflet-control-map-settings-list input[type=radio]:hover, html body .leaflet-control-icon-search-list input[type=radio]:hover { border-color: rgba(255, 255, 255, 0.8) !important; }"
            + "html body .leaflet-control-layers-list input[type=checkbox]:checked, html body .leaflet-control-map-settings-list input[type=checkbox]:checked, html body .leaflet-control-icon-search-list input[type=checkbox]:checked { background: #2196f3 url(\"data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Cpath d='M3.5 8.5l3 3 6-7' fill='none' stroke='white' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'/%3E%3C/svg%3E\") center / 14px 14px no-repeat !important; border-color: #2196f3 !important; }"
            + "html body .leaflet-control-layers-list input[type=radio]:checked, html body .leaflet-control-map-settings-list input[type=radio]:checked, html body .leaflet-control-icon-search-list input[type=radio]:checked { border-color: #2196f3 !important; background-color: #2196f3 !important; background-image: radial-gradient(circle at center, #fff 0 3px, transparent 3.5px) !important; background-size: 100% 100% !important; background-position: center !important; background-repeat: no-repeat !important; }"
            + "html body .leaflet-control-layers-list input[type=checkbox]::before, html body .leaflet-control-map-settings-list input[type=checkbox]::before, html body .leaflet-control-icon-search-list input[type=checkbox]::before, html body .leaflet-control-layers-list input[type=checkbox]::after, html body .leaflet-control-map-settings-list input[type=checkbox]::after, html body .leaflet-control-icon-search-list input[type=checkbox]::after, html body .leaflet-control-layers-list input[type=radio]::before, html body .leaflet-control-map-settings-list input[type=radio]::before, html body .leaflet-control-icon-search-list input[type=radio]::before, html body .leaflet-control-layers-list input[type=radio]::after, html body .leaflet-control-map-settings-list input[type=radio]::after, html body .leaflet-control-icon-search-list input[type=radio]::after { content: none !important; display: none !important; }"
            + "html body .leaflet-control-layers-list input[type=checkbox]:indeterminate, html body .leaflet-control-map-settings-list input[type=checkbox]:indeterminate, html body .leaflet-control-icon-search-list input[type=checkbox]:indeterminate { background: rgba(33, 150, 243, 0.5) !important; border-color: #2196f3 !important; }"
            + "html body .leaflet-control-layers-list .leaflet-control-layers-group-label { margin: 6px 0 3px !important; padding: 6px 4px 4px !important; background: transparent !important; border: 0 !important; border-top: 1px solid rgba(255, 255, 255, 0.12) !important; border-radius: 0 !important; font-size: 0.72rem !important; text-transform: uppercase !important; letter-spacing: 0.04em !important; color: rgba(255, 255, 255, 0.55) !important; }"
            + "html body .leaflet-control-layers-list .leaflet-control-layers-group-label span { color: inherit !important; font-size: inherit !important; }"
            + "html body .leaflet-control-layers-list .leaflet-control-layers-base + .leaflet-control-layers-separator, html body .leaflet-control-layers-list .leaflet-control-layers-separator { border-top: 1px solid rgba(255, 255, 255, 0.12) !important; margin: 4px 0 !important; }"
            + "html body .leaflet-control-layers-list .leaflet-control-layers-group:first-child .leaflet-control-layers-group-label { border-top: 0 !important; margin-top: 0 !important; }"
            + "html body .leaflet-control-layers-list img.control-item-image { width: 18px; height: 18px; vertical-align: middle; margin-right: 6px; }"
            + "html body .leaflet-control-map-settings-list .leaflet-control-map-settings-setting-container { display: block !important; width: 100% !important; }"
            + "html body .leaflet-control-layers-list label, html body .leaflet-control-map-settings-list label, html body .leaflet-control-icon-search-list label { width: 100% !important; box-sizing: border-box !important; }"
            + "html body .leaflet-control-map-settings-list .leaflet-control-map-settings-separator { border-top: 1px solid rgba(255, 255, 255, 0.12) !important; margin: 4px 0 !important; }"
            + "html body .leaflet-control-map-settings-list .leaflet-control-map-settings-player-location-help { font-size: 0.72rem !important; color: rgba(255, 255, 255, 0.45) !important; padding: 4px 2px !important; }"
            + "html body .leaflet-control-map-settings-list a { color: #64b5f6 !important; }"
            + "html body .leaflet-control-icon-search-list input[type=text] { box-sizing: border-box; width: 100% !important; margin: 0 0 6px !important; padding: 7px 30px 7px 10px !important; border-radius: 6px !important; border: 1px solid rgba(255, 255, 255, 0.15) !important; background: rgba(255, 255, 255, 0.06) !important; color: rgba(255, 255, 255, 0.9) !important; font-size: 0.8rem !important; outline: none !important; }"
            + "html body .leaflet-control-icon-search-list input[type=text]:focus { border-color: rgba(33, 150, 243, 0.8) !important; }"
            + "html body .leaflet-control-icon-search-list input[type=text]::placeholder { color: rgba(255, 255, 255, 0.4) !important; }"
            + "html body .leaflet-control-icon-search-list .maps-search-wrapper-info { font-size: 0.72rem !important; color: rgba(255, 255, 255, 0.45) !important; margin: 0 2px 8px !important; }"
            + "html body .leaflet-control-icon-search-list .maps-search-wrapper-info b { font-weight: 400 !important; }"
            + "html body .leaflet-control-icon-search-list .maps-search-wrapper-task-filter-title { display: flex !important; align-items: center !important; justify-content: space-between !important; margin: 2px 0 6px !important; padding-top: 6px !important; border-top: 1px solid rgba(255, 255, 255, 0.12) !important; font-size: 0.72rem !important; text-transform: uppercase !important; letter-spacing: 0.04em !important; color: rgba(255, 255, 255, 0.55) !important; }"
            + "html body .leaflet-control-icon-search-list .maps-search-wrapper-task-filter-title span { font-size: inherit !important; color: inherit !important; }"
            + "html body .leaflet-control-icon-search-list .maps-search-task-filter-button-container button { margin-left: 4px !important; padding: 1px 8px !important; border-radius: 10px !important; border: 1px solid rgba(255, 255, 255, 0.25) !important; background: transparent !important; color: rgba(255, 255, 255, 0.7) !important; font-size: 0.7rem !important; text-transform: none !important; letter-spacing: 0 !important; cursor: pointer; }"
            + "html body .leaflet-control-icon-search-list .maps-search-task-filter-button-container button:hover { border-color: #64b5f6 !important; color: #64b5f6 !important; }"
            + "html body .leaflet-control-icon-search-list .maps-search-wrapper-reset-search, html body .leaflet-control-icon-search-list .maps-search-wrapper-reset-task-filter { position: absolute !important; right: 20px !important; width: 20px !important; height: 20px !important; padding: 0 !important; border: 0 !important; border-radius: 50% !important; background: rgba(255, 255, 255, 0.12) !important; color: rgba(255, 255, 255, 0.7) !important; font-size: 0.7rem !important; line-height: 20px !important; text-align: center !important; cursor: pointer; }"
            + "html body .leaflet-control-icon-search-list .maps-search-task-list { max-height: 50vh; overflow-y: auto; }"
            + "html body .leaflet-control-map-settings-toggle, html body .leaflet-control-icon-search-toggle, html body .leaflet-control-layers-toggle { background-size: 24px 24px !important; background-position: center center !important; background-repeat: no-repeat !important; margin: 0 !important; }"
            + "html body .leaflet-control-icon-search-expanded .leaflet-control-icon-search-list { display: flex !important; flex-direction: column; height: auto !important; width: 340px; }"
            + "html body .id-wrapper { left: 8px !important; bottom: 8px !important; }"
            // Raid info: two-column card (times on top, then duration / players / author)
            + "html body .leaflet-control-raid-info { left: 0 !important; box-sizing: border-box; display: grid !important; grid-template-columns: 1fr 1fr; gap: 2px 12px; padding: 8px 12px !important; white-space: nowrap; font-size: 13px; line-height: 1.3; color: rgba(255, 255, 255, 0.85); }"
            + "html body .leaflet-control-raid-info > div:nth-child(-n+2) { font-size: 17px; font-weight: 600; letter-spacing: 0.02em; margin-bottom: 2px; }"
            + "html body .leaflet-control-raid-info > div:nth-child(5) { grid-column: 1 / -1; }"
            + "html body .leaflet-control-raid-info .tm-label { color: rgba(255, 255, 255, 0.5); margin-right: 6px; }"
            + "html body .leaflet-control-raid-info a { color: inherit; text-decoration: none; border-bottom: 1px dotted rgba(255, 255, 255, 0.35); }"
            // Remote id: 44px card with a small caption, no side-switch button
            + "html body .id-wrapper { height: 44px !important; min-width: 150px; padding: 6px 12px 4px !important; box-sizing: border-box; display: flex !important; flex-direction: column; justify-content: space-between; gap: 0; }"
            + "html body .id-wrapper .update-label { position: static !important; margin: 0 !important; padding: 0 !important; height: auto !important; display: flex; align-items: center; gap: 6px; font-size: 10px; line-height: 1; text-transform: uppercase; letter-spacing: 0.08em; color: rgba(255, 255, 255, 0.5); }"
            + "html body .id-wrapper .session-switch-side { display: none !important; }"
            + "html body .id-wrapper .session-question { display: none !important; }"
            + "html body .id-wrapper .session-id-container { position: static !important; margin: 0 !important; padding: 0 !important; height: auto !important; }"
            + "html body .id-wrapper .session-id-container, html body .id-wrapper .session-id { font-size: 15px; font-weight: 600; line-height: 1; }"
            // The dock sits left of the Maps tab buttons (tasks at right 12px, bosses at 64px).
            + "html body .leaflet-bottom.leaflet-right.tarkov-monitor-dock { position: absolute; right: 116px !important; bottom: 12px !important; margin: 0 !important; z-index: 1000; pointer-events: none; display: flex; align-items: flex-end; }"
            // Same colours as the Maps tab buttons; more specific than the general glass rule for Leaflet controls.
            + "html body .leaflet-control-container .tarkov-monitor-dock .leaflet-control { pointer-events: auto; background: rgba(30, 30, 30, 0.95) !important; border: 1px solid rgba(255, 255, 255, 0.18) !important; border-radius: 8px !important; box-shadow: 0 2px 8px rgba(0, 0, 0, 0.5) !important; }"
            + "html body .leaflet-control-container .tarkov-monitor-dock .leaflet-control:hover { background: rgba(60, 60, 60, 0.95) !important; }"
            + "html body .leaflet-control-container .tarkov-monitor-dock .leaflet-control-layers-expanded, html body .leaflet-control-container .tarkov-monitor-dock .leaflet-control-map-settings-expanded, html body .leaflet-control-container .tarkov-monitor-dock .leaflet-control-icon-search-expanded { border-color: rgba(33, 150, 243, 0.8) !important; }"
            + "html body div.leaflet-pane.leaflet-marker-pane > .pulse { animation: tarkov-monitor-pulse 1s ease-in-out infinite;"
            + " border-radius: 50%; z-index: 900 !important; opacity: 1 !important; }"
            + "</style>"
            // The Maps tab talks to the page with postMessage: a search request runs the
            // site's own marker search (task markers pulse, the rest are hidden).
            + "<script id=\"tarkov-monitor-frame-bridge\">(function () {"
            + " var pulseTimer = null, pulseUntil = 0;"
            + " function positionMarker() { var img = document.querySelector('.leaflet-marker-pane img[src*=\"player-position\"]'); return img && img.closest('.leaflet-marker-icon'); }"
            // The site re-creates the marker when the position arrives, so the class is
            // re-applied while the pulse is running and removed afterwards.
            + " var pendingSearch = '', pendingUntil = 0, lastBar = null;"
            + " function applySearch(text) {"
            + "  pendingSearch = text; pendingUntil = Date.now() + 15000; lastBar = null; pushSearch();"
            + " }"
            + " function pushSearch() {"
            + "  var bar = document.querySelector('input.maps-search-wrapper-search-bar');"
            + "  if (!bar || bar === lastBar) { return; }"
            + "  lastBar = bar; bar.value = pendingSearch; bar.dispatchEvent(new Event('input', { bubbles: true }));"
            + " }"
            // The raid info goes to the bottom-left column above the remote id box; layers,
            // settings and search go to a dock at the bottom right, next to the Maps tab buttons.
            + " var panelSpecs = [['.leaflet-control-layers', 'leaflet-control-layers-expanded'], ['.leaflet-control-map-settings', 'leaflet-control-map-settings-expanded'], ['.maps-search-wrapper', 'leaflet-control-icon-search-expanded']];"
            + " function collapsePanels(except) {"
            + "  panelSpecs.forEach(function (spec) { var control = document.querySelector(spec[0]); if (control && control !== except) { control.classList.remove(spec[1]); } });"
            + " }"
            + " function relocateControls() {"
            + "  var container = document.querySelector('.leaflet-control-container');"
            + "  var column = document.querySelector('.leaflet-bottom.leaflet-left');"
            + "  if (!container || !column) { return; }"
            + "  var dock = container.querySelector('.tarkov-monitor-dock');"
            + "  if (!dock) { dock = document.createElement('div'); dock.className = 'leaflet-bottom leaflet-right tarkov-monitor-dock'; container.appendChild(dock); }"
            + "  var info = document.querySelector('.leaflet-control-raid-info');"
            + "  if (info && info.parentElement !== column) { column.appendChild(info); }"
            + "  panelSpecs.forEach(function (spec) {"
            + "   var control = document.querySelector(spec[0]);"
            + "   if (control && control.parentElement !== dock) { dock.appendChild(control); }"
            + "  });"
            + "  [['input.maps-search-wrapper-search-bar', '.maps-search-wrapper-reset-search'], ['input.maps-search-task-filter', '.maps-search-wrapper-reset-task-filter']].forEach(function (pair) {"
            + "   var input = document.querySelector(pair[0]), reset = document.querySelector(pair[1]);"
            + "   if (!input || !reset) { return; }"
            + "   var top = Math.round(input.offsetTop + (input.offsetHeight - 20) / 2) + 'px';"
            + "   if (reset.style.top !== top) { reset.style.top = top; }"
            + "  });"
            + "  var idBox = document.querySelector('.id-wrapper');"
            + "  if (!idBox) { return; }"
            + "  var rect = idBox.getBoundingClientRect();"
            + "  var gap = Math.round(rect.height + 8) + 'px';"
            + "  var caption = idBox.querySelector('.update-label');"
            + "  if (caption && caption.firstChild && caption.firstChild.nodeType === 3 && caption.firstChild.nodeValue !== 'Remote ID') { caption.firstChild.nodeValue = 'Remote ID'; }"
            // The site re-renders the box now and then; label rows that lost their label.
            + "  if (info) {"
            + "   var rows = info.querySelectorAll(':scope > div');"
            + "   for (var i = 2; i < rows.length; i++) {"
            + "    if (rows[i].querySelector('.tm-label')) { continue; }"
            + "    var html = rows[i].innerHTML, at = html.indexOf(': ');"
            + "    if (at > 0) { rows[i].innerHTML = '<span class=\"tm-label\">' + html.slice(0, at) + '</span>' + html.slice(at + 2); }"
            + "   }"
            + "  }"
            + "  if (column.style.getPropertyValue('margin-bottom') !== gap) { column.style.setProperty('margin-bottom', gap, 'important'); }"
            // The remote id box is as wide as the raid info card above it.
            + "  if (info) { var infoWidth = Math.round(info.getBoundingClientRect().width) + 'px'; if (infoWidth !== '0px' && idBox.style.width !== infoWidth) { idBox.style.width = infoWidth; } }"
            + " }"
            // Panels open on click only: the hover handlers of the site are blocked in the
            // capture phase, a click on an open toggle closes the panel again, and only one
            // panel is open at a time (the Maps tab is told so it closes its own panels).
            + " function clickOnly(containerSelector, toggleSelector, expandedClass) {"
            + "  var container = document.querySelector(containerSelector);"
            + "  if (!container || container.dataset.tmClickOnly) { return; }"
            + "  container.dataset.tmClickOnly = '1';"
            + "  ['mouseover', 'mouseout', 'mouseenter', 'mouseleave'].forEach(function (type) { container.addEventListener(type, function (event) { event.stopImmediatePropagation(); }, true); });"
            + "  var toggle = container.querySelector(toggleSelector);"
            + "  if (!toggle) { return; }"
            + "  var wasExpanded = false;"
            + "  toggle.addEventListener('click', function () { wasExpanded = container.classList.contains(expandedClass); }, true);"
            + "  toggle.addEventListener('click', function () {"
            + "   if (wasExpanded) { container.classList.remove(expandedClass); return; }"
            + "   setTimeout(function () {"
            + "    if (!container.classList.contains(expandedClass)) { return; }"
            + "    collapsePanels(container);"
            + "    window.parent.postMessage({ type: 'tarkov-monitor-panel-opened' }, '*');"
            + "   }, 0);"
            + "  });"
            + " }"
            + " function applyClickOnly() {"
            + "  clickOnly('.leaflet-control-layers', '.leaflet-control-layers-toggle', 'leaflet-control-layers-expanded');"
            + "  clickOnly('.maps-search-wrapper', '.leaflet-control-icon-search-toggle', 'leaflet-control-icon-search-expanded');"
            + "  clickOnly('.leaflet-control-map-settings', '.leaflet-control-map-settings-toggle', 'leaflet-control-map-settings-expanded');"
            + " }"
            + " var relocateTimer = null;"
            + " new MutationObserver(function () {"
            + "  if (Date.now() < pendingUntil) { pushSearch(); }"
            + "  if (!relocateTimer) { relocateTimer = setTimeout(function () { relocateTimer = null; relocateControls(); applyClickOnly(); }, 100); }"
            + " }).observe(document.documentElement, { childList: true, subtree: true });"
            + " function pulsePosition(duration) {"
            + "  pulseUntil = Date.now() + duration;"
            + "  if (pulseTimer) { return; }"
            + "  pulseTimer = setInterval(function () {"
            + "   var marker = positionMarker();"
            + "   if (Date.now() >= pulseUntil) { clearInterval(pulseTimer); pulseTimer = null; if (marker) { marker.classList.remove('tarkov-monitor-position-pulse'); } return; }"
            + "   if (marker) { marker.classList.add('tarkov-monitor-position-pulse'); }"
            + "  }, 150);"
            + " }"
            + " window.addEventListener('message', function (event) {"
            + "  if (event.source !== window.parent || !event.data) { return; }"
            + "  if (event.data.type === 'tarkov-monitor-search') {"
            + "   applySearch(event.data.text || '');"
            + "  } else if (event.data.type === 'tarkov-monitor-position') {"
            + "   pulsePosition(event.data.duration || 5000);"
            + "  } else if (event.data.type === 'tarkov-monitor-close-panels') {"
            + "   collapsePanels(null);"
            + "  }"
            + " });"
            + "})();</script>"
            + "<script id=\"tarkov-monitor-frame-script\">(function () {"
            + "  var patched = false;"
            + "  function patch(L) {"
            + "    if (patched || !L || !L.Map || !L.latLngBounds) { return; }"
            + "    patched = true;"
            + "    var original = L.Map.prototype.setMaxBounds;"
            + "    L.Map.prototype.setMaxBounds = function (bounds) {"
            + "      var b = bounds ? L.latLngBounds(bounds) : null;"
            + "      return original.call(this, b && b.isValid() ? b.pad(1) : bounds);"
            + "    };"
            + "  }"
            + "  var current = window.L;"
            + "  Object.defineProperty(window, 'L', { configurable: true, enumerable: true,"
            + "    get: function () { return current; },"
            + "    set: function (value) { current = value; patch(value); } });"
            + "  patch(current);"
            + "})();</script>";
        private static readonly HttpClient mapFrameClient = new(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            AllowAutoRedirect = false,
        });
        private readonly System.Timers.Timer runthroughTimer;
        private readonly System.Timers.Timer scavCooldownTimer;
        private LocalizationService localizationService;
        private bool inRaid;
        private bool gameWatcherStarted;
        private int trackerStatusTransitionDepth;
        private FormWindowState lastPublishedWindowState = FormWindowState.Normal;
        private bool windowStateNotificationPending;
        private bool uiReady;
        private bool uiHostRevealed;
        private bool uiHostRevealQueued;
        private bool startupHeldForSplash;
        private bool startupServicesStarted;
        private readonly object trackerSessionNoticeLock = new();
        private long trackerSessionNoticeGeneration;
        private TrackerSessionNoticeIdentity? lastAnnouncedTrackerSession;
        private int noActiveEftSessionNoticePublished;
        private readonly object tarkovDevDataRefreshLock = new();
        private CancellationTokenSource? tarkovDevDataRefreshCancellation;
        private long tarkovDevDataRefreshGeneration;
        private Profile? tarkovDevDataProfile;
        private bool closing;

        private readonly record struct TrackerSessionNoticeIdentity(
            string AccountId,
            string ProfileId,
            EftSessionMode SessionMode);

        public event EventHandler? UiReady;
        public bool IsUiReady => uiReady;

        public MainBlazorUI(bool holdUntilSplashCompletes = false, DiagnosticsService? diagnosticsService = null)
        {
            InitializeComponent();
            startupHeldForSplash = holdUntilSplashCompletes;
            // The splash is an independent startup window. Unless a caller
            // explicitly asks for a gate, keep the main window visible while
            // WebView2 paints so both windows launch together with no reveal
            // delay or second native-host repaint.
            Opacity = startupHeldForSplash ? 0 : 1;
            this.TopMost = Properties.Settings.Default.stayOnTop;
            inRaid = false;

            // Singleton message log used to record and display messages for TarkovMonitor
            diagnostics = diagnosticsService ?? new DiagnosticsService();
            messageLog = new MessageLog(diagnostics);
            messageLog.AddMessage($"Tarkov Monitor v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");

            // Singleton log repository to record, display, and analyze logs for TarkovMonitor
            logRepository = new LogRepository();

            // Singleton Group tracker
            groupManager = new GroupManager();

			// Singleton tarkov.dev repository (to DI the results of the queries)
			//tarkovdevRepository = new TarkovDevRepository();

			eft = new GameWatcher();

            timersManager = new TimersManager(eft, messageLog);

            // State of the embedded Tarkov.dev map (Maps tab)
            mapsService = new MapsService();

            // Quest events from the game logs (which quests were really accepted)
            questLogStore = new QuestLogStore(Stats.DatabasePath);
            // A fresh installation counts from now; older sessions in the logs folder are not history.
            if (HistoryStart.Value == null && questLogStore.IsEmpty())
            {
                HistoryStart.Set(DateTime.Now);
            }
            HistoryStart.Changed += (_, _) =>
            {
                questLogStore.NotifyChanged();
                Stats.NotifyChanged();
                mapsService.NotifyQuestStateChanged(inRaid);
            };

            // Creates the dependency injection services which are the in-betweens for the Blazor interface and the rest of the C# application.
            var services = new ServiceCollection();
            services.AddWindowsFormsBlazorWebView();
            services.AddMudServices(configuration =>
            {
                configuration.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.TopCenter;
                configuration.PopoverOptions.FlipMargin = 8;
                configuration.PopoverOptions.OverflowPadding = 8;
            });
            services.AddLocalization();
            services.AddSingleton<DiagnosticsService>(diagnostics);
            services.AddSingleton<LocalizationService>();
            services.AddSingleton<GameWatcher>(eft);
            services.AddSingleton<MessageLog>(messageLog);
            services.AddSingleton<LogRepository>(logRepository);
            services.AddSingleton<GroupManager>(groupManager);
            services.AddSingleton<TimersManager>(timersManager);
            services.AddSingleton<MapsService>(mapsService);
            services.AddSingleton<QuestLogStore>(questLogStore);
            services.AddSingleton<TaskStateService>(new TaskStateService(questLogStore, mapsService));
            services.AddSingleton<MainBlazorUI>(this);

            blazorWebView1.HostPage = "wwwroot\\index.html";
            var serviceProvider = services.BuildServiceProvider();
            blazorWebView1.Services = serviceProvider;
            localizationService = serviceProvider.GetRequiredService<LocalizationService>();
            blazorWebView1.RootComponents.Add<TarkovMonitor.Blazor.App>("#app");
            //services.AddSingleton<TarkovDevRepository>(tarkovdevRepository);
            // Add event watchers
            eft.FleaSold += Eft_FleaSold;
            eft.FleaOfferExpired += Eft_FleaOfferExpired;
            eft.DebugMessage += Eft_DebugMessage;
            eft.ExceptionThrown += Eft_ExceptionThrown;
            eft.RaidStarting += Eft_RaidStarting;
            eft.RaidStarted += Eft_RaidStart;
            eft.RaidStopping += Eft_RaidStopping;
            eft.RaidExited += Eft_RaidExited;
            eft.RaidEnded += Eft_RaidEnded;
            eft.ExitedPostRaidMenus += Eft_ExitedPostRaidMenus;
            eft.TaskStarted += Eft_TaskStarted;
            eft.TaskFailed += Eft_TaskFailed;
            eft.TaskFinished += Eft_TaskFinished;
            eft.TaskModified += Eft_TaskModified;
            eft.NewLogData += Eft_NewLogData;
            eft.GroupInviteAccept += Eft_GroupInviteAccept;
            eft.GroupUserLeave += Eft_GroupUserLeave;
            eft.GroupRaidSettings += Eft_GroupRaidSettings;
            eft.GroupMemberReady += Eft_GroupMemberReady;
            eft.GroupDisbanded += Eft_GroupDisbanded;
            eft.MatchingAborted += Eft_GroupStaleEvent;
            eft.GameStarted += Eft_GroupStaleEvent;
            eft.GameStarted += Eft_GameStarted;
            eft.GameStopped += Eft_GameStopped;
            eft.MapLoading += Eft_MapLoading;
            eft.MapLoading += Eft_MapLoading_NavigateToMap;
            eft.MatchingStarted += Eft_MatchingStarted;
            eft.MatchFound += Eft_MatchFound;
            eft.PlayerPosition += Eft_PlayerPosition;
            eft.ProfileChanged += Eft_ProfileChanged;
            eft.ProfileReady += Eft_ProfileReady;
            eft.ControlSettings += Eft_ControlSettings;

            eft.InitialReadComplete += (object? sender, ProfileEventArgs e) =>
            {
                if (!e.Profile.HasTarkovDevPlayerRoute)
                {
                    PublishNoActiveEftSessionNotice();
                    TarkovTracker.ResetActiveState();
                    TarkovDev.StopAutoUpdates();
                    // EFT can be running at "Select Profile and Mode" while the
                    // watcher has not recovered a player route yet. Preserve the
                    // read-only Tarkov.dev preload until a real profile is selected.
                    return;
                }

                if (!eft.IsGameRunning)
                {
                    PublishNoActiveEftSessionNotice();
                    // The startup scan is historical, not a live EFT session.
                    // It may still establish the read-only Tarkov.dev context so
                    // the user does not need to launch EFT just to verify the
                    // data connection. Tracker writes remain inactive.
                    _ = RefreshTarkovDevApiData(e.Profile, allowPersistedProfile: true);
                    TarkovTracker.ResetActiveState();
                    TarkovDev.StopAutoUpdates();
                    return;
                }

                MarkEftSessionRecognized();
                // Load the data set for the exact EFT session mode detected by
                // the watcher. A later mode switch starts a new generation and
                // invalidates this load before it can publish stale assets.
                _ = RefreshTarkovDevApiData(e.Profile);
                TarkovDev.StartAutoUpdates();
                //TarkovDev.UpdatePlayerNames();

                // Historical profile identity remains available through GameWatcher for
                // Settings and Read Past Logs, but it must not activate or auto-bind a
                // tracker key while EFT is not running.
                if (!eft.IsGameRunning)
                {
                    TarkovTracker.DeactivateProfile();
                    return;
                }

                // The versioned .org store performs guarded legacy recovery. Keep the
                // original settings intact until a recovered key is explicitly assigned.
                _ = InitializeProgress(e.Profile);
            };

            Properties.Settings.Default.PropertyChanged += (object? sender, PropertyChangedEventArgs e) => {
                if (e.PropertyName == "stayOnTop")
                {
                    this.TopMost = Properties.Settings.Default.stayOnTop;
                }
                if (e.PropertyName == "customLogsPath")
                {
                    eft.LogsPath = Properties.Settings.Default.customLogsPath;
                    StartGameWatcher();
                }
            };

            TarkovTracker.ProgressRetrieved += TarkovTracker_ProgressRetrieved;
            TarkovDev.ExceptionThrown += TarkovDev_ExceptionThrown;

            UpdateCheck.NewVersion += UpdateCheck_NewVersion;
            UpdateCheck.Error += UpdateCheck_Error;

            SocketClient.ConnectionInterrupted += SocketClient_ConnectionInterrupted;

            blazorWebView1.WebView.CoreWebView2InitializationCompleted += WebView_CoreWebView2InitializationCompleted;

            runthroughTimer = new System.Timers.Timer(Properties.Settings.Default.runthroughTime.TotalMilliseconds)
            {
                AutoReset = false,
                Enabled = false
            };
            runthroughTimer.Elapsed += RunthroughTimer_Elapsed;
            scavCooldownTimer = new System.Timers.Timer(TimeSpan.FromSeconds(TarkovDev.ScavCooldownSeconds()).TotalMilliseconds)
            {
                AutoReset = false,
                Enabled = false
            };
            scavCooldownTimer.Elapsed += ScavCooldownTimer_Elapsed;
        }

        public bool IsMaximized => WindowState == FormWindowState.Maximized;

        // Default window size in logical pixels; the last size and state are remembered.
        private const int DefaultWindowWidth = 1100;
        private const int DefaultWindowHeight = 720;

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            MinimumSize = new System.Drawing.Size(MinimumWindowWidth, MinimumWindowHeight);
            RestoreWindowSize();
        }

        private void RestoreWindowSize()
        {
            var scale = DeviceDpi / 96f;
            var width = Properties.Settings.Default.windowWidth > 0 ? Properties.Settings.Default.windowWidth : DefaultWindowWidth;
            var height = Properties.Settings.Default.windowHeight > 0 ? Properties.Settings.Default.windowHeight : DefaultWindowHeight;
            var area = Screen.FromControl(this).WorkingArea;
            var size = new System.Drawing.Size(
                Math.Clamp((int)(width * scale), MinimumWindowWidth, area.Width),
                Math.Clamp((int)(height * scale), MinimumWindowHeight, area.Height));
            ClientSize = size;
            // Keep the window on screen after the resize.
            Location = new System.Drawing.Point(
                Math.Max(area.Left, Math.Min(Location.X, area.Right - Width)),
                Math.Max(area.Top, Math.Min(Location.Y, area.Bottom - Height)));
            if (Properties.Settings.Default.windowMaximized)
            {
                WindowState = FormWindowState.Maximized;
            }
        }

        private void SaveWindowSize()
        {
            try
            {
                var scale = DeviceDpi / 96f;
                var maximized = WindowState == FormWindowState.Maximized;
                var client = maximized ? RestoreBounds.Size - (Size - ClientSize) : ClientSize;
                if (client.Width >= MinimumWindowWidth && client.Height >= MinimumWindowHeight)
                {
                    Properties.Settings.Default.windowWidth = (int)Math.Round(client.Width / scale);
                    Properties.Settings.Default.windowHeight = (int)Math.Round(client.Height / scale);
                }
                Properties.Settings.Default.windowMaximized = maximized;
                Properties.Settings.Default.Save();
            }
            catch
            {
                // Remembering the size is a convenience only.
            }
        }

        public void MinimizeWindow() => WindowState = FormWindowState.Minimized;

        public void ToggleMaximizeWindow()
        {
            if (!IsMaximized)
            {
                WindowState = FormWindowState.Maximized;
                return;
            }

            WindowState = FormWindowState.Normal;
        }

        public void CloseWindow() => Close();

        private void RecordException(
            string displayMessage,
            string code,
            string operation,
            Exception exception,
            string service,
            string stage,
            string? endpoint = null,
            long? durationMilliseconds = null,
            string? incidentId = null)
        {
            messageLog.AddException(displayMessage, code, operation, exception, service, stage, endpoint, durationMilliseconds, incidentId);
        }

        public void MarkUiReady()
        {
            if (uiReady)
            {
                return;
            }

            uiReady = true;
            UiReady?.Invoke(this, EventArgs.Empty);
            RevealUiHostIfReady(revealImmediately: !startupHeldForSplash);
        }

        public void ReleaseSplashGate()
        {
            if (!startupHeldForSplash)
            {
                return;
            }

            startupHeldForSplash = false;
            RevealUiHostIfReady(revealImmediately: true);
        }

        private void RevealUiHostIfReady(bool revealImmediately = false)
        {
            if (IsDisposed || !IsHandleCreated || !uiReady || startupHeldForSplash || uiHostRevealed || uiHostRevealQueued)
            {
                return;
            }

            if (revealImmediately && !InvokeRequired)
            {
                RevealUiHost();
                return;
            }

            // WebView2 and Blazor are allowed to finish painting while the
            // splash is on top, but the native host must be revealed exactly
            // once. Multiple opacity changes can produce a full -> black ->
            // full repaint when the WebView surface is restored.
            uiHostRevealQueued = true;
            BeginInvoke(new Action(() =>
            {
                uiHostRevealQueued = false;
                RevealUiHost();
            }));
        }

        private void RevealUiHost()
        {
            if (IsDisposed || startupHeldForSplash || !uiReady || uiHostRevealed)
            {
                return;
            }

            uiHostRevealed = true;
            ShowInTaskbar = true;
            Opacity = 1;
            if (WindowState != FormWindowState.Minimized)
            {
                Activate();
            }

            // DWM can recreate the native frame when the hidden host is
            // revealed. Reapply the state-aware color after that transition
            // so the temporary white frame is not left behind.
            BeginInvoke(new Action(ApplyWindowFrameAttributes));
        }

        public void BeginWindowDrag()
        {
            ReleaseCapture();
            SendMessage(Handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
        }

        private void RefreshNormalWindowFrame()
        {
            SetWindowPos(
                Handle,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }

        private void ApplyWindowFrameAttributes()
        {
            if (!IsHandleCreated)
            {
                return;
            }

            var cornerPreference = DwmRound;
            DwmSetWindowAttribute(Handle, DwmWindowCornerPreference, ref cornerPreference, sizeof(int));

            // Keep the accepted gold frame around restored windows while
            // suppressing the native frame only for maximized windows.
            // WS_THICKFRAME remains enabled for Windows snap/resize behavior;
            // WM_NCACTIVATE below prevents it from being repainted white.
            var borderColor = WindowState == FormWindowState.Maximized
                ? DwmColorNone
                : TarkovBorderColor;
            DwmSetWindowAttribute(Handle, DwmBorderColor, ref borderColor, sizeof(int));

            var captionColor = TarkovHeaderColor;
            DwmSetWindowAttribute(Handle, DwmCaptionColor, ref captionColor, sizeof(int));
        }

        public void BeginWindowResize(int hitTest)
        {
            if (WindowState != FormWindowState.Normal || !IsResizeHit(hitTest))
            {
                return;
            }

            ReleaseCapture();
            SendMessage(Handle, WmNcLButtonDown, (IntPtr)hitTest, IntPtr.Zero);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Apply the borderless client frame immediately. Without an
            // initial SWP_FRAMECHANGED, DWM can keep the standard resize
            // frame until the first mouse hit-test or window-state change.
            RefreshNormalWindowFrame();
            ApplyWindowFrameAttributes();
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmNcPaint)
            {
                // The client area is the complete custom frame. Do not let
                // DefWindowProc paint the native resize border over it.
                message.Result = IntPtr.Zero;
                return;
            }

            if (message.Msg == WmNcActivate)
            {
                // DefWindowProc repaints the native non-client frame during
                // activation. Keep activation state while preventing that
                // repaint from restoring the white resize border.
                ApplyWindowFrameAttributes();
                message.Result = (IntPtr)1;
                return;
            }

            if (message.Msg == WmNcCalcSize && message.WParam != IntPtr.Zero)
            {
                message.Result = IntPtr.Zero;
                return;
            }

            if (message.Msg == WmNcHitTest && WindowState == FormWindowState.Normal)
            {
                message.Result = (IntPtr)GetResizeHitTest(GetScreenPosition(message.LParam));
                return;
            }

            base.WndProc(ref message);
        }

        private static Point GetScreenPosition(IntPtr packedCoordinates)
        {
            var packedPosition = packedCoordinates.ToInt64();
            return new Point(
                unchecked((short)(packedPosition & 0xffff)),
                unchecked((short)((packedPosition >> 16) & 0xffff)));
        }

        private int GetResizeHitTest(Point screenPosition)
        {
            var cursor = PointToClient(screenPosition);
            var left = cursor.X <= ResizeBorderWidth;
            var right = cursor.X >= ClientSize.Width - ResizeBorderWidth;
            var top = cursor.Y <= ResizeBorderWidth;
            var bottom = cursor.Y >= ClientSize.Height - ResizeBorderWidth;

            return (left, right, top, bottom) switch
            {
                (true, _, true, _) => 13,
                (_, true, true, _) => 14,
                (true, _, _, true) => 16,
                (_, true, _, true) => 17,
                (true, _, _, _) => 10,
                (_, true, _, _) => 11,
                (_, _, true, _) => 12,
                (_, _, _, true) => 15,
                _ => HtClient
            };
        }

        private static bool IsResizeHit(int hitTest) => hitTest is >= 10 and <= 17;

        private void Eft_ControlSettings(object? sender, ControlSettingsEventArgs e)
        {
            var startedUtc = DateTime.UtcNow;
            try
            {
                JsonArray keyBindings = e.ControlSettings["keyBindings"].AsArray();
                JsonNode screenshotBind = keyBindings.FirstOrDefault((n) => n.AsObject()["keyName"].ToString() == "MakeScreenshot" && n.AsObject()["variants"].AsArray().Any(variant => variant.AsObject()["isAxis"]?.GetValue<bool>() == true || variant.AsObject()["keyCode"].AsArray().Count > 0));
                if (screenshotBind == null)
                {
            messageLog.AddMessage("EFT has no screenshot key bound. Bind one to update your position on the Tarkov.dev map.", "info");
                    return;
                }
                var variant = screenshotBind["variants"].AsArray().FirstOrDefault(variant => variant.AsObject()["keyCode"].AsArray().Count > 0);
                if (variant == null)
                {
                    // screenshot is bound to an axis, like mousewheel
                    return;
                }
                var keys = variant["keyCode"].AsArray().Select(n => n.GetValue<string>());
                if (keys.Any(key => key == "SysReq"))
                {
                    messageLog.AddMessage("The EFT screenshot key is not bound correctly. Rebind it to update your position on the Tarkov.dev map.", "info");
                }
            }
            catch (Exception ex)
            {
                RecordException("EFT screenshot keybind could not be checked.", "TM-WATCHER-002", "ReadControlSettings", ex, "GameWatcher", "ControlSettings", durationMilliseconds: DiagnosticsService.ElapsedMilliseconds(startedUtc));
            }
        }

        private void Eft_ProfileChanged(object? sender, ProfileEventArgs e)
        {
            var profileSnapshot = e.Profile.Snapshot();
            mapsService.SetGameMode(profileSnapshot.SessionMode);
            // The map filters tasks only for a recognised session; reload it so the filter follows the profile.
            mapsService.NotifyQuestStateChanged(inRaid);
            if (profileSnapshot.HasTarkovDevPlayerRoute && eft.IsGameRunning)
            {
                MarkEftSessionRecognized();
            }
            else
            {
                PublishNoActiveEftSessionNotice();
            }
            if (profileSnapshot.HasTarkovDevPlayerRoute)
            {
                _ = RefreshTarkovDevApiData(profileSnapshot, allowPersistedProfile: !eft.IsGameRunning);
            }
            if (profileSnapshot.HasTarkovDevPlayerRoute && eft.IsGameRunning)
            {
                TarkovDev.StartAutoUpdates();
                _ = InitializeProgress(profileSnapshot, announceSession: true);
            }
            else
            {
                TarkovDev.StopAutoUpdates();
                if (!eft.IsGameRunning)
                {
                    TarkovTracker.DeactivateProfile();
                }
            }
        }

        private void Eft_ProfileReady(object? sender, ProfileEventArgs e)
        {
            var profileSnapshot = e.Profile.Snapshot();
            mapsService.SetGameMode(profileSnapshot.SessionMode);
            if (profileSnapshot.HasTarkovDevPlayerRoute && eft.IsGameRunning)
            {
                MarkEftSessionRecognized();
            }
            else
            {
                PublishNoActiveEftSessionNotice();
            }
            if (profileSnapshot.HasTarkovDevPlayerRoute)
            {
                _ = RefreshTarkovDevApiData(profileSnapshot, allowPersistedProfile: !eft.IsGameRunning);
            }
            if (profileSnapshot.HasTarkovDevPlayerRoute && eft.IsGameRunning)
            {
                TarkovDev.StartAutoUpdates();
                _ = InitializeProgress(profileSnapshot, announceSession: true);
            }
            else
            {
                TarkovDev.StopAutoUpdates();
                if (!eft.IsGameRunning)
                {
                    TarkovTracker.DeactivateProfile();
                }
            }
        }

        private void Eft_GameStopped(object? sender, EventArgs e)
        {
            PublishNoActiveEftSessionNotice();
            mapsService.NotifyQuestStateChanged(false);
            TarkovTracker.DeactivateProfile();
            TarkovDev.StopAutoUpdates();
            InvalidateTarkovDevData();
        }

        private void Eft_GameStarted(object? sender, EventArgs e)
        {
            lock (trackerSessionNoticeLock)
            {
                trackerSessionNoticeGeneration++;
                lastAnnouncedTrackerSession = null;
            }
        }

        private void Eft_ExitedPostRaidMenus(object? sender, RaidInfoEventArgs e)
        {
            if (Properties.Settings.Default.airFilterAlert && TarkovTracker.HasAirFilter())
            {
                Sound.Play("air_filter_off");
            }
        }

        private void ScavCooldownTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            if (!Properties.Settings.Default.scavCooldownAlert)
            {
                return;
            }
            if (!inRaid)
            {
                Sound.Play("scav_available");
            }
            messageLog.AddMessage("Your Scav is available.", "info");
        }

        private void RunthroughTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            if (Properties.Settings.Default.runthroughAlert)
            {
                Sound.Play("runthrough_over");
                messageLog.AddMessage("The run-through period is over.", "info");
            }
        }

        private void Delete_Screenshots(RaidInfoEventArgs e, MonitorMessage? monMessage = null, MonitorMessageButton? screenshotButton = null)
        {
            var screenshotCount = e.RaidInfo.Screenshots.Count;
            var screenshotLabel = screenshotCount == 1 ? "screenshot" : "screenshots";
            var startedUtc = DateTime.UtcNow;
            try
            {
                foreach (var filename in e.RaidInfo.Screenshots)
                {
                    File.Delete(Path.Combine(eft.ScreenshotsPath, filename));
                }
                messageLog.AddMessage($"Deleted {screenshotCount} raid {screenshotLabel}.");
            }
            catch (Exception ex)
            {
                RecordException("Raid screenshots could not be deleted.", "TM-FILES-001", "DeleteRaidScreenshots", ex, "Filesystem", "ScreenshotCleanup", durationMilliseconds: DiagnosticsService.ElapsedMilliseconds(startedUtc));
            }

            if (monMessage is null || screenshotButton is null)
            {
                return;
            }

            monMessage.Buttons.Remove(screenshotButton);
        }

        private void Handle_Screenshots(RaidInfoEventArgs e, MonitorMessage monMessage)
        {
            var automaticallyDelete = Properties.Settings.Default.automaticallyDeleteScreenshotsAfterRaid;
            if (automaticallyDelete)
            {
                Delete_Screenshots(e);
                return;
            }

            var screenshotCount = e.RaidInfo.Screenshots.Count;
            var screenshotLabel = screenshotCount == 1 ? "raid screenshot" : "raid screenshots";
            MonitorMessageButton screenshotButton = new($"Deleted {screenshotCount} {screenshotLabel}", Icons.Material.Filled.Delete);
            screenshotButton.OnClick = () =>
            {
                Delete_Screenshots(e, monMessage, screenshotButton);
            };
            screenshotButton.Timeout = TimeSpan.FromMinutes(120).TotalMilliseconds;
            monMessage.Buttons.Add(screenshotButton);
        }

        private async void Eft_RaidEnded(object? sender, RaidInfoEventArgs e)
        {
            inRaid = false;
            Stats.EndRaid(e.Profile?.Id, e.RaidInfo.RaidId);
            mapsService.RequestShowDashboard();
            await ResumeMediaAfterRaid();
            
            //groupManager.Stale = true;
            MonitorMessage monMessage = new($"Raid ended on {e.RaidInfo.Map?.name}.");

            if (e.RaidInfo.Screenshots.Count > 0) {
                Handle_Screenshots(e, monMessage);
            }

            messageLog.AddMessage(monMessage);
            runthroughTimer.Stop();
            if (Properties.Settings.Default.scavCooldownAlert && (e.RaidInfo.RaidType == RaidType.Scav || e.RaidInfo.RaidType == RaidType.PVE))
            {
                scavCooldownTimer.Stop();
                scavCooldownTimer.Interval = TimeSpan.FromSeconds(TarkovDev.ResetScavCoolDown()).TotalMilliseconds;
                scavCooldownTimer.Start();
            }
        }

        private void Eft_GroupRaidSettings(object? sender, LogContentEventArgs<GroupRaidSettingsLogContent> e)
        {
            return;
            groupManager.ClearGroup();
        }

        private void SocketClient_ConnectionInterrupted(object? sender, SocketConnectionIncidentEventArgs e)
        {
            if (closing)
            {
                return;
            }

            // A recoverable background disconnect is retained as sanitized
            // telemetry, not rendered as a frightening error card. A later
            // send owns user-facing reporting if lazy recovery fails.
            diagnostics.Capture(
                new DiagnosticContext(
                    "TM-SOCKET-001",
                    e.Operation,
                    "WebSocket",
                    "Background",
                    "Tarkov.dev connection interrupted.",
                    e.Endpoint,
                    IncidentId: e.IncidentId),
                e.Exception);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            // DWM can recreate the native frame while the hidden host is
            // being shown. Reapply the state-aware color after that transition
            // so the temporary white frame is not left behind.
            BeginInvoke(new Action(ApplyWindowFrameAttributes));

            var startedUtc = DateTime.UtcNow;
            try
            {
                if (Properties.Settings.Default.minimizeAtStartup)
                {

                    WindowState = FormWindowState.Minimized;
                }

                // Let WebView2 render the startup shell before watcher and
                // update-check work begins. This keeps startup responsive and
                // lets the application initialize behind the splash.
                BeginInvoke(new Action(StartStartupServices));
            }
            catch (Exception ex)
            {
                RecordException("The window could not minimize at startup.", "TM-UI-001", "MinimizeAtStartup", ex, "UI", "Startup", durationMilliseconds: DiagnosticsService.ElapsedMilliseconds(startedUtc));
            }
        }

        private void StartStartupServices()
        {
            if (startupServicesStarted || IsDisposed)
            {
                return;
            }

            startupServicesStarted = true;
            try
            {
                var lastKnownProfile = TarkovTracker.GetLastKnownOrgProfile();
                if (lastKnownProfile != null)
                {
                    // Tarkov.dev data is read-only and can be preloaded from the
                    // last complete profile without requiring EFT to be running.
                    // Live EFT identity is still required before tracker writes
                    // are activated.
                    _ = RefreshTarkovDevApiData(lastKnownProfile, allowPersistedProfile: true);
                }
                ScanQuestLogsInBackground();
                gameWatcherStarted = eft.Start();
                if (!eft.IsGameRunning)
                {
                    PublishNoActiveEftSessionNotice();
                }
            }
            catch (Exception ex)
            {
                RecordException("Game log monitoring could not start.", "TM-WATCHER-001", "StartGameWatcher", ex, "GameWatcher", "Startup");
            }

            try
            {
                UpdateCheck.CheckForNewVersion();
            }
            catch (Exception ex)
            {
                RecordException("Update checking could not start.", "TM-UPDATE-002", "CheckForNewVersion", ex, "UpdateCheck", "Startup");
            }

        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            closing = true;
            SaveWindowSize();
            SocketClient.ConnectionInterrupted -= SocketClient_ConnectionInterrupted;
            _ = SocketClient.StopAsync();
            base.OnFormClosed(e);
        }

        private async void Eft_PlayerPosition(object? sender, PlayerPositionEventArgs e)
        {
            if (e.RaidInfo.Map == null)
            {
                return;
            }
            // "position" messages stay in the log but do not pop up over the map.
            messageLog.AddMessage($"Current position on {e.RaidInfo.Map.name}: x={e.Position.X}, y={e.Position.Y}, z={e.Position.Z}.", "position");
            var positionMessage = SocketClient.GetPlayerPositionMessage(e);
            var navigateMessage = SocketClient.GetNavigateToMapMessage(e.RaidInfo.Map);

            // The embedded map always follows the game and gets the position
            // replayed after a reload; the user's remote follows the settings.
            mapsService.SetMap(e.RaidInfo.Map);
            mapsService.RememberPosition(positionMessage);
            await SendPlayerPositionAsync(new List<JsonObject> { navigateMessage, positionMessage }, SocketTargets.MapView);
            mapsService.NotifyPositionUpdated();

            List<JsonObject> remoteMessages = new() { positionMessage };
            if (Properties.Settings.Default.navigateMapOnPositionUpdate)
            {
                remoteMessages.Add(navigateMessage);
            }
            await SendPlayerPositionAsync(remoteMessages, SocketTargets.Remote);
        }

        private async Task SendPlayerPositionAsync(List<JsonObject> messages, SocketTargets targets)
        {
            var startedUtc = DateTime.UtcNow;
            try
            {
                await SocketClient.Send(messages, targets);
            }
            catch (Exception ex)
            {
                RecordException("Tarkov.dev is unavailable. No messages were resent; the connection will be retried when needed.", "TM-SOCKET-002", "SendPlayerPosition", ex, "WebSocket", "PlayerPosition", endpoint: SocketClient.GetEndpointForDiagnostics(), durationMilliseconds: DiagnosticsService.ElapsedMilliseconds(startedUtc), incidentId: SocketClient.GetIncidentId(ex));
            }
        }

        private void UpdateCheck_Error(object? sender, ExceptionEventArgs e)
        {
            RecordException("Update checking failed; copy diagnostics for details.", "TM-UPDATE-001", e.Context, e.Exception, "UpdateCheck", "Background");
        }

        private void TarkovDev_ExceptionThrown(object? sender, ExceptionEventArgs e)
        {
            var displayMessage = e.Context == "player profile lookup"
                ? "Player profile lookup failed; copy diagnostics for details."
                : "Automatic Tarkov.dev refresh failed; copy diagnostics for details.";
            RecordException(
                displayMessage,
                e.Context == "player profile lookup" ? "TM-API-TARKOVDEV-002" : "TM-API-TARKOVDEV-001",
                e.Context,
                e.Exception,
                "TarkovDev",
                "Background",
                e.Endpoint ?? "https://json.tarkov.dev",
                e.DurationMilliseconds);
        }

        private void UpdateCheck_NewVersion(object? sender, NewVersionEventArgs e)
        {
            messageLog.AddMessage($"A new Tarkov Monitor version is available ({e.Version}). Click to open the download page, and update before reporting a bug.", null, e.Uri.ToString());
        }

        private async void Eft_MapLoading(object? sender, EventArgs e)
        {
            if (TarkovTracker.Progress?.data?.tasksProgress == null)
            {
                return;
            }
            var startedUtc = DateTime.UtcNow;
            try
            {
                //await AllDataLoaded();
                var failedTasks = new List<TarkovDev.Task>();
                foreach (var taskStatus in TarkovTracker.Progress.data.tasksProgress)
                {
                    if (!taskStatus.failed)
                    {
                        continue;
                    }
                    var task = TarkovDev.Tasks.Find(match: t => t.id == taskStatus.id);
                    if (task == null)
                    {
                        continue;
                    }
                    if (task.restartable)
                    {
                        failedTasks.Add(task);
                    }
                }
                if (Properties.Settings.Default.airFilterAlert && TarkovTracker.HasAirFilter())
                {
                    await Sound.Play("air_filter_on");
                }
                if (Properties.Settings.Default.questItemsAlert)
                {
                    await Sound.Play("quest_items");
                }
                if (failedTasks.Count == 0)
                {
                    return;
                }
                foreach (var task in failedTasks)
                {
                    messageLog.AddMessage($"Task failed: {task.name}. Restart required.", "quest", task.wikiLink);
                }
                if (Properties.Settings.Default.restartTaskAlert)
                {
                    await Sound.Play("restart_failed_tasks");
                }
            }
            catch (Exception ex)
            {
                RecordException("Raid-start processing failed.", "TM-WATCHER-003", "RaidStartProcessing", ex, "GameWatcher", "RaidStart", durationMilliseconds: DiagnosticsService.ElapsedMilliseconds(startedUtc));
            }
        }

        private async void Eft_MapLoading_NavigateToMap(object? sender, RaidInfoEventArgs e)
        {
            if (e.RaidInfo.Map == null)
            {
                return;
            }
            await FollowMapAsync(e.RaidInfo.Map);
        }

        // The embedded map (Maps tab) always switches to the map being loaded.
        // The Tarkov.dev website remote only does so when the setting is on.
        private async Task FollowMapAsync(TarkovDev.Map map)
        {
            mapsService.SetMap(map);
            mapsService.RequestShowMaps();
            var targets = Properties.Settings.Default.autoNavigateMap ? SocketTargets.All : SocketTargets.MapView;
            await NavigateToMapWithDiagnostics(map, targets);
        }

        private async Task NavigateToMapWithDiagnostics(TarkovDev.Map map, SocketTargets targets = SocketTargets.All)
        {
            var startedUtc = DateTime.UtcNow;
            try
            {
                await SocketClient.Send(new List<JsonObject> { SocketClient.GetNavigateToMapMessage(map) }, targets);
            }
            catch (Exception exception)
            {
                RecordException(
                    "Tarkov.dev is unavailable. No messages were resent; the connection will be retried when needed.",
                    "TM-SOCKET-002",
                    "NavigateToMap",
                    exception,
                    "WebSocket",
                    "MapNavigation",
                    endpoint: SocketClient.GetEndpointForDiagnostics(),
                    durationMilliseconds: DiagnosticsService.ElapsedMilliseconds(startedUtc),
                    incidentId: SocketClient.GetIncidentId(exception));
            }
        }

        private void Eft_GroupUserLeave(object? sender, LogContentEventArgs<GroupMatchUserLeaveLogContent> e)
        {
            return;
            if (e.LogContent.Nickname != "You")
            {
                groupManager.RemoveGroupMember(e.LogContent.Nickname);
            }
            messageLog.AddMessage($"{e.LogContent.Nickname} left the group.", "group");
        }

        private void Eft_GroupInviteAccept(object? sender, LogContentEventArgs<GroupLogContent> e)
        {
            messageLog.AddMessage($"{e.LogContent.Info.Nickname} ({e.LogContent.Info.Side.ToUpper()} {e.LogContent.Info.Level}) accepted the group invite.", "group");
        }

        private void Eft_GroupDisbanded(object? sender, EventArgs e)
        {
            return;
            groupManager.ClearGroup();
        }

        private void TarkovTracker_ProgressRetrieved(object? sender, TarkovTracker.ProgressRetrievedEventArgs e)
        {
            // Hand the same tracker profile to the embedded Tarkov.dev map.
            mapsService.SetTrackerLink(e.SessionMode, e.ApiKey, Properties.Settings.Default.tarkovTrackerDomain);
            messageLog.AddMessage(
                string.Format(
                    localizationService.GetString("RetrievedDataFromTarkovTracker"),
                    e.Progress.data.displayName,
                    e.Progress.data.playerLevel,
                    e.Progress.data.pmcFaction,
                    TarkovTracker.GetSessionDisplayName(e.SessionMode)),
                "update");
            /*messageLog.AddProtectedMessage(
                string.Format(
                    localizationService.GetString("RetrievedDataFromTarkovTracker"),
                    e.Progress.data.displayName,
                    e.Progress.data.playerLevel,
                    e.Progress.data.pmcFaction,
                    TarkovTracker.GetSessionDisplayName(e.SessionMode)),
                "update",
                new[]
                {
                    new MonitorMessageProtectedValue("API token", e.ApiKey),
                },
                $"https://{Properties.Settings.Default.tarkovTrackerDomain}");*/
        }

        private void Eft_GroupStaleEvent(object? sender, EventArgs e)
        {
            return;
            groupManager.Stale = true;
        }

        private void WebView_CoreWebView2InitializationCompleted(object? sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (Debugger.IsAttached) blazorWebView1.WebView.CoreWebView2.OpenDevToolsWindow();

            if (e.IsSuccess)
            {
                ConfigureMapFrameHost(blazorWebView1.WebView.CoreWebView2);
            }

            if (!e.IsSuccess)
            {
                // Do not leave the native host invisible if WebView2 cannot
                // initialize; the normal Blazor error surface must remain
                // reachable for diagnosis.
                MarkUiReady();
            }
        }

        private void ConfigureMapFrameHost(CoreWebView2 core)
        {
            try
            {
                core.AddWebResourceRequestedFilter($"https://{MapFrameHost}/*", CoreWebView2WebResourceContext.Document);
                foreach (var domain in TarkovTracker.Domains.Keys)
                {
                    core.AddWebResourceRequestedFilter($"https://api.{domain}/*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
                }
                core.WebResourceRequested += CoreWebView2_WebResourceRequested;
            }
            catch (Exception exception)
            {
                RecordException("The Maps tab could not be prepared; the embedded map will not load.", "TM-MAPS-001", "ConfigureMapFrameHost", exception, "Maps", "Startup");
            }
        }

        // Tarkov.dev answers with "X-Frame-Options: DENY", which would block the
        // iframe used by the Maps tab. Document requests to tarkov.dev are
        // therefore fetched here and handed to WebView2 without that header.
        // Scripts, styles, images and API calls are not touched.
        private async void CoreWebView2_WebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            if (sender is not CoreWebView2 core || !Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri))
            {
                return;
            }
            if (uri.Host.StartsWith(TrackerApiHostPrefix, StringComparison.OrdinalIgnoreCase)
                && (Debugger.IsAttached || Environment.GetEnvironmentVariable("TARKOVMONITOR_MAPS_DEBUG") == "1"))
            {
                messageLog.AddMessage($"Map debug: tracker request {e.Request.Method} {uri.AbsolutePath} context={e.ResourceContext}", "info");
            }
            if (!string.Equals(e.Request.Method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (uri.Host.StartsWith(TrackerApiHostPrefix, StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath.Equals("/progress", StringComparison.OrdinalIgnoreCase)
                && (e.ResourceContext == CoreWebView2WebResourceContext.XmlHttpRequest || e.ResourceContext == CoreWebView2WebResourceContext.Fetch))
            {
                await HandleTrackerProgressRequest(core, e, uri);
                return;
            }
            if (e.ResourceContext != CoreWebView2WebResourceContext.Document
                || !uri.Host.Equals(MapFrameHost, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var deferral = e.GetDeferral();
            var startedUtc = DateTime.UtcNow;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                foreach (var header in e.Request.Headers)
                {
                    if (IsHopByHopHeader(header.Key))
                    {
                        continue;
                    }
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                using var response = await mapFrameClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                var body = new MemoryStream();
                await response.Content.CopyToAsync(body);
                body.Position = 0;
                if (string.Equals(response.Content.Headers.ContentType?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase))
                {
                    body = InjectMapFrameContent(body, mapsService.GetFrameBootstrapScript());
                }

                var headers = new StringBuilder();
                foreach (var header in response.Headers.Concat(response.Content.Headers))
                {
                    if (IsStrippedResponseHeader(header.Key))
                    {
                        continue;
                    }
                    foreach (var value in header.Value)
                    {
                        headers.Append(header.Key).Append(": ").Append(value).Append("\r\n");
                    }
                }

                // Continuations of the awaits above run on the UI thread, which is
                // where the WebView2 event arguments must be completed.
                e.Response = core.Environment.CreateWebResourceResponse(body, (int)response.StatusCode, response.ReasonPhrase ?? "OK", headers.ToString());
            }
            catch (Exception exception)
            {
                // Without a response WebView2 performs the normal request, which
                // shows the site's own error inside the frame.
                RecordException("The embedded Tarkov.dev map could not be loaded.", "TM-MAPS-001", "MapFrameRequest", exception, "Maps", "Frame", endpoint: uri.ToString(), durationMilliseconds: DiagnosticsService.ElapsedMilliseconds(startedUtc));
            }
            finally
            {
                deferral.Complete();
            }
        }

        private static MemoryStream InjectMapFrameContent(MemoryStream html, string bootstrapScript)
        {
            var text = Encoding.UTF8.GetString(html.GetBuffer(), 0, (int)html.Length);
            var headEnd = text.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
            if (headEnd < 0)
            {
                return html;
            }
            text = text.Insert(headEnd, MapFrameInjection + bootstrapScript);
            return new MemoryStream(Encoding.UTF8.GetBytes(text));
        }

        // The embedded Tarkov.dev page fetches its TarkovTracker progress itself.
        // The answer passes through here so quests the game logs prove were
        // never accepted can be marked failed: Tarkov.dev then treats them as
        // inactive and "only show markers for active tasks" matches the game.
        private bool SessionRecognised => eft.IsGameRunning && GameWatcher.CurrentProfile.Snapshot().SessionMode != EftSessionMode.Unknown;

        private async Task HandleTrackerProgressRequest(CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e, Uri uri)
        {
            var deferral = e.GetDeferral();
            if (Debugger.IsAttached || Environment.GetEnvironmentVariable("TARKOVMONITOR_MAPS_DEBUG") == "1")
            {
                var (debugProfileId, debugSessionMode) = ResolveQuestHistoryProfile();
                messageLog.AddMessage($"Map debug: tracker progress request ({e.ResourceContext}) profile={debugProfileId} mode={debugSessionMode} tasks={TarkovDev.Tasks.Count} withRequirements={TarkovDev.Tasks.Count(t => t.taskRequirements.Count > 0)} hideSetting={Properties.Settings.Default.mapHideUnacceptedTasks}", "info");
            }
            var startedUtc = DateTime.UtcNow;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                foreach (var header in e.Request.Headers)
                {
                    if (IsHopByHopHeader(header.Key))
                    {
                        continue;
                    }
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                using var response = await mapFrameClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                var body = new MemoryStream();
                await response.Content.CopyToAsync(body);
                body.Position = 0;
                // Which tasks are available depends on the profile and its mode; nothing is hidden before the game announced them.
                if (response.IsSuccessStatusCode && Properties.Settings.Default.mapHideUnacceptedTasks && SessionRecognised)
                {
                    body = HideUnacceptedTasks(body);
                }

                var headers = new StringBuilder();
                foreach (var header in response.Headers.Concat(response.Content.Headers))
                {
                    if (IsStrippedResponseHeader(header.Key))
                    {
                        continue;
                    }
                    foreach (var value in header.Value)
                    {
                        headers.Append(header.Key).Append(": ").Append(value).Append("\r\n");
                    }
                }
                e.Response = core.Environment.CreateWebResourceResponse(body, (int)response.StatusCode, response.ReasonPhrase ?? "OK", headers.ToString());
            }
            catch (Exception exception)
            {
                RecordException("The map could not filter the Tarkov Tracker progress; all available tasks are shown.", "TM-MAPS-004", "TrackerProgressFilter", exception, "Maps", "QuestHistory", endpoint: uri.GetLeftPart(UriPartial.Path), durationMilliseconds: DiagnosticsService.ElapsedMilliseconds(startedUtc));
            }
            finally
            {
                deferral.Complete();
            }
        }

        // The profile whose quest history applies: the tracker's active profile
        // while a session is recognised, otherwise the profile last seen in the logs.
        private static (string ProfileId, EftSessionMode SessionMode) ResolveQuestHistoryProfile()
        {
            var profileId = TarkovTracker.CurrentProfileId;
            if (!string.IsNullOrEmpty(profileId) && TarkovTracker.CurrentSessionMode != EftSessionMode.Unknown)
            {
                return (profileId, TarkovTracker.CurrentSessionMode);
            }
            var logProfile = GameWatcher.CurrentProfile.Snapshot();
            return (logProfile.Id ?? "", logProfile.SessionMode);
        }

        private MemoryStream HideUnacceptedTasks(MemoryStream json)
        {
            var root = JsonNode.Parse(json.GetBuffer().AsSpan(0, (int)json.Length));
            if (root?["data"]?["tasksProgress"] is not JsonArray tasksProgress)
            {
                json.Position = 0;
                return json;
            }
            var completed = new HashSet<string>();
            var entries = new Dictionary<string, JsonObject>();
            foreach (var node in tasksProgress)
            {
                if (node is not JsonObject entry || entry["id"]?.GetValue<string>() is not string id)
                {
                    continue;
                }
                entries[id] = entry;
                if (entry["complete"]?.GetValue<bool>() == true)
                {
                    completed.Add(id);
                }
            }

            // Objective progress proves acceptance even before the logged period.
            var objectiveToTask = new Dictionary<string, string>();
            foreach (var task in TarkovDev.Tasks)
            {
                foreach (var objective in task.objectives)
                {
                    if (!string.IsNullOrEmpty(objective.id))
                    {
                        objectiveToTask[objective.id] = task.id;
                    }
                }
            }
            var evidence = new HashSet<string>();
            if (root["data"]?["taskObjectivesProgress"] is JsonArray objectivesProgress)
            {
                foreach (var node in objectivesProgress)
                {
                    if (node is not JsonObject entry || entry["id"]?.GetValue<string>() is not string objectiveId)
                    {
                        continue;
                    }
                    var complete = entry["complete"]?.GetValue<bool>() == true;
                    var count = entry["count"] is JsonValue value && value.TryGetValue<int>(out var parsed) ? parsed : 0;
                    if ((complete || count > 0) && objectiveToTask.TryGetValue(objectiveId, out var taskId))
                    {
                        evidence.Add(taskId);
                    }
                }
            }

            var (profileId, sessionMode) = ResolveQuestHistoryProfile();
            var hidden = questLogStore.ComputeHiddenTaskIds(profileId, sessionMode, TarkovDev.Tasks, completed, evidence, Properties.Settings.Default.mapStrictAcceptedTasks);
            if (hidden.Count == 0)
            {
                json.Position = 0;
                return json;
            }
            foreach (var id in hidden)
            {
                if (entries.TryGetValue(id, out var entry))
                {
                    entry["failed"] = true;
                }
                else
                {
                    tasksProgress.Add(new JsonObject { ["id"] = id, ["complete"] = false, ["failed"] = true });
                }
            }
            // The page refetches its progress every few minutes; only report changes.
            if (hidden.Count != lastHiddenTaskCount)
            {
                lastHiddenTaskCount = hidden.Count;
                messageLog.AddMessage($"Map: hiding {hidden.Count} available task(s) the game logs show as never accepted.", "info");
            }
            return new MemoryStream(Encoding.UTF8.GetBytes(root.ToJsonString()));
        }

        private bool questLogScanStarted;
        private int lastHiddenTaskCount = -1;

        private void ScanQuestLogsInBackground()
        {
            if (questLogScanStarted)
            {
                return;
            }
            questLogScanStarted = true;
            var logsPath = eft.LogsPath;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                var startedUtc = DateTime.UtcNow;
                try
                {
                    var result = questLogStore.ScanLogs(logsPath, eft);
                    if (result.Folders > 0)
                    {
                        var since = result.Horizon?.ToString("yyyy-MM-dd") ?? "unknown";
                        messageLog.AddMessage($"Quest history: {result.Events} quest events found in {result.Folders} log sessions since {since}.", "info");
                    }
                    else
                    {
                        messageLog.AddMessage($"Quest history: no game log sessions found in \"{logsPath}\".", "warning");
                    }
                    if (result.NewEvents > 0)
                    {
                        mapsService.NotifyQuestStateChanged(inRaid);
                    }
                }
                catch (Exception exception)
                {
                    RecordException("Reading the quest history from the game logs failed.", "TM-MAPS-003", "ScanQuestLogs", exception, "Maps", "QuestHistory", durationMilliseconds: DiagnosticsService.ElapsedMilliseconds(startedUtc));
                }
            });
        }

        private void Eft_TaskModified(object? sender, LogContentEventArgs<TaskStatusMessageLogContent> e)
        {
            if (GameWatcher.ReadingPastLogs)
            {
                return;
            }
            questLogStore.AddLiveEvent(e.Profile, e.LogContent.TaskId, e.LogContent.Status, DateTime.Now);
            mapsService.NotifyQuestStateChanged(inRaid);
        }

        private static bool IsHopByHopHeader(string name)
        {
            return name.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsStrippedResponseHeader(string name)
        {
            return name.Equals("X-Frame-Options", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Connection", StringComparison.OrdinalIgnoreCase);
        }

        private void MainBlazorUI_Shown(object? sender, EventArgs e)
        {
            StartGameWatcher();
        }

        private void StartGameWatcher()
        {
            if (gameWatcherStarted)
            {
                return;
            }
            try
            {
                ScanQuestLogsInBackground();
                gameWatcherStarted = eft.Start();
            }
            catch (Exception ex)
            {
                RecordException("Game log monitoring could not start.", "TM-WATCHER-001", "StartGameWatcher", ex, "GameWatcher", "Startup");
            }
        }

        private async Task RefreshTarkovDevApiData(Profile profile, bool allowPersistedProfile = false)
        {
            var profileSnapshot = profile.Snapshot();
            if (!profileSnapshot.HasTarkovDevPlayerRoute
                || profileSnapshot.Type == ProfileType.Unknown)
            {
                InvalidateTarkovDevData();
                return;
            }

            CancellationTokenSource refreshCancellation;
            long refreshGeneration;
            lock (tarkovDevDataRefreshLock)
            {
                if (tarkovDevDataProfile != null
                    && ProfilesMatch(tarkovDevDataProfile, profileSnapshot)
                    && TarkovDev.LoadedProfileType == profileSnapshot.Type)
                {
                    return;
                }

                tarkovDevDataRefreshGeneration++;
                refreshGeneration = tarkovDevDataRefreshGeneration;
                tarkovDevDataRefreshCancellation?.Cancel();
                refreshCancellation = new CancellationTokenSource();
                tarkovDevDataRefreshCancellation = refreshCancellation;
                tarkovDevDataProfile = profileSnapshot;

            }

            var published = false;
            try
            {
                var data = await TarkovDev.LoadApiData(profileSnapshot.Type, refreshCancellation.Token);
                lock (tarkovDevDataRefreshLock)
                {
                    if (refreshGeneration != tarkovDevDataRefreshGeneration
                        || !ReferenceEquals(refreshCancellation, tarkovDevDataRefreshCancellation)
                        || !IsTarkovDevRefreshOwnerCurrent(profileSnapshot, allowPersistedProfile))
                    {
                        return;
                    }

                    TarkovDev.PublishApiData(data, profileSnapshot);
                    published = true;
                }

                if (eft.IsGameRunning && !allowPersistedProfile)
                {
                    // Delay only the current-session Tarkov.dev notification so
                    // the session/progress messages can appear first. Do not
                    // delay startup preload or change callback sequencing.
                    await Task.Delay(TimeSpan.FromMilliseconds(1000), refreshCancellation.Token);
                }

                messageLog.AddMessage(
                    string.Format(
                        localizationService.GetString("RetrievedDataFromTarkovDev"),
                        String.Format("{0:n0}", data.Items.Count),
                        data.Maps.Count,
                        data.Traders.Count,
                        data.Tasks.Count,
                        data.Stations.Count,
                        TarkovTracker.GetSessionDisplayName(profileSnapshot.SessionMode)),
                    "update");
            }
            catch (OperationCanceledException) when (refreshCancellation.IsCancellationRequested)
            {
                // A newer EFT session owns the next asset load.
            }
            catch (Exception ex)
            {
                lock (tarkovDevDataRefreshLock)
                {
                    if (refreshGeneration != tarkovDevDataRefreshGeneration
                        || !ReferenceEquals(refreshCancellation, tarkovDevDataRefreshCancellation)
                        || !IsTarkovDevRefreshOwnerCurrent(profileSnapshot, allowPersistedProfile))
                    {
                        return;
                    }
                }

                RecordException($"Tarkov.dev data update failed for {TarkovTracker.GetSessionDisplayName(profileSnapshot.SessionMode)}; copy diagnostics for details.", "TM-API-TARKOVDEV-001", "UpdateApiData", ex, "TarkovDev", "DataUpdate", "https://json.tarkov.dev");
            }
            finally
            {
                lock (tarkovDevDataRefreshLock)
                {
                    if (refreshGeneration == tarkovDevDataRefreshGeneration
                        && ReferenceEquals(refreshCancellation, tarkovDevDataRefreshCancellation))
                    {
                        if (!published)
                        {
                            tarkovDevDataProfile = null;
                        }
                        tarkovDevDataRefreshCancellation = null;
                    }
                }
            }
        }

        private static bool ProfilesMatch(Profile left, Profile right)
        {
            return left.Type == right.Type
                && left.SessionMode == right.SessionMode
                && string.Equals(left.AccountId, right.AccountId, StringComparison.Ordinal)
                && string.Equals(left.Id, right.Id, StringComparison.Ordinal);
        }

        private static bool IsCurrentProfile(Profile expectedProfile)
        {
            var currentProfile = GameWatcher.CurrentProfile.Snapshot();
            return currentProfile.HasTarkovDevPlayerRoute
                && ProfilesMatch(currentProfile, expectedProfile);
        }

        private bool IsTarkovDevRefreshOwnerCurrent(Profile expectedProfile, bool allowPersistedProfile)
        {
            if (eft.IsGameRunning)
            {
                if (IsCurrentProfile(expectedProfile))
                {
                    return true;
                }

                // While EFT is waiting at profile selection, there is no live
                // player route to own the refresh. Permit only the persisted
                // read-only preload; a selected live profile must supersede it.
                if (!allowPersistedProfile
                    || GameWatcher.CurrentProfile.Snapshot().HasTarkovDevPlayerRoute)
                {
                    return false;
                }

                var waitingProfile = TarkovTracker.GetLastKnownOrgProfile();
                return waitingProfile != null
                    && ProfilesMatch(waitingProfile, expectedProfile);
            }

            if (!allowPersistedProfile)
            {
                return false;
            }

            var historicalProfile = GameWatcher.CurrentProfile.Snapshot();
            if (historicalProfile.HasTarkovDevPlayerRoute
                && ProfilesMatch(historicalProfile, expectedProfile))
            {
                return true;
            }

            var lastKnownProfile = TarkovTracker.GetLastKnownOrgProfile();
            return lastKnownProfile != null
                && ProfilesMatch(lastKnownProfile, expectedProfile);
        }

        private void InvalidateTarkovDevData()
        {
            lock (tarkovDevDataRefreshLock)
            {
                tarkovDevDataRefreshGeneration++;
                tarkovDevDataRefreshCancellation?.Cancel();
                tarkovDevDataRefreshCancellation = null;
                tarkovDevDataProfile = null;
            }
        }

        private void PublishNoActiveEftSessionNotice()
        {
            if (Interlocked.Exchange(ref noActiveEftSessionNoticePublished, 1) == 0)
            {
                messageLog.AddMessage(
                    localizationService.GetString("NoActiveEftSessionRecognized"),
                    "info");
            }
        }

        private void MarkEftSessionRecognized()
        {
            Volatile.Write(ref noActiveEftSessionNoticePublished, 0);
        }

        private async Task InitializeProgress(Profile? profile = null, bool announceSession = true)
        {
            var profileSnapshot = (profile ?? GameWatcher.CurrentProfile).Snapshot();
            long noticeGeneration = 0;
            if (announceSession)
            {
                lock (trackerSessionNoticeLock)
                {
                    noticeGeneration = trackerSessionNoticeGeneration;
                }
            }

            if (TarkovTracker.IsLegacyService
                || !profileSnapshot.HasIdentity
                || !profileSnapshot.SupportsTarkovTrackerWrites)
            {
                TarkovTracker.DeactivateProfile();
                return;
            }
            var startedUtc = DateTime.UtcNow;
            try
            {
                await TarkovTracker.SetProfile(profileSnapshot);
            }
            catch (ProfileActivationSupersededException)
            {
                // A newer profile/key/mode activation owns the result. This is
                // expected latest-wins behavior, not a user-visible failure.
                return;
            }
            catch (Exception ex)
            {
                RecordException("Tarkov Tracker profile retrieval failed; copy diagnostics for details.", "TM-API-TRACKER-001", "GetProfile", ex, "TarkovTracker", "Profile", $"https://{Properties.Settings.Default.tarkovTrackerDomain}", DiagnosticsService.ElapsedMilliseconds(startedUtc));
                return;
            }

            if (!announceSession)
            {
                return;
            }

            var identity = new TrackerSessionNoticeIdentity(
                profileSnapshot.AccountId,
                profileSnapshot.Id,
                profileSnapshot.SessionMode);
            lock (trackerSessionNoticeLock)
            {
                if (noticeGeneration != trackerSessionNoticeGeneration
                    || lastAnnouncedTrackerSession == identity)
                {
                    return;
                }

                lastAnnouncedTrackerSession = identity;
            }

            mapsService.SetGameMode(profileSnapshot.SessionMode);
            messageLog.AddMessage($"EFT session confirmed: {TarkovTracker.GetSessionDisplayName(profileSnapshot.SessionMode)}.", "info");
            /*messageLog.AddProtectedMessage(
                $"EFT session confirmed: {TarkovTracker.GetSessionDisplayName(profileSnapshot.SessionMode)}.",
                "info",
                new[]
                {
                    new MonitorMessageProtectedValue("Account ID", profileSnapshot.AccountId),
                    new MonitorMessageProtectedValue("Profile ID", profileSnapshot.Id),
                });*/
            if (TarkovTracker.GetTokenForProfile(profileSnapshot) == "")
            {
                messageLog.AddMessage(localizationService.GetString("ToAutomaticallyTrackTaskProgress"));
                return;
            }
            /*try
            {
                var tokenResponse = await TarkovTracker.TestToken(TarkovTracker.GetToken(eft.CurrentProfile.Id));
                if (!tokenResponse.permissions.Contains("WP"))
                {
                    messageLog.AddMessage("Your Tarkov Tracker token does not have the required write permissions.", "warning");
                }
            }
            catch (Exception ex)
            {
                RecordException("Tarkov Tracker token validation failed; copy diagnostics for details.", "TM-API-TRACKER-006", "TestToken", ex, "TarkovTracker", "TokenValidation", $"https://{Properties.Settings.Default.tarkovTrackerDomain}");
                return;
            }*/
        }

        internal void BeginTrackerStatusTransition()
        {
            Interlocked.Increment(ref trackerStatusTransitionDepth);
            TarkovTracker.DeactivateProfile();
        }

        internal void CompleteTrackerStatusTransition()
        {
            if (Interlocked.Decrement(ref trackerStatusTransitionDepth) < 0)
            {
                Interlocked.Exchange(ref trackerStatusTransitionDepth, 0);
                throw new InvalidOperationException(
                    "TarkovTracker status transition completed without a matching start.");
            }
        }

        private void Eft_MatchFound(object? sender, RaidInfoEventArgs e)
        {
            if (Properties.Settings.Default.matchFoundAlert)
            {
                Sound.Play("match_found");
            }
            var mapName = e.RaidInfo.Map?.name ?? "unknown map";
            messageLog.AddMessage($"Matching complete on {mapName} after {e.RaidInfo.QueueTime:0.##} seconds.");
        }

        private void Eft_MatchingStarted(object? sender, RaidInfoEventArgs e)
        {
            var mapName = e.RaidInfo.Map?.name;
            var message = string.IsNullOrWhiteSpace(mapName)
                ? "Matching started"
                : $"Matching started on {mapName}";
            messageLog.AddMessage(message, "info");
        }

        private void Eft_NewLogData(object? sender, NewLogDataEventArgs e)
        {
            TarkovDev.LastActivity = DateTime.Now;
            var startedUtc = DateTime.UtcNow;
            try
            {
                //Debug.WriteLine($"MainBlazorUI {e.Type} NewLogData");
                logRepository.AddLog(e.Data, e.Type.ToString());
            } catch (Exception ex)
            {
                RecordException("A game log event could not be stored.", "TM-DATA-001", "AddLog", ex, "LogRepository", "Persistence", durationMilliseconds: DiagnosticsService.ElapsedMilliseconds(startedUtc));
            }
        }

        private void Eft_GroupMemberReady(object? sender, LogContentEventArgs<GroupMatchRaidReadyLogContent> e)
        {
            return;
            groupManager.UpdateGroupMember(e.LogContent);
            messageLog.AddMessage($"{e.LogContent.extendedProfile.Info.Nickname} ({e.LogContent.extendedProfile.PlayerVisualRepresentation.Info.Side.ToUpper()} {e.LogContent.extendedProfile.PlayerVisualRepresentation.Info.Level}) is ready.", "group");
        }

        private async void Eft_TaskFinished(object? sender, LogContentEventArgs<TaskStatusMessageLogContent> e)
        {
            //await AllDataLoaded();
            var task = TarkovDev.Tasks.Find(t => t.id == e.LogContent.TaskId);
            if (task == null)
            {
                //Debug.WriteLine($"Task with id {e.TaskId} not found");
                return;
            }

            messageLog.AddMessage($"Task completed: {task.name}.", "quest", $"https://tarkov.dev/task/{task.normalizedName}");

            if (!TarkovTracker.ValidToken)
            {
                return;
            }
            var startedUtc = DateTime.UtcNow;
            try
            {
                await TarkovTracker.SetTaskComplete(
                    task.id,
                    e.Profile.Id,
                    e.Profile.SessionMode,
                    e.Profile.AccountId);
                //messageLog.AddMessage(response, "quest");
            }
            catch (Exception ex)
            {
                RecordException("Tarkov Tracker task progress could not be updated.", "TM-API-TRACKER-002", "SetTaskComplete", ex, "TarkovTracker", "TaskUpdate", $"https://{Properties.Settings.Default.tarkovTrackerDomain}", DiagnosticsService.ElapsedMilliseconds(startedUtc));
            }
        }

        private async void Eft_TaskFailed(object? sender, LogContentEventArgs<TaskStatusMessageLogContent> e)
        {
            var task = TarkovDev.Tasks.Find(t => t.id == e.LogContent.TaskId);
            if (task == null)
            {
                return;
            }

            messageLog.AddMessage($"Task failed: {task.name}.", "quest", $"https://tarkov.dev/task/{task.normalizedName}");

            if (!TarkovTracker.ValidToken)
            {
                return;
            }
            var startedUtc = DateTime.UtcNow;
            try
            {
                await TarkovTracker.SetTaskFailed(
                    task.id,
                    e.Profile.Id,
                    e.Profile.SessionMode,
                    e.Profile.AccountId);
                //messageLog.AddMessage(response, "quest");
            }
            catch (Exception ex)
            {
                RecordException("Tarkov Tracker task progress could not be updated.", "TM-API-TRACKER-003", "SetTaskFailed", ex, "TarkovTracker", "TaskUpdate", $"https://{Properties.Settings.Default.tarkovTrackerDomain}", DiagnosticsService.ElapsedMilliseconds(startedUtc));
            }
        }

        private async void Eft_TaskStarted(object? sender, LogContentEventArgs<TaskStatusMessageLogContent> e)
        {
            var task = TarkovDev.Tasks.Find(t => t.id == e.LogContent.TaskId);
            if (task == null)
            {
                return;
            }
            messageLog.AddMessage($"Task started: {task.name}.", "quest", $"https://tarkov.dev/task/{task.normalizedName}");

            if (!TarkovTracker.ValidToken)
            {
                return;
            }
            var startedUtc = DateTime.UtcNow;
            try
            {
                await TarkovTracker.SetTaskStarted(
                    e.LogContent.TaskId,
                    e.Profile.Id,
                    e.Profile.SessionMode,
                    e.Profile.AccountId);
            }
            catch (Exception ex)
            {
                RecordException("Tarkov Tracker task progress could not be updated.", "TM-API-TRACKER-004", "SetTaskStarted", ex, "TarkovTracker", "TaskUpdate", $"https://{Properties.Settings.Default.tarkovTrackerDomain}", DiagnosticsService.ElapsedMilliseconds(startedUtc));
            }
        }

        private void Eft_FleaSold(object? sender, LogContentEventArgs<FleaSoldMessageLogContent> e)
        {
            Stats.AddFleaSale(e.LogContent, e.Profile);
            if (TarkovDev.Items == null)
            {
                return;
            }
            List<string> received = new();
            //await AllDataLoaded();
            foreach (var receivedId in e.LogContent.ReceivedItems.Keys)
            {
                if (receivedId == "5449016a4bdc2d6f028b456f")
                {
                    received.Add(e.LogContent.ReceivedItems[receivedId].ToString("C0", CultureInfo.CreateSpecificCulture("ru-RU")));
                    continue;
                }
                else if (receivedId == "5696686a4bdc2da3298b456a")
                {
                    received.Add(e.LogContent.ReceivedItems[receivedId].ToString("C0", CultureInfo.CreateSpecificCulture("en-US")));
                    continue;
                }
                else if (receivedId == "569668774bdc2da2298b4568")
                {
                    received.Add(e.LogContent.ReceivedItems[receivedId].ToString("C0", CultureInfo.CreateSpecificCulture("de-DE")));
                    continue;
                }
                var receivedItem = TarkovDev.Items.Find(item => item.id == receivedId);
                if (receivedItem == null)
                {
                    continue;
                }
                received.Add($"{String.Format("{0:n0}", e.LogContent.ReceivedItems[receivedId])} {receivedItem.name}");
            }
            var soldItem = TarkovDev.Items.Find(item => item.id == e.LogContent.SoldItemId);
            if (soldItem == null)
            {
                return;
            }
            messageLog.AddMessage($"{e.LogContent.Buyer} purchased {String.Format("{0:n0}", e.LogContent.SoldItemCount)} {soldItem.name} for {String.Join(", ", received.ToArray())}", "flea", soldItem.link);
        }

        private void Eft_FleaOfferExpired(object? sender, LogContentEventArgs<FleaExpiredMessageLogContent> e)
        {
            if (TarkovDev.Items == null)
            {
                return;
            }
            var unsoldItem = TarkovDev.Items.Find(item => item.id == e.LogContent.ItemId);
            if (unsoldItem == null)
            {
                return;
            }
            messageLog.AddMessage($"Your offer for {unsoldItem.name} (x{e.LogContent.ItemCount}) has expired.", "flea", unsoldItem.link);
        }

        private void Eft_DebugMessage(object? sender, DebugEventArgs e)
        {
            messageLog.AddMessage(e.Message, "debug");
        }

        private void Eft_ExceptionThrown(object? sender, ExceptionEventArgs e)
        {
            RecordException("EFT monitoring failed; copy diagnostics for details.", "TM-WATCHER-004", e.Context, e.Exception, "GameWatcher", "Runtime");
        }

        private async void Eft_RaidStarting(object? sender, RaidInfoEventArgs e)
        {
            if (Properties.Settings.Default.raidStartAlert)
            {
                // always notify if the GameStarting event appeared
                Sound.Play("raid_starting");
            }

            await PauseMediaForRaid();
        }

        private async Task PauseMediaForRaid()
        {
            if (!Properties.Settings.Default.pauseMediaOnRaid) return;

            var startedUtc = DateTime.UtcNow;
            try
            {
                int pausedSessions = await MediaController.PauseAsync();
                var sessionLabel = pausedSessions == 1 ? "session" : "sessions";
                messageLog.AddMessage($"Paused {pausedSessions} music {sessionLabel}.", "info");
            }
            catch (Exception ex)
            {
                RecordException("Media could not be paused for the raid.", "TM-MEDIA-001", "PauseMedia", ex, "Media", "RaidStart", durationMilliseconds: DiagnosticsService.ElapsedMilliseconds(startedUtc));
            }
        }

        private async void Eft_RaidStopping(object? sender, EventArgs e)
        {
            await ResumeMediaAfterRaid();
        }

        private async Task ResumeMediaAfterRaid()
        {
            if (!Properties.Settings.Default.pauseMediaOnRaid) return;

            var startedUtc = DateTime.UtcNow;
            try
            {
                int resumedSessions = await MediaController.ResumeAsync();
                if (resumedSessions > 0)
                {
                    var sessionLabel = resumedSessions == 1 ? "session" : "sessions";
                    messageLog.AddMessage($"Resumed {resumedSessions} music {sessionLabel}.", "info");
                }
            }
            catch (Exception ex)
            {
                RecordException("Media could not be resumed after the raid.", "TM-MEDIA-002", "ResumeMedia", ex, "Media", "RaidEnd", durationMilliseconds: DiagnosticsService.ElapsedMilliseconds(startedUtc));
            }
        }

        private async void Eft_RaidStart(object? sender, RaidInfoEventArgs e)
        {
            inRaid = true;
            Stats.AddRaid(e);
            
            // GameStarting is not always logged for scav raids, so pause here as a fallback.
            if (e.RaidInfo.StartingTime == null)
            {
                await PauseMediaForRaid();
            }
            
            if (!e.RaidInfo.Reconnected && e.RaidInfo.RaidType != RaidType.Unknown)
            {
                MonitorMessage monMessage = new($"Starting a {e.RaidInfo.RaidType} raid on {e.RaidInfo.Map?.name}.");
                if (e.RaidInfo.Map != null && e.RaidInfo.StartedTime != null && e.RaidInfo.Map.HasGoons())
                {
                    AddGoonsButton(monMessage, e.RaidInfo);
                }
                else if (e.RaidInfo.Map == null)
                {
                    monMessage.Message = $"Starting a {e.RaidInfo.RaidType} raid. Choose a map:";
                    MonitorMessageSelect select = new();
                    foreach (var gameMap in TarkovDev.Maps)
                    {
                        select.Options.Add(new(gameMap.name, gameMap.id));
                    }
                    select.Placeholder = "Choose a map";
                    monMessage.Selects.Add(select);
                    MonitorMessageButton mapButton = new("Set map", Icons.Material.Filled.Map);
                    mapButton.OnClick += async () => {
                        if (select.Selected == null)
                        {
                            return;
                        }
                        e.RaidInfo.Map = TarkovDev.Maps.Find(m => m.id == select.Selected.Value);
                        Stats.SetRaidMap(e.Profile?.Id, e.RaidInfo.RaidId, e.RaidInfo.Map?.nameId);
                        monMessage.Message = $"Starting a {e.RaidInfo.RaidType} raid on {select.Selected.Text}.";
                        monMessage.Buttons.Clear();
                        monMessage.Selects.Clear();
                        //AddGoonsButton(monMessage, e.RaidInfo); // offline raids have goons on all goons maps
                        if (e.RaidInfo.Map != null)
                        {
                            await FollowMapAsync(e.RaidInfo.Map);
                        }
                    };
                    monMessage.Buttons.Add(mapButton);
                }
                messageLog.AddMessage(monMessage);
                if (Properties.Settings.Default.raidStartAlert && e.RaidInfo.StartingTime == null)
                {
                    // if there was no GameStarting event in the log, play the notification sound
                    Sound.Play("raid_starting");
                }
            }
            else
            {
                messageLog.AddMessage($"Re-entering the raid on {e.RaidInfo.Map?.name}.");
            }
            if (Properties.Settings.Default.runthroughAlert && !e.RaidInfo.Reconnected && (e.RaidInfo.RaidType == RaidType.PMC || e.RaidInfo.RaidType == RaidType.PVE))
            {
                runthroughTimer.Stop();
                runthroughTimer.Start();
            }
            return;
            if (Properties.Settings.Default.submitQueueTime && e.RaidInfo.QueueTime > 0 && e.RaidInfo.RaidType != RaidType.Unknown)
            {
                try
                {
                    await TarkovDev.PostQueueTime(e.RaidInfo.Map.nameId, (int)Math.Round(e.RaidInfo.QueueTime), e.RaidInfo.RaidType.ToString().ToLower(), GameWatcher.CurrentProfile.Type);
                }
                catch (Exception ex)
                {
#if DEBUG
                    messageLog.AddMessage($"Error submitting queue time: {ex.Message}", "exception");
#endif
                }
            }
        }

        private void AddGoonsButton(MonitorMessage monMessage, RaidInfo raidInfo)
        {
            if (raidInfo.Map != null && raidInfo.StartedTime != null && raidInfo.Map.HasGoons())
            {
                MonitorMessageButton goonsButton = new("Report Goons", Icons.Material.Filled.Groups);
                goonsButton.OnClick = async () => {
                    var startedUtc = DateTime.UtcNow;
                    try
                    {
                        await TarkovDev.PostGoonsSighting(raidInfo.Map?.nameId, (DateTime)raidInfo.StartedTime, Int32.Parse(raidInfo.Profile.AccountId), GameWatcher.CurrentProfile.Type);
                        messageLog.AddMessage($"Reported Goons on {raidInfo.Map?.name}.", "info");
                    }
                    catch (Exception ex)
                    {
                        RecordException("The Goons report could not be submitted.", "TM-API-GOONS-001", "SubmitGoonsReport", ex, "TarkovDev", "Report", "https://manager.tarkov.dev/api", DiagnosticsService.ElapsedMilliseconds(startedUtc));
                    }
                    monMessage.Buttons.Remove(goonsButton);
                };
                goonsButton.Confirm = new(
                    $"Report Goons on {raidInfo.Map?.name}",
                    "<p>Submit a report only if you saw the Goons during this raid.</p><p><strong>Notice:</strong> By submitting a report, you consent to the collection of your IP address and EFT account ID for verification.</p>",
                    "Submit report", "Cancel"
                );
                goonsButton.Timeout = TimeSpan.FromMinutes(120).TotalMilliseconds;
                monMessage.Buttons.Add(goonsButton);
            }
        }

        private async void Eft_RaidExited(object? sender, RaidExitedEventArgs e)
        {
            //groupManager.Stale = true;
            runthroughTimer.Stop();
            inRaid = false;
            Stats.EndRaid(GameWatcher.CurrentProfile.Snapshot().Id, e.RaidId);
            mapsService.RequestShowDashboard();
            await ResumeMediaAfterRaid();
            var startedUtc = DateTime.UtcNow;
            try
            {
                var mapName = e.Map;
                var map = TarkovDev.Maps.Find(m => m.nameId == mapName);
                if (map != null) mapName = map.name;
                messageLog.AddMessage($"Left the {mapName} raid.", "raidleave");
            }
            catch (Exception ex)
            {
                RecordException("Raid-exit processing failed.", "TM-WATCHER-005", "RaidExited", ex, "GameWatcher", "RaidExit", durationMilliseconds: DiagnosticsService.ElapsedMilliseconds(startedUtc));
            }
        }

        private void MainBlazorUI_Resize(object sender, EventArgs e)
        {
            WindowStateChanged?.Invoke(this, EventArgs.Empty);
            var startedUtc = DateTime.UtcNow;
            try
            {
                if (this.WindowState == FormWindowState.Minimized && Properties.Settings.Default.minimizeToTray)
                {
                    Hide();
                    notifyIconTarkovMonitor.Visible = true;
                }

                if (WindowState == lastPublishedWindowState || windowStateNotificationPending)
                {
                    return;
                }

                windowStateNotificationPending = true;
                BeginInvoke(new Action(() =>
                {
                    windowStateNotificationPending = false;

                    if (IsDisposed || !IsHandleCreated || WindowState == lastPublishedWindowState)
                    {
                        return;
                    }

                    var previousWindowState = lastPublishedWindowState;
                    var nextWindowState = WindowState;
                    lastPublishedWindowState = nextWindowState;

                    if (previousWindowState != nextWindowState)
                    {
                        RefreshNormalWindowFrame();
                        ApplyWindowFrameAttributes();
                    }

                    WindowStateChanged?.Invoke(this, EventArgs.Empty);
                }));
            }
            catch (Exception ex)
            {
                RecordException("The application could not minimize to the tray.", "TM-UI-002", "MinimizeToTray", ex, "UI", "WindowState", durationMilliseconds: DiagnosticsService.ElapsedMilliseconds(startedUtc));
            }
        }

        private void notifyIconTarkovMonitor_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            var startedUtc = DateTime.UtcNow;
            try
            {
                Show();
                this.WindowState = FormWindowState.Normal;
                notifyIconTarkovMonitor.Visible = false;
            }
            catch (Exception ex)
            {
                RecordException("The application could not restore from the tray.", "TM-UI-003", "RestoreFromTray", ex, "UI", "WindowState", durationMilliseconds: DiagnosticsService.ElapsedMilliseconds(startedUtc));
            }
        }

        private void menuItemQuit_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        /*private async Task UpdatePlayerLevel()
        {
            if (!TarkovTracker.ValidToken)
            {
                return;
            }
            var level = TarkovDev.GetLevel(await TarkovDev.GetExperience(eft.AccountId));
            if (level == TarkovTracker.Progress.data.playerLevel)
            {
                return;
            }
        }*/
    }
}
