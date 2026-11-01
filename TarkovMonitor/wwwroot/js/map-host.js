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

    // While the window is dragged to a new size, the embedded page keeps its size
    // and is only stretched to the current area with a CSS transform on every frame.
    // That is smooth and cheap; the page reflows once, when the drag ends.
    const resizing = { frame: null, width: 0, height: 0 };

    function stretchFrame() {
        const frame = resizing.frame;
        if (!frame || !resizing.width || !resizing.height) {
            return;
        }
        const area = frame.parentElement;
        frame.style.transform = "scale(" + (area.clientWidth / resizing.width) + ", " + (area.clientHeight / resizing.height) + ")";
    }

    // Resize events come at most once per frame, so the stretch is applied right away;
    // waiting for the next animation frame let the map trail the window edge.
    function onResizeFrame() {
        stretchFrame();
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
                frame.style.position = "absolute";
                frame.style.left = "0";
                frame.style.top = "0";
                frame.style.width = resizing.width + "px";
                frame.style.height = resizing.height + "px";
                frame.style.transformOrigin = "0 0";
                window.addEventListener("resize", onResizeFrame);
            } else {
                window.removeEventListener("resize", onResizeFrame);
                resizing.frame = null;
                frame.style.position = "";
                frame.style.left = "";
                frame.style.top = "";
                frame.style.width = "";
                frame.style.height = "";
                frame.style.transform = "";
                frame.style.transformOrigin = "";
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
