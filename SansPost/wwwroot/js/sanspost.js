// SansPost — minimalny JS dla UI: motyw, mikroanimacja marki, natywny <dialog>, fokus.
// Żadnych danych uwierzytelnienia w storage — tylko preferencje prezentacji.
(function () {
    "use strict";

    const THEME_KEY = "sp-theme";
    const BRAND_KEY = "sp-brand-played";

    function safeGet(storage, key) {
        try { return window[storage].getItem(key); } catch { return null; }
    }

    function safeSet(storage, key, value) {
        try { window[storage].setItem(key, value); } catch { /* prywatny tryb / zablokowany storage */ }
    }

    function reducedMotion() {
        return window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    }

    // ---- Motyw ------------------------------------------------------------
    function currentTheme() {
        return document.documentElement.getAttribute("data-theme") === "dark" ? "dark" : "light";
    }

    function applyTheme(theme) {
        document.documentElement.setAttribute("data-theme", theme);
        const meta = document.querySelector('meta[name="theme-color"]');
        if (meta) meta.setAttribute("content", theme === "dark" ? "#171311" : "#ece2d0");
    }

    // Delegacja: przycisk renderuje Blazor, JS zmienia tylko atrybut na <html> (poza drzewem Blazora).
    document.addEventListener("click", function (event) {
        const toggle = event.target.closest && event.target.closest("[data-theme-toggle]");
        if (!toggle) return;
        const next = currentTheme() === "dark" ? "light" : "dark";
        applyTheme(next);
        safeSet("localStorage", THEME_KEY, next);
    });

    // Zmiana motywu systemu działa, dopóki użytkownik nie wybrał własnego.
    if (window.matchMedia) {
        window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", function (e) {
            if (!safeGet("localStorage", THEME_KEY)) applyTheme(e.matches ? "dark" : "light");
        });
    }

    // ---- Natywne formularze (logowanie/rejestracja): stan "wysyłanie" do czasu przeładowania strony.
    document.addEventListener("submit", function (event) {
        const form = event.target;
        if (!form.matches || !form.matches("[data-loading-form]") || event.defaultPrevented) return;
        const button = form.querySelector("button[type='submit'][data-loading-text]");
        if (!button) return;
        // Po bieżącym zdarzeniu — wyłączenie przycisku przed wysłaniem zablokowałoby submit.
        window.setTimeout(function () {
            button.setAttribute("aria-busy", "true");
            button.disabled = true;
            button.textContent = button.getAttribute("data-loading-text");
        }, 0);
    });

    // ---- Marka: SansPost → SandPost → SensPost → SendPost → SansPost -----
    // Raz na sesję, ~1 s, bez blokowania. Reduced motion: od razu SansPost.
    const BRAND_STEPS = [
        ["a", "d"], // SandPost
        ["e", "s"], // SensPost
        ["e", "d"], // SendPost
        ["a", "s"]  // SansPost
    ];

    function playBrand() {
        if (reducedMotion() || safeGet("sessionStorage", BRAND_KEY)) return;
        safeSet("sessionStorage", BRAND_KEY, "1");

        const word = document.querySelector("[data-brand-word]");
        if (!word) return;
        const vowel = word.querySelector("[data-brand-slot='vowel']");
        const tail = word.querySelector("[data-brand-slot='tail']");
        if (!vowel || !tail) return;

        let step = 0;
        const timer = window.setInterval(function () {
            const [v, t] = BRAND_STEPS[step];
            swap(vowel, v);
            swap(tail, t);
            step++;
            if (step >= BRAND_STEPS.length) window.clearInterval(timer);
        }, 240);
    }

    function swap(el, letter) {
        if (el.textContent === letter) return;
        el.textContent = letter;
        el.classList.remove("is-swapping");
        void el.offsetWidth; // restart animacji
        el.classList.add("is-swapping");
    }

    // ---- Dialog -----------------------------------------------------------
    function showDialog(dialog, dotnet) {
        if (!dialog || dialog.open) return;
        if (!dialog.__spBound) {
            dialog.__spBound = true;
            dialog.addEventListener("close", function () {
                if (dialog.__spDotnet) dialog.__spDotnet.invokeMethodAsync("OnDialogClosed").catch(function () { });
            });
            // Klik w tło (poza panelem) zamyka dialog.
            dialog.addEventListener("click", function (event) {
                if (event.target === dialog) dialog.close();
            });
        }
        dialog.__spDotnet = dotnet;
        dialog.showModal();
    }

    function closeDialog(dialog) {
        if (dialog && dialog.open) dialog.close();
    }

    // ---- Fokus ------------------------------------------------------------
    function focusSelector(selector) {
        const el = document.querySelector(selector);
        if (!el) return;
        if (!el.hasAttribute("tabindex") && !/^(A|BUTTON|INPUT|SELECT|TEXTAREA)$/.test(el.tagName)) {
            el.setAttribute("tabindex", "-1");
        }
        el.focus({ preventScroll: false });
    }

    // ---- Liczniki znaków (tylko prezentacja) --------------------------------
    // Pola są wiązane zdarzeniem "change" — licznik podczas pisania liczy przeglądarka. Walidacja zostaje na serwerze.
    function formatCount(n) {
        return String(n).replace(/\B(?=(\d{3})+(?!\d))/g, " "); // jak "N0" w pl-PL: 1 000
    }

    document.addEventListener("input", function (event) {
        const field = event.target;
        if (!field.id || !window.CSS) return;
        const counter = document.querySelector(".counter[data-counter-for='" + CSS.escape(field.id) + "']");
        if (!counter) return;
        const max = Number(counter.getAttribute("data-max"));
        const length = field.value.length;
        const value = counter.querySelector("[data-counter-value]");
        if (value) value.textContent = formatCount(length);
        counter.classList.toggle("is-over", length > max);
        counter.classList.toggle("is-near", length <= max && length >= max * 0.9);
    });

    // ---- Czas: pełna data w strefie przeglądarki ----------------------------
    // Serwer i baza pracują w UTC; <time datetime="…Z"> dostaje w podpowiedzi datę lokalną (Intl), zamiast "… UTC".
    const fullDate = window.Intl ? new Intl.DateTimeFormat("pl-PL", { dateStyle: "long", timeStyle: "short" }) : null;

    function localizeTime(el) {
        if (!fullDate || !el || el.tagName !== "TIME") return;
        const value = el.getAttribute("datetime");
        if (!value || el.__spLocal === value) return;
        const date = new Date(value);
        if (isNaN(date.getTime())) return;
        el.__spLocal = value;
        el.setAttribute("title", fullDate.format(date));
    }

    function localizeTimes(root) {
        (root || document).querySelectorAll("time[datetime]").forEach(localizeTime);
    }

    // Elementy dorenderowane później (doładowanie, nawigacja) — w chwili wskazania.
    document.addEventListener("pointerover", function (event) {
        localizeTime(event.target.closest && event.target.closest("time[datetime]"));
    });

    // Skip link: router Blazora przechwytuje "#main" jako nawigację i fokus zostaje na linku. Obsługa w fazie
    // przechwytywania (przed routerem): fokus faktycznie trafia do <main tabindex="-1">, adres się nie zmienia.
    window.addEventListener("click", function (event) {
        const link = event.target.closest && event.target.closest("a.skip-link[href^='#']");
        if (!link) return;
        const target = document.getElementById(link.getAttribute("href").slice(1));
        if (!target) return;
        event.preventDefault();
        event.stopPropagation();
        if (!target.hasAttribute("tabindex")) target.setAttribute("tabindex", "-1");
        target.focus({ preventScroll: true });
        target.scrollIntoView({ block: "start", behavior: "auto" });
    }, true);

    function scrollToId(id) {
        // Po FocusOnNavigate (fokus na h1) — dlatego w następnej klatce.
        window.requestAnimationFrame(function () {
            const el = document.getElementById(id);
            if (el) el.scrollIntoView({ block: "start", behavior: reducedMotion() ? "auto" : "smooth" });
        });
    }

    // ---- Poziomy pasek (kategorie) ----------------------------------------
    // Strzałki: stan wyłącznie z pomiarów (scrollLeft, clientWidth, scrollWidth) — bez założeń o szerokości elementów.
    function updateScroller(strip) {
        const track = strip.querySelector("[data-scroller-track]");
        if (!track) return;
        const max = track.scrollWidth - track.clientWidth;
        const left = strip.querySelector("[data-scroll='-1']");
        const right = strip.querySelector("[data-scroll='1']");
        const canLeft = track.scrollLeft > 1;
        const canRight = track.scrollLeft < max - 1;
        if (left) left.disabled = !canLeft;
        if (right) right.disabled = !canRight;
        strip.toggleAttribute("data-overflowing", max > 1);
    }

    function bindScroller(strip) {
        if (!strip) return;
        const track = strip.querySelector("[data-scroller-track]");
        if (!track) return;

        if (!strip.__spBound) {
            strip.__spBound = true;
            track.addEventListener("scroll", function () { updateScroller(strip); }, { passive: true });
            if (window.ResizeObserver) {
                const observer = new ResizeObserver(function () { updateScroller(strip); });
                observer.observe(track);
                strip.__spObserver = observer;
            } else {
                window.addEventListener("resize", function () { updateScroller(strip); });
            }

            // Aktywny szyld (np. bieżąca kategoria na końcu listy) od razu w widoku.
            const active = track.querySelector("[aria-current='page']");
            if (active && active.offsetLeft + active.offsetWidth > track.clientWidth) {
                track.scrollLeft = active.offsetLeft - track.clientWidth / 2 + active.offsetWidth / 2;
            }
        }

        updateScroller(strip);
    }

    document.addEventListener("click", function (event) {
        const button = event.target.closest && event.target.closest("[data-scroller] [data-scroll]");
        if (!button || button.disabled) return;
        const strip = button.closest("[data-scroller]");
        const track = strip.querySelector("[data-scroller-track]");
        const direction = Number(button.getAttribute("data-scroll"));
        track.scrollBy({ left: direction * Math.max(120, track.clientWidth * 0.7), behavior: reducedMotion() ? "auto" : "smooth" });
        // Stan aktualizuje zdarzenie "scroll"; dodatkowo po zakończeniu płynnego przewijania.
        // Gdy strzałka z fokusem stała się nieaktywna (koniec listy) — fokus na przeciwną, nie na <body>.
        window.setTimeout(function () {
            updateScroller(strip);
            if (button.disabled && document.activeElement === button) {
                const other = strip.querySelector("[data-scroll='" + (-direction) + "']");
                if (other && !other.disabled) other.focus();
            }
        }, 400);
    });

    // Znacznik "circuit podłączony" (po pierwszym interaktywnym renderze layoutu) — dla testów E2E i diagnostyki.
    function markInteractive() {
        document.documentElement.setAttribute("data-interactive", "1");
        localizeTimes(document);
    }

    window.sansPost = {
        markInteractive: markInteractive,
        bindScroller: bindScroller,
        scrollToId: scrollToId,
        playBrand: playBrand,
        showDialog: showDialog,
        closeDialog: closeDialog,
        focusSelector: focusSelector
    };
})();
