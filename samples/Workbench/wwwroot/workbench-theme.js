// Workbench theme manager: provides toggle pushbuttons with browser preference defaulting
// and local persistence across all Workbench pages.

const STORAGE_KEY = "workbench-theme";
const mediaQuery = typeof window !== "undefined" && window.matchMedia ? window.matchMedia("(prefers-color-scheme: dark)") : null;

/** Returns the initial theme based on stored user choice or current browser preference. */
export function getInitialTheme() {
    try {
        const stored = localStorage.getItem(STORAGE_KEY);
        if (stored === "dark" || stored === "light") return stored;
    } catch {}
    return mediaQuery?.matches ? "dark" : "light";
}

/** Applies the requested theme ('dark' or 'light') to the document and report elements. */
export function applyTheme(theme, persist = false) {
    document.documentElement.setAttribute("data-theme", theme);
    document.documentElement.style.colorScheme = theme;

    // Update radio buttons if present
    const radio = document.querySelector(`input[name="workbench-theme"][value="${theme}"]`);
    if (radio) radio.checked = true;

    // Update all interactive-report and interactive-report-admin components on the page
    document.querySelectorAll("interactive-report, interactive-report-admin").forEach(el => {
        el.setAttribute("theme", theme);
        try { el.theme = theme; } catch {}
    });

    if (persist) {
        try { localStorage.setItem(STORAGE_KEY, theme); } catch {}
    }
}

/** Initializes the Workbench theme toggle listeners and observers. */
export function initWorkbenchTheme() {
    const theme = getInitialTheme();
    applyTheme(theme, false);

    // Ensure elements receive the theme once custom elements are registered/upgraded
    if (typeof customElements !== "undefined") {
        Promise.allSettled([
            customElements.whenDefined("interactive-report"),
            customElements.whenDefined("interactive-report-admin"),
        ]).then(() => {
            const current = document.documentElement.getAttribute("data-theme");
            if (current) {
                document.querySelectorAll("interactive-report, interactive-report-admin").forEach(el => {
                    el.setAttribute("theme", current);
                    try { el.theme = current; } catch {}
                });
            }
        });
    }

    // Bind change listeners to theme radio inputs
    document.querySelectorAll('input[name="workbench-theme"]').forEach(radio => {
        radio.addEventListener("change", e => {
            if (e.target.checked) {
                applyTheme(e.target.value, true);
            }
        });
    });

    // Listen to browser/OS preference changes when no explicit choice has been stored
    mediaQuery?.addEventListener?.("change", e => {
        let hasStored = false;
        try { hasStored = Boolean(localStorage.getItem(STORAGE_KEY)); } catch {}
        if (!hasStored) {
            applyTheme(e.matches ? "dark" : "light", false);
        }
    });

    // Ensure dynamically added elements inherit the active theme
    const observer = new MutationObserver(mutations => {
        for (const mutation of mutations) {
            for (const node of mutation.addedNodes) {
                if (node.nodeType === 1) {
                    const current = document.documentElement.getAttribute("data-theme");
                    if (!current) continue;
                    if (node.matches?.("interactive-report, interactive-report-admin")) {
                        node.setAttribute("theme", current);
                        try { node.theme = current; } catch {}
                    }
                    node.querySelectorAll?.("interactive-report, interactive-report-admin").forEach(el => {
                        el.setAttribute("theme", current);
                        try { el.theme = current; } catch {}
                    });
                }
            }
        }
    });
    if (document.body) {
        observer.observe(document.body, { childList: true, subtree: true });
    }
}

// Auto-initialize when script loads
if (typeof document !== "undefined") {
    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", () => initWorkbenchTheme());
    } else {
        initWorkbenchTheme();
    }
}
