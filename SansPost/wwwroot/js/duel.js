// Stół gry na żywo (Sprint 20) — jedyny właściciel połączenia DuelHub (SignalR) w przeglądarce.
// Blazor (GameTable) rysuje stan; ten moduł tylko: łączy się z hubem (cookie sesji — bez tokenów w adresie), przekazuje
// zdarzenia serwera do komponentu, wywołuje metody huba i po ponownym połączeniu prosi o pełny stan (RequestState).
// Czas: serwer podaje, ile zostało (…RemainingMs) — termin lokalny = chwila odbioru + pozostały czas (annotate), więc różnica
// zegarów klienta i serwera nie ma znaczenia. Odliczanie rysuje przeglądarka (bez ticków SignalR co sekundę).
// Biblioteka @microsoft/signalr wczytywana dopiero przy pierwszym otwarciu stołu (sala i wejście jej nie pobierają).
(function () {
    'use strict';

    const EVENTS = ['DuelChallengeReceived', 'DuelChallengeUpdated', 'DuelUpdated', 'RoundStarted', 'RoundResolved', 'PresenceChanged', 'StandingsChanged'];
    const RETRY_MS = [0, 1000, 2000, 5000, 5000, 10000];
    let connection = null;
    let starting = null;
    let library = null;
    let listener = null;
    let keepAlive = false;
    let retryTimer = 0;
    let ticker = 0;

    function loadLibrary() {
        if (window.signalR) return Promise.resolve();
        if (!library) {
            library = new Promise(function (resolve, reject) {
                const script = document.createElement('script');
                script.src = 'lib/signalr/signalr.min.js';
                script.onload = function () { resolve(); };
                script.onerror = function () { library = null; reject(new Error('signalr')); };
                document.head.appendChild(script);
            });
        }
        return library;
    }

    // Terminy lokalne (ms od epoki) obok każdego "pozostało": remainingMs → localDeadline (runda, wyzwanie),
    // roundRemainingMs → roundLocalDeadline (stan pojedynku), graceRemainingMs → graceLocalDeadline (powrót przeciwnika).
    function annotate(value, now, depth) {
        if (!value || typeof value !== 'object' || depth > 4) return value;
        if (Array.isArray(value)) {
            value.forEach(function (item) { annotate(item, now, depth + 1); });
            return value;
        }
        if (typeof value.remainingMs === 'number') value.localDeadline = now + value.remainingMs;
        if (typeof value.roundRemainingMs === 'number') value.roundLocalDeadline = now + value.roundRemainingMs;
        if (typeof value.graceRemainingMs === 'number') value.graceLocalDeadline = now + value.graceRemainingMs;
        Object.keys(value).forEach(function (key) {
            if (value[key] && typeof value[key] === 'object') annotate(value[key], now, depth + 1);
        });
        return value;
    }

    function notify(name, payload) {
        if (!listener) {
            // Stół zamknięty w trakcie pojedynku — połączenie trwa tylko do jego końca (potem gracz znika z listy online).
            const snapshot = payload && (payload.snapshot || payload);
            if (keepAlive && snapshot && snapshot.status === 'finished') stop();
            return;
        }
        listener.invokeMethodAsync('OnHubEvent', name, payload === undefined ? null : annotate(payload, Date.now(), 0)).catch(function () { });
    }

    function status(kind) {
        if (listener) listener.invokeMethodAsync('OnConnectionChanged', kind).catch(function () { });
    }

    async function resume() {
        try {
            notify('State', await connection.invoke('RequestState'));
        } catch (e) {
            // Połączenie zerwało się w trakcie — kolejna próba przyjdzie z reconnect.
        }
    }

    function build() {
        const hub = new window.signalR.HubConnectionBuilder()
            .withUrl('hubs/duel')
            .withAutomaticReconnect(RETRY_MS)
            .configureLogging(window.signalR.LogLevel.None)
            .build();
        // Zgodnie z serwerem (keep-alive 5 s, limit 12 s): zerwane połączenie wykryte po obu stronach w kilka sekund.
        hub.keepAliveIntervalInMilliseconds = 5000;
        hub.serverTimeoutInMilliseconds = 12000;
        EVENTS.forEach(function (name) { hub.on(name, function (payload) { notify(name, payload); }); });
        hub.onreconnecting(function () { status('reconnecting'); });
        hub.onreconnected(function () { status('connected'); resume(); });
        hub.onclose(function () {
            status('disconnected');
            if (listener || keepAlive) scheduleRetry();
        });
        return hub;
    }

    // Po wyczerpaniu automatycznych prób (np. długa przerwa w sieci) — dalsze próby co kilka sekund, dopóki stół jest otwarty.
    function scheduleRetry() {
        clearTimeout(retryTimer);
        retryTimer = setTimeout(async function () {
            if (!listener && !keepAlive) return;
            try {
                await ensureStarted();
                status('connected');
                await resume();
            } catch (e) {
                scheduleRetry();
            }
        }, 4000);
    }

    function ensureStarted() {
        if (connection && connection.state === 'Connected') return Promise.resolve();
        if (starting) return starting;
        starting = (async function () {
            await loadLibrary();
            if (!connection) connection = build();
            if (connection.state === 'Disconnected') await connection.start();
            else if (connection.state !== 'Connected') {
                // Łączy się / wznawia — poczekaj na wynik.
                await new Promise(function (resolve, reject) {
                    const started = Date.now();
                    (function wait() {
                        if (connection.state === 'Connected') resolve();
                        else if (connection.state === 'Disconnected' || Date.now() - started > 15000) reject(new Error('disconnected'));
                        else setTimeout(wait, 100);
                    })();
                });
            }
        })().finally(function () { starting = null; });
        return starting;
    }

    // Odliczania na stronie: [data-deadline] (termin lokalny) → "12 s"; data-urgent w ostatnich 5 s.
    function tick() {
        const now = Date.now();
        document.querySelectorAll('[data-deadline]').forEach(function (el) {
            const deadline = Number(el.getAttribute('data-deadline'));
            if (!deadline) return;
            const seconds = Math.max(0, Math.ceil((deadline - now) / 1000));
            const text = seconds + ' s';
            if (el.textContent !== text) el.textContent = text;
            el.toggleAttribute('data-urgent', seconds <= 5);
        });
    }

    function startTicker() {
        if (!ticker) ticker = setInterval(tick, 250);
        tick();
    }

    function stopTicker() {
        clearInterval(ticker);
        ticker = 0;
    }

    function stop() {
        keepAlive = false;
        clearTimeout(retryTimer);
        stopTicker();
        const current = connection;
        connection = null;
        if (current) current.stop().catch(function () { });
    }

    window.sansPostDuel = {
        // Stół otwarty: komponent słucha zdarzeń; zwraca pełny stan (także wznowienie po odświeżeniu strony).
        attach: async function (dotnet) {
            listener = dotnet;
            keepAlive = false;
            clearTimeout(retryTimer);
            startTicker();
            try {
                await ensureStarted();
                status('connected');
                return annotate(await connection.invoke('RequestState'), Date.now(), 0);
            } catch (e) {
                status('disconnected');
                scheduleRetry();
                return null;
            }
        },

        // Stół zamknięty. Trwający pojedynek: połączenie zostaje (zamknięcie okna to nie rozłączenie), w innym razie — koniec.
        detach: function (keepConnection) {
            listener = null;
            stopTicker();
            if (keepConnection && connection) keepAlive = true;
            else stop();
        },

        invoke: async function (method) {
            const args = Array.prototype.slice.call(arguments, 1);
            try {
                await ensureStarted();
                return annotate(await connection.invoke.apply(connection, [method].concat(args)), Date.now(), 0);
            } catch (e) {
                return { succeeded: false, code: 'connection-lost', message: 'Brak połączenia ze stołem gry — spróbuj za chwilę.', value: null };
            }
        },

        // Po ruchu wybrana karta staje się nieaktywna i przeglądarka gubi fokus (trafia na body) — wtedy, i tylko wtedy,
        // fokus wraca do okna stołu (pierwsza dostępna karta / "GOTOWY" / nagłówek planszy). Świadomego wyboru nie ruszamy.
        refocus: function (selector) {
            const active = document.activeElement;
            if (active && active !== document.body && !active.disabled && active.isConnected) return;
            const dialog = document.querySelector('dialog[open]');
            const target = dialog && dialog.querySelector(selector);
            if (target) target.focus({ preventScroll: true });
        },

        // Diagnostyka (testy E2E, pomiary): stan połączenia bez dostępu do wnętrza biblioteki.
        state: function () {
            return connection ? connection.state : 'None';
        }
    };
})();
