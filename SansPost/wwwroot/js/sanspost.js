// SansPost — minimalny JS dla UI: motyw, natywny <dialog>, fokus, skip link, liczniki znaków, czas lokalny, pasek kategorii.
// Żadnych danych uwierzytelnienia w storage — tylko preferencje prezentacji.
(function () {
    "use strict";

    const THEME_KEY = "sp-theme";

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

    // "Odśwież stronę" w oknie ponownego łączenia (_Host) — bez inline onclick, który blokuje CSP.
    document.addEventListener("click", function (event) {
        if (event.target.closest && event.target.closest("[data-reload]")) location.reload();
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

    // ---- Dialog -----------------------------------------------------------
    // managed: o zamknięciu decyduje właściciel (np. BAR z poziomami — Escape cofa o poziom). Escape, klik w tło
    // i żądanie zamknięcia systemu (np. gest "wstecz") trafiają do OnDialogDismiss zamiast zamykać dialog.
    function showDialog(dialog, dotnet, managed, generation) {
        if (!dialog) return;
        dialog.__spGen = generation;   // także gdy okno jest już otwarte — jego zamknięcie dotyczy tego otwarcia
        if (dialog.open) return;
        if (!dialog.__spBound) {
            dialog.__spBound = true;
            const dismiss = function (reason) {
                if (dialog.__spDotnet) dialog.__spDotnet.invokeMethodAsync("OnDialogDismiss", reason).catch(function () { });
            };
            dialog.addEventListener("close", function () {
                // Zdarzenie "close" przychodzi jako osobne zadanie — jeśli okno zdążyło się już ponownie otworzyć
                // (np. Escape i od razu Enter na przycisku otwierającym), dotyczy poprzedniego otwarcia: pomijamy.
                if (dialog.open) return;
                if (dialog.__spDotnet) dialog.__spDotnet.invokeMethodAsync("OnDialogClosed", dialog.__spGen || 0).catch(function () { });
            });
            // Klik w tło (poza panelem) zamyka dialog.
            dialog.addEventListener("click", function (event) {
                if (event.target !== dialog) return;
                if (dialog.__spManaged) dismiss("backdrop"); else dialog.close();
            });
            // Escape obsłużony już przy keydown — przeglądarka nie wysyła wtedy żądania zamknięcia (także przy
            // kolejnych naciśnięciach bez innej interakcji, których "cancel" nie dałoby się już zatrzymać).
            dialog.addEventListener("keydown", function (event) {
                if (!dialog.__spManaged || event.key !== "Escape" || event.defaultPrevented) return;
                // Escape w oknie zagnieżdżonym (zgłoszenie, usunięcie komentarza) należy do niego.
                const target = event.target;
                if (target && target.closest && target.closest("dialog") !== dialog) return;
                event.preventDefault();
                // Pisany tekst (komentarz, edycja) nie znika przez przypadkowy Escape — okno zostaje na tym poziomie.
                if (target && ((target.tagName === "TEXTAREA" && target.value.trim()) || target.isContentEditable
                    || (target.tagName === "INPUT" && /^(text|email|password)$/.test(target.type) && target.value))) return;
                dismiss("escape");
            });
            dialog.addEventListener("cancel", function (event) {
                if (!dialog.__spManaged || !event.cancelable) return;
                event.preventDefault();
                dismiss("escape");
            });
        }
        dialog.__spDotnet = dotnet;
        dialog.__spManaged = !!managed;
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

    // ---- Wejście do Saloonu (Entrance) -----------------------------------
    // Czas otwarcia drzwi: 0 przy reduced motion (od razu /saloon), pełny przy pierwszym wejściu w sesji, krótszy
    // przy kolejnych. Tylko sessionStorage — Entrance nie znika na stałe.
    const SALOON_ENTERED_KEY = "sp-saloon-entered";

    function saloonEntryDuration() {
        if (reducedMotion()) return 0;
        const repeat = safeGet("sessionStorage", SALOON_ENTERED_KEY);
        safeSet("sessionStorage", SALOON_ENTERED_KEY, "1");
        return repeat ? 600 : 1500;
    }

    // Parallax 2.5D od wskaźnika: tylko mysz/trackpad (hover + fine pointer), bez reduced motion. Zapis --px/--py
    // (zakres -1..1) raz na klatkę. Ruszają się tylko warstwy tła (niebo, chmury, dalekie miasteczko) przez CSS
    // `translate`; fasada wyłącznie o 2 px w poziomie, podłoże wcale. JS nigdy nie ustawia `transform` —
    // animacje wejścia (kamera, drzwi) mają własne `transform`. Podczas wejścia parallax jest zamrożony.
    function bindEntranceParallax(scene) {
        if (!scene || scene.__spParallax || reducedMotion()) return;
        if (!window.matchMedia || !window.matchMedia("(hover: hover) and (pointer: fine)").matches) return;
        scene.__spParallax = true;
        let frame = 0, x = 0, y = 0;
        // Kursor poza sceną → powrót do położenia spoczynkowego.
        scene.addEventListener("pointerleave", function () {
            if (scene.classList.contains("is-entering")) return;
            scene.style.removeProperty("--px");
            scene.style.removeProperty("--py");
        }, { passive: true });
        scene.addEventListener("pointermove", function (event) {
            if (scene.classList.contains("is-entering")) return;
            const box = scene.getBoundingClientRect();
            x = ((event.clientX - box.left) / box.width) * 2 - 1;
            y = ((event.clientY - box.top) / box.height) * 2 - 1;
            if (frame) return;
            frame = window.requestAnimationFrame(function () {
                frame = 0;
                scene.style.setProperty("--px", x.toFixed(3));
                scene.style.setProperty("--py", y.toFixed(3));
            });
        }, { passive: true });
    }

    // ---- Saloon Main Hall ---------------------------------------------------
    // Gość przeszedł właśnie przez drzwi Entrance 3D (znacznik z entrance3d.enter) — sala zaczyna od kadru progu.
    function takeDoorArrival() {
        const at = Number(safeGet("sessionStorage", "sp-through-door"));
        try { sessionStorage.removeItem("sp-through-door"); } catch { /* bez storage */ }
        return at > 0 && Date.now() - at < 15000;
    }

    // ---- BAR: historia -------------------------------------------------------
    // Poziomy BAR to wpisy historii (BarNavigation): "w górę" = cofnięcie o jeden wpis, zamknięcie = o kilka.
    function historyGo(delta) {
        history.go(delta);
    }

    // Link do rozmowy kliknięty w oknie BAR: zamiast wyjścia na /post-view/{id} — poziom "rozmowa" w tym samym oknie;
    // link do /login lub /register — karta logowania / rejestracji w tym samym oknie (klasyczne strony tylko wprost).
    // Zwykły klik (bez Ctrl/Shift/Alt/Meta, lewy przycisk, bez target) — reszta (nowa karta, kopiowanie) zostaje
    // natywna i daje klasyczny, udostępnialny adres. Celowo nie przez LocationChanging Blazora: zarejestrowany
    // handler sprawia, że Blazor cofa i ponawia każde "wstecz"/"dalej" przeglądarki (dodatkowe popstate), co ścigało
    // się z kolejną nawigacją użytkownika.
    function bindBarLinks(dotnet) {
        const handler = function (event) {
            if (event.defaultPrevented || event.button !== 0 || event.ctrlKey || event.shiftKey || event.altKey || event.metaKey) return;
            const link = event.target && event.target.closest ? event.target.closest("a[href]") : null;
            if (!link || link.target || !link.closest("dialog.bar-panel[open]")) return;
            const url = new URL(link.href, document.baseURI);
            if (url.origin !== location.origin) return;
            const post = /^\/(?:post-view|p)\/(\d+)\/?$/.exec(url.pathname);
            const auth = /^\/(login|register)\/?$/.exec(url.pathname);
            if (!post && !auth) return;
            event.preventDefault();
            event.stopPropagation();
            if (post) {
                dotnet.invokeMethodAsync("OpenConversation", Number(post[1])).catch(function () { });
                return;
            }
            // Logowanie / rejestracja z BAR: karta w oknie; przycisk zapamiętany — po powrocie wraca na niego fokus.
            document.querySelectorAll("[data-auth-return]").forEach(function (el) { el.removeAttribute("data-auth-return"); });
            link.setAttribute("data-auth-return", "");
            dotnet.invokeMethodAsync("OpenAuth", auth[1], url.searchParams.get("returnUrl")).catch(function () { });
        };
        document.addEventListener("click", handler, true);
        return { dispose: function () { document.removeEventListener("click", handler, true); } };
    }

    // Nowy poziom w oknie (BAR): okno od góry, fokus na nagłówek poziomu (bez przewijania pod przyklejony nagłówek okna).
    // Powrót do Karty rozmów (top = false): fokus na pozycję, z której wyszliśmy — przewinięta do widoku.
    function focusInDialog(selector, top) {
        const el = document.querySelector(selector);
        if (!el) return;
        // Przewija się treść okna (.dialog-body), nie samo okno — zerujemy właściwy kontener.
        const scroller = el.closest(".dialog-body") || el.closest("dialog");
        if (top && scroller) scroller.scrollTop = 0;
        el.focus({ preventScroll: !!top });
        if (!top) el.scrollIntoView({ block: "center" });
    }

    // Jak wyżej, ale element może się jeszcze wczytywać (tytuł rozmowy po pobraniu posta): czekamy do 4 s,
    // potem fallback. Nowe wywołanie anuluje poprzednie (szybkie przejścia między poziomami bez "skaczącego" fokusu).
    let focusToken = 0;
    function focusInDialogWhenReady(selector, fallback, top) {
        const token = ++focusToken;
        const started = performance.now();
        const attempt = function () {
            if (token !== focusToken) return;
            const el = document.querySelector(selector);
            if (el && el.getClientRects().length) { focusInDialog(selector, top); return; }
            if (performance.now() - started > 4000) { if (fallback) focusInDialog(fallback, top); return; }
            requestAnimationFrame(attempt);
        };
        attempt();
    }

    // Fokus w polu z kursorem na końcu (pisanie trwa dalej po przejściu Karta rozmów → wyniki).
    function focusInputEnd(selector) {
        const el = document.querySelector(selector);
        if (!el) return;
        el.focus({ preventScroll: true });
        try { el.setSelectionRange(el.value.length, el.value.length); } catch { /* pole bez zaznaczenia */ }
    }

    // Onboarding sali: pełny pasek stref do pierwszego świadomego wyboru strefy w tej sesji, potem kompaktowy.
    function hallOnboarded(done) {
        if (done) safeSet("sessionStorage", "sp-hall-onboarded", "1");
        return safeGet("sessionStorage", "sp-hall-onboarded") === "1";
    }

    // ---- Karty logowania i rejestracji na Entrance ------------------------
    // Ten sam natywny POST co strony /login i /register (antiforgery, rate limiting, cookie ustawia AccountController),
    // tylko wysłany przez fetch: błąd zostaje w karcie, a po sukcesie najpierw otwierają się drzwi. Wynik to adres
    // końcowy po przekierowaniach (np. /saloon albo /login?error=invalid) — ta sama umowa co przy zwykłym formularzu.
    async function postForm(url, body) {
        try {
            const response = await fetch(url, { method: "POST", body: body, credentials: "same-origin", redirect: "follow" });
            const end = new URL(response.url);
            return { status: response.status, path: end.pathname, query: end.search };
        } catch {
            return { status: 0, path: "", query: "" };
        }
    }

    async function submitAuthForm(form) {
        return Object.assign({ stage: "login" }, await postForm(form.action, new FormData(form)));
    }

    // Rejestracja, a po sukcesie (przekierowanie na /login?registered=1) logowanie tymi samymi danymi —
    // dwa istniejące kroki backendu jeden po drugim, bez nowego endpointu.
    async function registerAndSignIn(form) {
        const registered = await postForm(form.action, new FormData(form));
        if (registered.path !== "/login" || !/[?&]registered=/.test(registered.query))
            return Object.assign({ stage: "register" }, registered);

        const body = new FormData();
        body.set("__RequestVerificationToken", form.elements["__RequestVerificationToken"].value);
        body.set("Email", form.elements["Email"].value);
        body.set("Password", form.elements["Password"].value);
        body.set("ReturnUrl", form.getAttribute("data-return-url") || "/saloon");   // serwer i tak przyjmie tylko adres lokalny
        return Object.assign({ stage: "login" }, await postForm(new URL("auth/login", document.baseURI).href, body));
    }

    // Znacznik "circuit podłączony" (po pierwszym interaktywnym renderze layoutu) — dla testów E2E i diagnostyki.
    function markInteractive() {
        document.documentElement.setAttribute("data-interactive", "1");
        localizeTimes(document);
    }

    window.sansPost = {
        markInteractive: markInteractive,
        saloonEntryDuration: saloonEntryDuration,
        bindEntranceParallax: bindEntranceParallax,
        takeDoorArrival: takeDoorArrival,
        historyGo: historyGo,
        focusInputEnd: focusInputEnd,
        bindBarLinks: bindBarLinks,
        focusInDialog: focusInDialog,
        focusInDialogWhenReady: focusInDialogWhenReady,
        // Język interfejsu (Sprint 22): cookie na rok (SameSite=Lax) i atrybut lang dokumentu; treść przełącza Blazor w miejscu.
        setLanguage: function (language) {
            const value = language === "en" ? "en" : "pl";
            document.cookie = "sp-lang=" + value + "; path=/; max-age=31536000; samesite=lax";
            document.documentElement.lang = value;
            window.dispatchEvent(new CustomEvent("sansPost:language", { detail: { language: value } }));
        },
        hallOnboarded: hallOnboarded,
        submitAuthForm: submitAuthForm,
        registerAndSignIn: registerAndSignIn,
        bindScroller: bindScroller,
        scrollToId: scrollToId,
        showDialog: showDialog,
        closeDialog: closeDialog,
        focusSelector: focusSelector
    };
})();
