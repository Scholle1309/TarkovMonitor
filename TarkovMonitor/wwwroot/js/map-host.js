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

    // While the window is dragged to a new size, the embedded page keeps its size and
    // scale and stays centred in the current area; nothing is stretched or reflowed.
    // Leaflet keeps the map centre when it is resized at the end, so the map content
    // stays where it was and only the visible part grows or shrinks.
    const resizing = { frame: null, width: 0, height: 0 };

    // The app's own buttons and panels over the map.
    const hostOverlays = ".map-host__fab, .map-tasks";

    function centreFrame() {
        const frame = resizing.frame;
        if (!frame) {
            return;
        }
        const area = frame.parentElement;
        const left = Math.round((area.clientWidth - resizing.width) / 2);
        const top = Math.round((area.clientHeight - resizing.height) / 2);
        frame.style.left = left + "px";
        frame.style.top = top + "px";
        // Smaller window: the page is cropped, its controls move in to the visible edges.
        post(frame.id, { type: "tarkov-monitor-resizing", on: true, x: Math.max(0, -left), y: Math.max(0, -top) });
        // Larger window: the page cannot draw beyond itself, so the app's buttons move
        // in to the page edges and both groups stay together.
        const x = Math.max(0, left), y = Math.max(0, top);
        document.querySelectorAll(hostOverlays).forEach(element => { element.style.translate = (-x) + "px " + (-y) + "px"; });
    }

    return {
        // Called when a window resize starts and ends (see MainBlazorUI.OnResizeBegin).
        freeze(on) {
            const frame = document.querySelector(".map-host__frame iframe");
            if (!frame) {
                return;
            }
            if (on) {
                resizing.frame = frame;
                resizing.width = frame.clientWidth;
                resizing.height = frame.clientHeight;
                frame.parentElement.style.overflow = "hidden";
                frame.style.position = "absolute";
                frame.style.width = resizing.width + "px";
                frame.style.height = resizing.height + "px";
                centreFrame();
                window.addEventListener("resize", centreFrame);
            } else {
                window.removeEventListener("resize", centreFrame);
                resizing.frame = null;
                frame.parentElement.style.overflow = "";
                frame.style.position = "";
                frame.style.left = "";
                frame.style.top = "";
                frame.style.width = "";
                frame.style.height = "";
                post(frame.id, { type: "tarkov-monitor-resizing", on: false });
                document.querySelectorAll(hostOverlays).forEach(element => { element.style.translate = ""; });
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
