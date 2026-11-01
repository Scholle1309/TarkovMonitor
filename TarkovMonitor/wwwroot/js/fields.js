// Settings fields (.tm-field) frame a caption and a MudBlazor control. A click on the
// caption, the padding or the frame should act like a click on the control itself:
// a select opens its list, a text or date field gets the cursor. MudBlazor only opens a
// select on a real pointer event, but it also opens on Enter while focused, so the
// click is passed on as focus plus Enter, after the click has finished; opening on
// mousedown would let the following click close the list again at once.
(() => {
    function fieldOf(event) {
        if (event.button !== 0) {
            return null;
        }
        const field = event.target.closest(".tm-field");
        // Clicks on the control, its buttons or a slider keep their own behaviour.
        if (!field || event.target.closest(".mud-input-control, .mud-input-adornment, button, .mud-slider, a")) {
            return null;
        }
        return field;
    }

    // Keep the focus where it is until the click is complete.
    document.addEventListener("mousedown", (event) => {
        if (fieldOf(event)) {
            event.preventDefault();
        }
    }, true);

    document.addEventListener("click", (event) => {
        const field = fieldOf(event);
        if (!field) {
            return;
        }
        const input = field.querySelector(".mud-input-control input");
        if (!input) {
            return;
        }
        setTimeout(() => {
            input.focus();
            if (field.querySelector(".mud-select")) {
                input.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", code: "Enter", bubbles: true }));
            }
        }, 0);
    }, true);
})();
