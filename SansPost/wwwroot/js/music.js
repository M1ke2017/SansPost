// SansPost — radio w Saloonie (Sprint 18). Jeden właściciel audio na całą sesję strony: jeden element <audio>
// (tworzony dopiero przy pierwszym "Graj"), jeden zestaw nasłuchów na dokumencie. Komponenty Blazor (Kącik muzyczny,
// mini-player) tylko rysują stan — subskrybują go i znikają bez śladu; nawigacja po Saloonie (sala, BAR, Wanted)
// nie przerywa muzyki.
//
// Sterowanie przez atrybuty w HTML (delegacja zdarzeń, obsługa od razu w geście użytkownika — bez obchodzenia
// polityki autoplay i bez podróży do serwera):
//   [data-music-toggle] — Graj / Pauza (stacja z data-station-*),  [data-music-station] — wybór stacji,
//   [data-music-retry] — ponów bieżącą stację,  input[data-music-volume] — głośność 0–100.
// Pauza zatrzymuje strumień (bez pobierania w tle); "Graj" łączy się ze stacją od nowa — to radio na żywo.
// Audio z adresu stacji gra przeglądarka bezpośrednio (serwer SansPost tylko podaje listę stacji).
(function () {
    "use strict";

    const state = { station: null, status: "idle", volume: 70, started: false };
    const subscribers = new Map();
    let nextId = 1;
    let audio = null;
    const debug = { status: "idle", audioCreated: 0, audioListeners: 0, documentListeners: 0, subscribers: 0, playRequests: 0 };

    function safeGetVolume() {
        try {
            const stored = Number(localStorage.getItem("sp-radio-volume"));
            return Number.isFinite(stored) && stored >= 0 && stored <= 100 && localStorage.getItem("sp-radio-volume") !== null ? stored : 70;
        } catch { return 70; }
    }

    function snapshot() {
        const s = state.station;
        return {
            stationId: s ? s.id : null, name: s ? s.name : null, country: s ? s.country : null,
            codec: s ? s.codec : null, bitrate: s ? s.bitrate : null, homepage: s ? s.homepage : null, streamUrl: s ? s.url : null,
            status: state.status, volume: state.volume, started: state.started
        };
    }

    function publish() {
        const s = snapshot();
        subscribers.forEach(function (dotnet) { dotnet.invokeMethodAsync("OnMusicState", s).catch(function () { }); });
        window.dispatchEvent(new CustomEvent("sansPost:music", { detail: s }));
        Object.assign(debug, { status: state.status, stationId: s.stationId, subscribers: subscribers.size, src: audio ? audio.getAttribute("src") : null });
    }

    function setStatus(status) {
        if (state.status === status) return;
        state.status = status;
        publish();
    }

    function listen(target, type, handler) {
        target.addEventListener(type, handler);
        if (target === audio) debug.audioListeners++; else debug.documentListeners++;
    }

    // Jeden element audio (poza drzewem Blazora) — tworzony przy pierwszym "Graj", potem tylko zmienia się src.
    function ensureAudio() {
        if (audio) return audio;
        audio = document.createElement("audio");
        audio.id = "sp-radio";
        audio.preload = "none";
        audio.hidden = true;
        audio.volume = state.volume / 100;
        document.body.appendChild(audio);
        debug.audioCreated++;
        listen(audio, "playing", function () { if (audio.getAttribute("src")) setStatus("playing"); });
        listen(audio, "waiting", function () { if (state.status === "playing") setStatus("loading"); });
        // Błąd strumienia albo koniec transmisji: komunikat i wybór użytkownika (ponów / następna) — bez pętli ponowień.
        listen(audio, "error", function () { if (audio.getAttribute("src")) fail(); });
        listen(audio, "ended", function () { if (audio.getAttribute("src")) fail(); });
        return audio;
    }

    function stopStream() {
        if (!audio) return;
        audio.pause();
        if (audio.hasAttribute("src")) {
            audio.removeAttribute("src");
            audio.load();   // zrywa połączenie ze stacją (pauza w radiu na żywo nie buforuje w tle)
        }
    }

    function fail() {
        stopStream();
        setStatus("error");
    }

    function stationFrom(el) {
        const url = el.getAttribute("data-station-url") || "";
        let parsed;
        try { parsed = new URL(url); } catch { return null; }
        if (parsed.protocol !== "https:") return null;
        const bitrate = Number(el.getAttribute("data-station-bitrate"));
        return {
            id: el.getAttribute("data-station-id") || url,
            name: el.getAttribute("data-station-name") || "Radio country",
            country: el.getAttribute("data-station-country") || null,
            codec: el.getAttribute("data-station-codec") || null,
            bitrate: bitrate > 0 ? bitrate : null,
            homepage: el.getAttribute("data-station-homepage") || null,
            url: parsed.href
        };
    }

    // Tylko z gestu użytkownika (Graj, ponów, zmiana stacji w trakcie grania).
    function play(station) {
        if (station) state.station = station;
        if (!state.station) return;
        const a = ensureAudio();
        stopStream();
        a.volume = state.volume / 100;
        a.setAttribute("src", state.station.url);
        state.started = true;
        state.status = "loading";
        debug.playRequests++;
        publish();
        const attempt = a.play();
        if (attempt && attempt.catch) {
            attempt.catch(function (error) {
                // AbortError — przerwane przez kolejną zmianę (pauza, inna stacja); to nie błąd stacji.
                if (error && error.name === "AbortError") return;
                if (a.getAttribute("src") === state.station.url) fail();
            });
        }
    }

    function pause() {
        stopStream();
        setStatus(state.started ? "paused" : "idle");
    }

    // Zmiana stacji: w trakcie grania — od razu nowa stacja; zatrzymane radio zostaje zatrzymane (bez dźwięku bez "Graj").
    function select(station) {
        const active = state.status === "playing" || state.status === "loading";
        if (active) { play(station); return; }
        stopStream();
        state.station = station;
        state.status = state.started ? "paused" : "idle";
        publish();
    }

    function setVolume(value, final) {
        const volume = Math.max(0, Math.min(100, Math.round(Number(value) || 0)));
        state.volume = volume;
        if (audio) audio.volume = volume / 100;
        document.querySelectorAll("input[data-music-volume]").forEach(function (input) { if (Number(input.value) !== volume) input.value = volume; });
        if (final) {
            try { localStorage.setItem("sp-radio-volume", String(volume)); } catch { /* bez pamięci przeglądarki */ }
            publish();
        }
    }

    listen(document, "click", function (event) {
        const target = event.target && event.target.closest ? event.target.closest("[data-music-toggle], [data-music-station], [data-music-retry]") : null;
        if (!target || target.disabled) return;
        if (target.hasAttribute("data-music-toggle")) {
            if (state.status === "playing" || state.status === "loading") pause();
            else play(stationFrom(target) || state.station);
        } else if (target.hasAttribute("data-music-station")) {
            const station = stationFrom(target);
            if (station && (!state.station || station.id !== state.station.id || state.status === "error")) {
                if (state.status === "error") play(station); else select(station);
            }
        } else if (target.hasAttribute("data-music-retry")) {
            play();
        }
    });
    listen(document, "input", function (event) {
        if (event.target && event.target.matches && event.target.matches("input[data-music-volume]")) setVolume(event.target.value, false);
    });
    listen(document, "change", function (event) {
        if (event.target && event.target.matches && event.target.matches("input[data-music-volume]")) setVolume(event.target.value, true);
    });

    state.volume = safeGetVolume();

    window.sansPostMusic = {
        // Komponent Blazor rysujący stan: od razu dostaje bieżący stan, potem każdą zmianę. Zwraca numer do odpięcia.
        subscribe: function (dotnet) {
            const id = nextId++;
            subscribers.set(id, dotnet);
            debug.subscribers = subscribers.size;
            dotnet.invokeMethodAsync("OnMusicState", snapshot()).catch(function () { });
            return id;
        },
        unsubscribe: function (id) {
            subscribers.delete(id);
            debug.subscribers = subscribers.size;
        },
        state: snapshot
    };
    window.__sansPostMusic = debug;
})();
