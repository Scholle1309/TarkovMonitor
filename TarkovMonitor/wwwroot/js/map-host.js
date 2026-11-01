// Bridge between the Maps tab and the embedded Tarkov.dev page. The page is
// cross-origin, so requests are passed with postMessage; a script injected
// into the page (see MainBlazorUI) reacts to them.
window.mapHost = (() => {
    const targetOrigin = "https://tarkov.dev";

    function post(frameId, message) {
        const frame = document.getElementById(frameId);
        if (!frame || !frame.contentWindow) {
            return false;
        }
        frame.contentWindow.postMessage(message, targetOrigin);
        return true;
    }

    const resizing = { host: null, timer: 0 };

    function applyHostSize(host) {
        const area = host.parentElement;
        host.style.width = area.clientWidth + "px";
        host.style.height = area.clientHeight + "px";
    }

    function onResizeStep() {
        if (resizing.timer) {
            return;
        }
        resizing.timer = setTimeout(() => {
            resizing.timer = 0;
            if (resizing.host) {
                applyHostSize(resizing.host);
            }
        }, 100);
    }

    return {
        // While the window is dragged to a new size, the map area follows in steps of
        // about 100 ms instead of on every frame: it still grows and shrinks with the
        // window, but the embedded page reflows a few times a second, not 60 times.
        freeze(on) {
            const host = document.querySelector(".map-host");
            if (!host) {
                return;
            }
            if (on) {
                resizing.host = host;
                host.style.right = "auto";
                host.style.bottom = "auto";
                applyHostSize(host);
                window.addEventListener("resize", onResizeStep);
            } else {
                window.removeEventListener("resize", onResizeStep);
                clearTimeout(resizing.timer);
                resizing.timer = 0;
                resizing.host = null;
                host.style.right = "";
                host.style.bottom = "";
                host.style.width = "";
                host.style.height = "";
            }
        },
        // Highlight the markers of a task on the map (empty text clears the highlight).
        search(frameId, text) {
            return post(frameId, { type: "tarkov-monitor-search", text: text || "" });
        },
        // Let the player position marker pulse for the given time.
        pulsePosition(frameId, durationMs) {
            return post(frameId, { type: "tarkov-monitor-position", duration: durationMs || 5000 });
        },
        // Close the layer, settings and search panels of the page (one panel at a time).
        closePanels(frameId) {
            return post(frameId, { type: "tarkov-monitor-close-panels" });
        },
        // Own markers of the shown map, drawn by the page.
        setMarkers(frameId, markers) {
            return post(frameId, { type: "tarkov-monitor-markers", markers: markers || [] });
        },
        // The next click on the map places a marker.
        setPlacing(frameId, on) {
            return post(frameId, { type: "tarkov-monitor-place", on: !!on });
        },
        focusMarker(frameId, id) {
            return post(frameId, { type: "tarkov-monitor-focus-marker", id });
        },
        // The page reports panel openings, a new map instance, a placed marker and marker edits.
        register(dotNetRef) {
            window.addEventListener("message", (event) => {
                if (event.origin !== targetOrigin || !event.data) {
                    return;
                }
                switch (event.data.type) {
                    case "tarkov-monitor-panel-opened":
                        dotNetRef.invokeMethodAsync("OnFramePanelOpened");
                        break;
                    case "tarkov-monitor-ready":
                        dotNetRef.invokeMethodAsync("OnFrameMapReady", event.data.map || "");
                        break;
                    case "tarkov-monitor-placed":
                        dotNetRef.invokeMethodAsync("OnFramePlaced");
                        break;
                    case "tarkov-monitor-marker":
                        dotNetRef.invokeMethodAsync("OnFrameMarker", event.data.action || "", JSON.stringify(event.data.marker || {}));
                        break;
                }
            });
        }
    };
})();
